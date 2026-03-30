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

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = @"
                    CREATE SEQUENCE IF NOT EXISTS event_seq START 1;

                    CREATE TABLE IF NOT EXISTS events (
                        id BIGINT PRIMARY KEY,
                        timestamp TIMESTAMPTZ NOT NULL,
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
                        timestamp TIMESTAMPTZ NOT NULL,
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
                        bucket TIMESTAMPTZ NOT NULL,
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
                        bucket TIMESTAMPTZ NOT NULL,
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
                var rows = new List<string>();
                var ts = snapshot.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var groupName = EscapeSql(snapshot.Name);
                var groupType = EscapeSql(snapshot.GroupType.ToString());

                var replicas = CollectReplicas(snapshot);
                foreach (var (replica, dbs) in replicas)
                {
                    var replicaName = EscapeSql(dbs.ReplicaServerName);
                    var dbName = EscapeSql(dbs.DatabaseName);
                    var role = EscapeSql(replica.Role.ToString());
                    var syncState = EscapeSql(dbs.SynchronizationState.ToString());
                    var connState = EscapeSql(replica.ConnectedState.ToString());
                    var availMode = EscapeSql(dbs.AvailabilityMode.ToString());

                    rows.Add(string.Format(CultureInfo.InvariantCulture,
                        "('{0}', '{1}', '{2}', '{3}', '{4}', '{5}', '{6}', '{7}', '{8}', {9}, {10}, {11}, {12}, {13}, {14}, {15}, {16}, {17})",
                        ts, groupName, groupType, replicaName, dbName,
                        role, syncState, connState, availMode,
                        dbs.LastHardenedLsn.ToString(CultureInfo.InvariantCulture),
                        dbs.LastCommitLsn.ToString(CultureInfo.InvariantCulture),
                        dbs.LogSendQueueSizeKb.ToString(CultureInfo.InvariantCulture),
                        dbs.RedoQueueSizeKb.ToString(CultureInfo.InvariantCulture),
                        dbs.LogSendRateKbPerSec.ToString(CultureInfo.InvariantCulture),
                        dbs.RedoRateKbPerSec.ToString(CultureInfo.InvariantCulture),
                        dbs.LogBlockDifference.ToString(CultureInfo.InvariantCulture),
                        dbs.SecondaryLagSeconds.ToString(CultureInfo.InvariantCulture),
                        dbs.IsSuspended ? "true" : "false"));
                }

                if (rows.Count == 0) return;

                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "INSERT INTO snapshots VALUES " + string.Join(",\n", rows);
                cmd.ExecuteNonQuery();
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

                var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
                cmd.CommandText = $@"
                    SELECT id, timestamp, alert_type, group_name, replica_name, database_name, message, severity, email_sent, syslog_sent
                    FROM events
                    {whereClause}
                    ORDER BY timestamp DESC
                    LIMIT {limit}
                ";

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
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = "DELETE FROM events WHERE timestamp < $cutoff";
                    cmd.Parameters.Add(new DuckDBParameter("cutoff", DateTime.UtcNow.AddDays(-maxAgeDays.Value)));
                    totalDeleted += cmd.ExecuteNonQuery();
                }

                if (maxRecords.HasValue)
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = @"
                        DELETE FROM events 
                        WHERE id NOT IN (
                            SELECT id FROM events ORDER BY timestamp DESC LIMIT $max_records
                        )";
                    cmd.Parameters.Add(new DuckDBParameter("max_records", maxRecords.Value));
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

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
