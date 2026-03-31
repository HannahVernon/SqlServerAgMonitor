using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// DuckDB is an embedded, in-process database. Its ADO.NET provider is entirely synchronous —
/// OpenAsync/ExecuteReaderAsync etc. just call their sync counterparts. All DuckDB I/O is
/// offloaded to thread-pool threads via Task.Run to avoid blocking the UI thread.
/// 
/// Connections to an embedded database cannot spontaneously "drop", so no reconnection logic
/// is needed. The connection stays open for the lifetime of the service.
/// </summary>
public class DuckDbEventHistoryService : IEventHistoryService
{
    private readonly ILogger<DuckDbEventHistoryService> _logger;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private DuckDBConnection? _connection;
    private bool _initialized;
    private bool _disposed;

    public DuckDbEventHistoryService(ILogger<DuckDbEventHistoryService> logger, string? dataDirectory = null)
    {
        _logger = logger;
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SqlAgMonitor", "data");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "events.duckdb");
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            // DuckDB Open() is synchronous — run on thread pool
            await Task.Run(() =>
            {
                _connection = new DuckDBConnection(_connectionString);
                _connection.Open();

                // Force UTC timezone to prevent TIMESTAMPTZ double-offset conversion.
                // DuckDB defaults to the OS timezone, which causes inserted UTC strings
                // to be interpreted as local time and shifted again.
                using var tzCmd = _connection.CreateCommand();
                tzCmd.CommandText = "SET TimeZone = 'UTC'";
                tzCmd.ExecuteNonQuery();

                // One-time schema migration: drop tables that used TIMESTAMPTZ
                // (which stored timestamps with a double timezone offset).
                // Recreate with plain TIMESTAMP to avoid any timezone surprises.
                using var migCmd = _connection.CreateCommand();
                migCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS _schema_meta (key VARCHAR PRIMARY KEY, value VARCHAR);
                ";
                migCmd.ExecuteNonQuery();

                using var verCmd = _connection.CreateCommand();
                verCmd.CommandText = "SELECT value FROM _schema_meta WHERE key = 'schema_version'";
                var versionObj = verCmd.ExecuteScalar();
                var version = versionObj != null && versionObj != DBNull.Value ? int.Parse((string)versionObj, CultureInfo.InvariantCulture) : 0;

                if (version < 2)
                {
                    _logger.LogInformation("Migrating DuckDB schema from version {Old} to 2 (TIMESTAMPTZ → TIMESTAMP).", version);
                    using var dropCmd = _connection.CreateCommand();
                    dropCmd.CommandText = @"
                        DROP TABLE IF EXISTS events;
                        DROP TABLE IF EXISTS snapshots;
                        DROP TABLE IF EXISTS snapshot_hourly;
                        DROP TABLE IF EXISTS snapshot_daily;
                        DROP SEQUENCE IF EXISTS event_seq;
                    ";
                    dropCmd.ExecuteNonQuery();

                    using var setVerCmd = _connection.CreateCommand();
                    setVerCmd.CommandText = @"
                        INSERT INTO _schema_meta (key, value) VALUES ('schema_version', '2')
                        ON CONFLICT (key) DO UPDATE SET value = '2';
                    ";
                    setVerCmd.ExecuteNonQuery();
                }

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE SEQUENCE IF NOT EXISTS event_seq START 1;

                    CREATE TABLE IF NOT EXISTS events (
                        id BIGINT PRIMARY KEY,
                        timestamp TIMESTAMP NOT NULL,
                        alert_type VARCHAR NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR,
                        database_name VARCHAR,
                        message VARCHAR NOT NULL,
                        severity VARCHAR NOT NULL,
                        email_sent BOOLEAN DEFAULT FALSE,
                        syslog_sent BOOLEAN DEFAULT FALSE
                    );

                    CREATE TABLE IF NOT EXISTS snapshots (
                        timestamp TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        group_type VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        role VARCHAR NOT NULL,
                        sync_state VARCHAR NOT NULL,
                        connected_state VARCHAR NOT NULL,
                        availability_mode VARCHAR NOT NULL,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        log_send_queue_kb BIGINT,
                        redo_queue_kb BIGINT,
                        log_send_rate_kb_per_sec BIGINT,
                        redo_rate_kb_per_sec BIGINT,
                        log_block_difference DECIMAL(25, 0),
                        secondary_lag_seconds BIGINT,
                        is_suspended BOOLEAN DEFAULT FALSE
                    );

                    CREATE TABLE IF NOT EXISTS snapshot_hourly (
                        bucket TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        sample_count INTEGER NOT NULL,
                        log_send_queue_kb_min BIGINT,
                        log_send_queue_kb_max BIGINT,
                        log_send_queue_kb_avg DOUBLE,
                        redo_queue_kb_min BIGINT,
                        redo_queue_kb_max BIGINT,
                        redo_queue_kb_avg DOUBLE,
                        log_send_rate_min BIGINT,
                        log_send_rate_max BIGINT,
                        log_send_rate_avg DOUBLE,
                        redo_rate_min BIGINT,
                        redo_rate_max BIGINT,
                        redo_rate_avg DOUBLE,
                        log_block_diff_min DECIMAL(25, 0),
                        log_block_diff_max DECIMAL(25, 0),
                        log_block_diff_avg DOUBLE,
                        secondary_lag_min BIGINT,
                        secondary_lag_max BIGINT,
                        secondary_lag_avg DOUBLE,
                        last_role VARCHAR,
                        last_sync_state VARCHAR,
                        any_suspended BOOLEAN,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        PRIMARY KEY (bucket, group_name, replica_name, database_name)
                    );

                    CREATE TABLE IF NOT EXISTS snapshot_daily (
                        bucket TIMESTAMP NOT NULL,
                        group_name VARCHAR NOT NULL,
                        replica_name VARCHAR NOT NULL,
                        database_name VARCHAR NOT NULL,
                        sample_count INTEGER NOT NULL,
                        log_send_queue_kb_min BIGINT,
                        log_send_queue_kb_max BIGINT,
                        log_send_queue_kb_avg DOUBLE,
                        redo_queue_kb_min BIGINT,
                        redo_queue_kb_max BIGINT,
                        redo_queue_kb_avg DOUBLE,
                        log_send_rate_min BIGINT,
                        log_send_rate_max BIGINT,
                        log_send_rate_avg DOUBLE,
                        redo_rate_min BIGINT,
                        redo_rate_max BIGINT,
                        redo_rate_avg DOUBLE,
                        log_block_diff_min DECIMAL(25, 0),
                        log_block_diff_max DECIMAL(25, 0),
                        log_block_diff_avg DOUBLE,
                        secondary_lag_min BIGINT,
                        secondary_lag_max BIGINT,
                        secondary_lag_avg DOUBLE,
                        last_role VARCHAR,
                        last_sync_state VARCHAR,
                        any_suspended BOOLEAN,
                        last_hardened_lsn DECIMAL(25, 0),
                        last_commit_lsn DECIMAL(25, 0),
                        PRIMARY KEY (bucket, group_name, replica_name, database_name)
                    );
                ";
                cmd.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.LogInformation("DuckDB event history initialized at {Path}.", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize DuckDB at {Path}.", _dbPath);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO events (id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent)
                    VALUES (nextval('event_seq'), $timestamp, $alert_type, $group_name, $replica_name, $database_name, $message, $severity, $email_sent, $syslog_sent)
                ";
                cmd.Parameters.Add(new DuckDBParameter("timestamp", alertEvent.Timestamp.UtcDateTime));
                cmd.Parameters.Add(new DuckDBParameter("alert_type", alertEvent.AlertType.ToString()));
                cmd.Parameters.Add(new DuckDBParameter("group_name", alertEvent.GroupName));
                cmd.Parameters.Add(new DuckDBParameter("replica_name", (object?)alertEvent.ReplicaName ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("database_name", (object?)alertEvent.DatabaseName ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter("message", alertEvent.Message));
                cmd.Parameters.Add(new DuckDBParameter("severity", alertEvent.Severity.ToString()));
                cmd.Parameters.Add(new DuckDBParameter("email_sent", alertEvent.EmailSent));
                cmd.Parameters.Add(new DuckDBParameter("syslog_sent", alertEvent.SyslogSent));
                cmd.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record event for group {Group}.", alertEvent.GroupName);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task RecordSnapshotAsync(MonitoredGroupSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var replicas = CollectReplicas(snapshot);

                if (replicas.Count == 0)
                {
                    _logger.LogDebug("No replica/database pairs found in snapshot for group {Group} (AgInfo={HasAg}, DagInfo={HasDag}).",
                        snapshot.Name, snapshot.AgInfo != null, snapshot.DagInfo != null);
                    return;
                }

                var ts = snapshot.Timestamp.UtcDateTime;
                var groupName = snapshot.Name;
                var groupType = snapshot.GroupType.ToString();

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO snapshots VALUES (
                        $ts, $group_name, $group_type, $replica_name, $database_name,
                        $role, $sync_state, $connected_state, $availability_mode,
                        $last_hardened_lsn, $last_commit_lsn,
                        $log_send_queue_kb, $redo_queue_kb,
                        $log_send_rate, $redo_rate,
                        $log_block_diff, $secondary_lag, $is_suspended)";

                var pTs = new DuckDBParameter("ts", ts);
                var pGroupName = new DuckDBParameter("group_name", groupName);
                var pGroupType = new DuckDBParameter("group_type", groupType);
                var pReplicaName = new DuckDBParameter("replica_name", "");
                var pDbName = new DuckDBParameter("database_name", "");
                var pRole = new DuckDBParameter("role", "");
                var pSyncState = new DuckDBParameter("sync_state", "");
                var pConnState = new DuckDBParameter("connected_state", "");
                var pAvailMode = new DuckDBParameter("availability_mode", "");
                var pHardenedLsn = new DuckDBParameter("last_hardened_lsn", 0m);
                var pCommitLsn = new DuckDBParameter("last_commit_lsn", 0m);
                var pSendQueue = new DuckDBParameter("log_send_queue_kb", 0L);
                var pRedoQueue = new DuckDBParameter("redo_queue_kb", 0L);
                var pSendRate = new DuckDBParameter("log_send_rate", 0L);
                var pRedoRate = new DuckDBParameter("redo_rate", 0L);
                var pLogBlockDiff = new DuckDBParameter("log_block_diff", 0m);
                var pSecondaryLag = new DuckDBParameter("secondary_lag", 0L);
                var pSuspended = new DuckDBParameter("is_suspended", false);

                cmd.Parameters.Add(pTs);
                cmd.Parameters.Add(pGroupName);
                cmd.Parameters.Add(pGroupType);
                cmd.Parameters.Add(pReplicaName);
                cmd.Parameters.Add(pDbName);
                cmd.Parameters.Add(pRole);
                cmd.Parameters.Add(pSyncState);
                cmd.Parameters.Add(pConnState);
                cmd.Parameters.Add(pAvailMode);
                cmd.Parameters.Add(pHardenedLsn);
                cmd.Parameters.Add(pCommitLsn);
                cmd.Parameters.Add(pSendQueue);
                cmd.Parameters.Add(pRedoQueue);
                cmd.Parameters.Add(pSendRate);
                cmd.Parameters.Add(pRedoRate);
                cmd.Parameters.Add(pLogBlockDiff);
                cmd.Parameters.Add(pSecondaryLag);
                cmd.Parameters.Add(pSuspended);

                foreach (var (replica, dbs) in replicas)
                {
                    pReplicaName.Value = dbs.ReplicaServerName;
                    pDbName.Value = dbs.DatabaseName;
                    pRole.Value = replica.Role.ToString();
                    pSyncState.Value = dbs.SynchronizationState.ToString();
                    pConnState.Value = replica.ConnectedState.ToString();
                    pAvailMode.Value = dbs.AvailabilityMode.ToString();
                    pHardenedLsn.Value = dbs.LastHardenedLsn;
                    pCommitLsn.Value = dbs.LastCommitLsn;
                    pSendQueue.Value = dbs.LogSendQueueSizeKb;
                    pRedoQueue.Value = dbs.RedoQueueSizeKb;
                    pSendRate.Value = dbs.LogSendRateKbPerSec;
                    pRedoRate.Value = dbs.RedoRateKbPerSec;
                    pLogBlockDiff.Value = dbs.LogBlockDifference;
                    pSecondaryLag.Value = dbs.SecondaryLagSeconds;
                    pSuspended.Value = dbs.IsSuspended;

                    cmd.ExecuteNonQuery();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record snapshot for group {Group}.", snapshot.Name);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task SummarizeSnapshotsAsync(int rawRetentionHours = 48, int hourlyRetentionDays = 90, int dailyRetentionDays = 730, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;

        // Clamp retention values to sane minimums to prevent misconfigured or
        // tampered config.json from deleting all historical data.
        rawRetentionHours = Math.Max(1, rawRetentionHours);
        hourlyRetentionDays = Math.Max(1, hourlyRetentionDays);
        dailyRetentionDays = Math.Max(1, dailyRetentionDays);

        // Each step acquires/releases _opLock independently so that high-frequency
        // RecordSnapshotAsync calls can interleave between steps rather than queuing
        // behind a single long-held lock.

        // Step 1 — Generate hourly summaries from raw snapshots
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO snapshot_hourly
                    SELECT 
                        date_trunc('hour', timestamp) AS bucket,
                        group_name, replica_name, database_name,
                        COUNT(*)::INTEGER AS sample_count,
                        MIN(log_send_queue_kb), MAX(log_send_queue_kb), AVG(log_send_queue_kb)::DOUBLE,
                        MIN(redo_queue_kb), MAX(redo_queue_kb), AVG(redo_queue_kb)::DOUBLE,
                        MIN(log_send_rate_kb_per_sec), MAX(log_send_rate_kb_per_sec), AVG(log_send_rate_kb_per_sec)::DOUBLE,
                        MIN(redo_rate_kb_per_sec), MAX(redo_rate_kb_per_sec), AVG(redo_rate_kb_per_sec)::DOUBLE,
                        MIN(log_block_difference), MAX(log_block_difference), AVG(log_block_difference)::DOUBLE,
                        MIN(secondary_lag_seconds), MAX(secondary_lag_seconds), AVG(secondary_lag_seconds)::DOUBLE,
                        LAST(role ORDER BY timestamp),
                        LAST(sync_state ORDER BY timestamp),
                        BOOL_OR(is_suspended),
                        LAST(last_hardened_lsn ORDER BY timestamp),
                        LAST(last_commit_lsn ORDER BY timestamp)
                    FROM snapshots
                    WHERE date_trunc('hour', timestamp) < date_trunc('hour', current_timestamp)
                      AND date_trunc('hour', timestamp) NOT IN (SELECT DISTINCT bucket FROM snapshot_hourly)
                    GROUP BY date_trunc('hour', timestamp), group_name, replica_name, database_name
                ";
                var inserted = cmd.ExecuteNonQuery();
                if (inserted > 0)
                    _logger.LogInformation("Inserted {Count} hourly snapshot summaries.", inserted);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate hourly snapshot summaries.");
        }
        finally
        {
            _opLock.Release();
        }

        // Step 2 — Generate daily summaries from hourly data
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO snapshot_daily
                    SELECT
                        date_trunc('day', bucket) AS bucket,
                        group_name, replica_name, database_name,
                        SUM(sample_count)::INTEGER,
                        MIN(log_send_queue_kb_min), MAX(log_send_queue_kb_max), 
                        SUM(log_send_queue_kb_avg * sample_count) / SUM(sample_count),
                        MIN(redo_queue_kb_min), MAX(redo_queue_kb_max),
                        SUM(redo_queue_kb_avg * sample_count) / SUM(sample_count),
                        MIN(log_send_rate_min), MAX(log_send_rate_max),
                        SUM(log_send_rate_avg * sample_count) / SUM(sample_count),
                        MIN(redo_rate_min), MAX(redo_rate_max),
                        SUM(redo_rate_avg * sample_count) / SUM(sample_count),
                        MIN(log_block_diff_min), MAX(log_block_diff_max),
                        SUM(log_block_diff_avg * sample_count) / SUM(sample_count),
                        MIN(secondary_lag_min), MAX(secondary_lag_max),
                        SUM(secondary_lag_avg * sample_count) / SUM(sample_count),
                        LAST(last_role ORDER BY bucket),
                        LAST(last_sync_state ORDER BY bucket),
                        BOOL_OR(any_suspended),
                        LAST(last_hardened_lsn ORDER BY bucket),
                        LAST(last_commit_lsn ORDER BY bucket)
                    FROM snapshot_hourly
                    WHERE date_trunc('day', bucket) < date_trunc('day', current_timestamp)
                      AND date_trunc('day', bucket) NOT IN (SELECT DISTINCT bucket FROM snapshot_daily)
                    GROUP BY date_trunc('day', bucket), group_name, replica_name, database_name
                ";
                var inserted = cmd.ExecuteNonQuery();
                if (inserted > 0)
                    _logger.LogInformation("Inserted {Count} daily snapshot summaries.", inserted);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate daily snapshot summaries.");
        }
        finally
        {
            _opLock.Release();
        }

        // Step 3 — Prune old data
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                // Retention values are integers from application config — safe to format into
                // DuckDB INTERVAL literals. DuckDB does not support parameterized INTERVAL syntax.
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = string.Format(CultureInfo.InvariantCulture, @"
                    DELETE FROM snapshots WHERE timestamp < current_timestamp - INTERVAL '{0} hours';
                    DELETE FROM snapshot_hourly WHERE bucket < current_timestamp - INTERVAL '{1} days';
                    DELETE FROM snapshot_daily WHERE bucket < current_timestamp - INTERVAL '{2} days';
                ", rawRetentionHours, hourlyRetentionDays, dailyRetentionDays);
                cmd.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Snapshot summarization and pruning complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune old snapshots.");
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return Array.Empty<AlertEvent>();
        limit = Math.Max(1, limit);
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var events = new List<AlertEvent>();
                using var cmd = _connection!.CreateCommand();
                var where = new List<string>();
                if (groupName != null)
                {
                    where.Add("group_name = $group_name");
                    cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                if (since != null)
                {
                    where.Add("timestamp >= $since");
                    cmd.Parameters.Add(new DuckDBParameter("since", since.Value.UtcDateTime));
                }

                // WHERE clause is assembled from code-controlled static fragments containing
                // DuckDB parameter placeholders (e.g. "group_name = $group_name"). No user input
                // is interpolated — only the structural SQL keywords (WHERE, AND) are joined in.
                var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                cmd.CommandText = @"
                    SELECT id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent
                    FROM events
                    " + whereClause + @"
                    ORDER BY timestamp DESC
                    LIMIT $limit
                ";
                cmd.Parameters.Add(new DuckDBParameter("limit", limit));

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(new AlertEvent
                    {
                        Id = reader.GetInt64(0),
                        Timestamp = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                        AlertType = Enum.TryParse<AlertType>(reader.GetString(2), out var at) ? at : AlertType.Unknown,
                        GroupName = reader.GetString(3),
                        ReplicaName = reader.IsDBNull(4) ? null : reader.GetString(4),
                        DatabaseName = reader.IsDBNull(5) ? null : reader.GetString(5),
                        Message = reader.GetString(6),
                        Severity = Enum.TryParse<AlertSeverity>(reader.GetString(7), out var sev) ? sev : AlertSeverity.Information,
                        EmailSent = reader.GetBoolean(8),
                        SyslogSent = reader.GetBoolean(9)
                    });
                }
                return events;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read events from DuckDB.");
            return Array.Empty<AlertEvent>();
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return 0;
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = _connection!.CreateCommand();
                if (groupName != null)
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events WHERE group_name = $group_name";
                    cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                else
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM events";
                }
                return Convert.ToInt64(cmd.ExecuteScalar());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get event count from DuckDB.");
            return 0;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return 0;
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                long totalDeleted = 0;
                if (maxAgeDays.HasValue)
                {
                    var clampedDays = Math.Max(1, maxAgeDays.Value);
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = "DELETE FROM events WHERE timestamp < $cutoff";
                    cmd.Parameters.Add(new DuckDBParameter("cutoff", DateTime.UtcNow.AddDays(-clampedDays)));
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (maxRecords.HasValue)
                {
                    var clampedRecords = Math.Max(1, maxRecords.Value);
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        DELETE FROM events 
                        WHERE id NOT IN (
                            SELECT id FROM events ORDER BY timestamp DESC LIMIT $max_records
                        )";
                    cmd.Parameters.Add(new DuckDBParameter("max_records", clampedRecords));
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (totalDeleted > 0)
                    _logger.LogInformation("Pruned {Count} old events from history.", totalDeleted);

                return totalDeleted;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune events from DuckDB.");
            return 0;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(
        DateTimeOffset since, DateTimeOffset until,
        string? groupName = null, string? replicaName = null, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_initialized) return Array.Empty<SnapshotDataPoint>();
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                // Auto-select snapshot tier based on query range to balance detail vs performance.
                // Thresholds align with retention defaults: raw kept 48h, hourly kept 90d.
                var range = until - since;
                SnapshotTier tier;
                string tableName;
                if (range.TotalHours <= 48) // Within raw retention window
                {
                    tier = SnapshotTier.Raw;
                    tableName = "snapshots";
                }
                else if (range.TotalDays <= 90) // Within hourly retention window
                {
                    tier = SnapshotTier.Hourly;
                    tableName = "snapshot_hourly";
                }
                else
                {
                    tier = SnapshotTier.Daily;
                    tableName = "snapshot_daily";
                }

                using var cmd = _connection!.CreateCommand();
                var filters = new List<string>();

                string timeColumn = tier == SnapshotTier.Raw ? "timestamp" : "bucket";

                filters.Add($"{timeColumn} >= $since");
                cmd.Parameters.Add(new DuckDBParameter("since", since.UtcDateTime));
                filters.Add($"{timeColumn} < $until");
                cmd.Parameters.Add(new DuckDBParameter("until", until.UtcDateTime));

                if (groupName != null)
                {
                    filters.Add("group_name = $group_name");
                    cmd.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                if (replicaName != null)
                {
                    filters.Add("replica_name = $replica_name");
                    cmd.Parameters.Add(new DuckDBParameter("replica_name", replicaName));
                }
                if (databaseName != null)
                {
                    filters.Add("database_name = $database_name");
                    cmd.Parameters.Add(new DuckDBParameter("database_name", databaseName));
                }

                // WHERE clause is assembled from code-controlled static fragments containing
                // DuckDB parameter placeholders (e.g. "group_name = $group_name"). The table name
                // and time column are selected by the tier enum, not user input. No user-supplied
                // values are interpolated into the SQL structure.
                var whereClause = "WHERE " + string.Join(" AND ", filters);

                if (tier == SnapshotTier.Raw)
                {
                    cmd.CommandText = $@"
                        SELECT timestamp, group_name, replica_name, database_name,
                            1 AS sample_count,
                            log_send_queue_kb, log_send_queue_kb, log_send_queue_kb::DOUBLE,
                            redo_queue_kb, redo_queue_kb, redo_queue_kb::DOUBLE,
                            log_send_rate_kb_per_sec, log_send_rate_kb_per_sec, log_send_rate_kb_per_sec::DOUBLE,
                            redo_rate_kb_per_sec, redo_rate_kb_per_sec, redo_rate_kb_per_sec::DOUBLE,
                            log_block_difference, log_block_difference, log_block_difference::DOUBLE,
                            secondary_lag_seconds, secondary_lag_seconds, secondary_lag_seconds::DOUBLE,
                            role, sync_state, is_suspended,
                            last_hardened_lsn, last_commit_lsn
                        FROM snapshots
                        {whereClause}
                        ORDER BY timestamp";
                }
                else
                {
                    cmd.CommandText = $@"
                        SELECT bucket, group_name, replica_name, database_name,
                            sample_count,
                            log_send_queue_kb_min, log_send_queue_kb_max, log_send_queue_kb_avg,
                            redo_queue_kb_min, redo_queue_kb_max, redo_queue_kb_avg,
                            log_send_rate_min, log_send_rate_max, log_send_rate_avg,
                            redo_rate_min, redo_rate_max, redo_rate_avg,
                            log_block_diff_min, log_block_diff_max, log_block_diff_avg,
                            secondary_lag_min, secondary_lag_max, secondary_lag_avg,
                            last_role, last_sync_state, any_suspended,
                            last_hardened_lsn, last_commit_lsn
                        FROM {tableName}
                        {whereClause}
                        ORDER BY bucket";
                }

                var results = new List<SnapshotDataPoint>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new SnapshotDataPoint
                    {
                        Timestamp = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero),
                        GroupName = reader.GetString(1),
                        ReplicaName = reader.GetString(2),
                        DatabaseName = reader.GetString(3),
                        SampleCount = reader.GetInt32(4),
                        LogSendQueueKbMin = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                        LogSendQueueKbMax = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                        LogSendQueueKbAvg = reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                        RedoQueueKbMin = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                        RedoQueueKbMax = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                        RedoQueueKbAvg = reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                        LogSendRateMin = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                        LogSendRateMax = reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                        LogSendRateAvg = reader.IsDBNull(13) ? 0 : reader.GetDouble(13),
                        RedoRateMin = reader.IsDBNull(14) ? 0 : reader.GetInt64(14),
                        RedoRateMax = reader.IsDBNull(15) ? 0 : reader.GetInt64(15),
                        RedoRateAvg = reader.IsDBNull(16) ? 0 : reader.GetDouble(16),
                        LogBlockDiffMin = reader.IsDBNull(17) ? 0 : reader.GetDecimal(17),
                        LogBlockDiffMax = reader.IsDBNull(18) ? 0 : reader.GetDecimal(18),
                        LogBlockDiffAvg = reader.IsDBNull(19) ? 0 : reader.GetDouble(19),
                        SecondaryLagMin = reader.IsDBNull(20) ? 0 : reader.GetInt64(20),
                        SecondaryLagMax = reader.IsDBNull(21) ? 0 : reader.GetInt64(21),
                        SecondaryLagAvg = reader.IsDBNull(22) ? 0 : reader.GetDouble(22),
                        Role = reader.IsDBNull(23) ? string.Empty : reader.GetString(23),
                        SyncState = reader.IsDBNull(24) ? string.Empty : reader.GetString(24),
                        AnySuspended = !reader.IsDBNull(25) && reader.GetBoolean(25),
                        LastHardenedLsn = reader.IsDBNull(26) ? 0 : reader.GetDecimal(26),
                        LastCommitLsn = reader.IsDBNull(27) ? 0 : reader.GetDecimal(27),
                        Tier = tier
                    });
                }
                return results;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read snapshot data from DuckDB.");
            return Array.Empty<SnapshotDataPoint>();
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(string? groupName = null, string? replicaName = null, CancellationToken cancellationToken = default)
    {
        if (!_initialized) return new SnapshotFilterOptions();
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                // Groups are never filtered — always show all
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = @"
                    SELECT DISTINCT group_name FROM (
                        SELECT group_name FROM snapshots
                        UNION SELECT group_name FROM snapshot_hourly
                        UNION SELECT group_name FROM snapshot_daily
                    ) ORDER BY group_name";
                var groups = new List<string>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        groups.Add(reader.GetString(0));
                }

                // Replicas filtered by selected group.
                // WHERE clause uses a static fragment with a DuckDB parameter placeholder.
                using var cmd2 = _connection!.CreateCommand();
                var replicaWhere = "";
                if (groupName != null)
                {
                    replicaWhere = " WHERE group_name = $group_name";
                    cmd2.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                cmd2.CommandText = $@"
                    SELECT DISTINCT replica_name FROM (
                        SELECT group_name, replica_name FROM snapshots
                        UNION SELECT group_name, replica_name FROM snapshot_hourly
                        UNION SELECT group_name, replica_name FROM snapshot_daily
                    ){replicaWhere} ORDER BY replica_name";
                var replicas = new List<string>();
                using (var reader2 = cmd2.ExecuteReader())
                {
                    while (reader2.Read())
                        replicas.Add(reader2.GetString(0));
                }

                // Databases filtered by selected group and replica.
                // WHERE clause assembled from static fragments with DuckDB parameter placeholders.
                using var cmd3 = _connection!.CreateCommand();
                var dbFilters = new List<string>();
                if (groupName != null)
                {
                    dbFilters.Add("group_name = $group_name");
                    cmd3.Parameters.Add(new DuckDBParameter("group_name", groupName));
                }
                if (replicaName != null)
                {
                    dbFilters.Add("replica_name = $replica_name");
                    cmd3.Parameters.Add(new DuckDBParameter("replica_name", replicaName));
                }
                var dbWhere = dbFilters.Count > 0 ? " WHERE " + string.Join(" AND ", dbFilters) : "";
                cmd3.CommandText = $@"
                    SELECT DISTINCT database_name FROM (
                        SELECT group_name, replica_name, database_name FROM snapshots
                        UNION SELECT group_name, replica_name, database_name FROM snapshot_hourly
                        UNION SELECT group_name, replica_name, database_name FROM snapshot_daily
                    ){dbWhere} ORDER BY database_name";
                var databases = new List<string>();
                using (var reader3 = cmd3.ExecuteReader())
                {
                    while (reader3.Read())
                        databases.Add(reader3.GetString(0));
                }

                return new SnapshotFilterOptions
                {
                    GroupNames = groups,
                    ReplicaNames = replicas,
                    DatabaseNames = databases
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read snapshot filters from DuckDB.");
            return new SnapshotFilterOptions();
        }
        finally
        {
            _opLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_connection != null)
            {
                try { _connection.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Error disposing DuckDB connection."); }
            }
            _initLock.Dispose();
            _opLock.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Collects all (ReplicaInfo, DatabaseReplicaState) pairs from a snapshot,
    /// handling both AG and DAG group types.
    /// </summary>
    private static List<(ReplicaInfo Replica, DatabaseReplicaState Dbs)> CollectReplicas(MonitoredGroupSnapshot snapshot)
    {
        var result = new List<(ReplicaInfo, DatabaseReplicaState)>();

        if (snapshot.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup && snapshot.DagInfo != null)
        {
            foreach (var member in snapshot.DagInfo.Members)
            {
                if (member.LocalAgInfo == null) continue;
                foreach (var replica in member.LocalAgInfo.Replicas)
                    foreach (var dbs in replica.DatabaseStates)
                        result.Add((replica, dbs));
            }
        }
        else if (snapshot.AgInfo != null)
        {
            foreach (var replica in snapshot.AgInfo.Replicas)
                foreach (var dbs in replica.DatabaseStates)
                    result.Add((replica, dbs));
        }

        return result;
    }

}
