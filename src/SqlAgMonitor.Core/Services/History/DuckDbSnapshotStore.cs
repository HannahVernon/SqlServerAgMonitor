using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Handles snapshot recording, querying, and filter retrieval against DuckDB.
/// </summary>
internal sealed class DuckDbSnapshotStore
{
    private readonly DuckDbConnectionManager _db;
    private readonly ILogger _logger;

    public DuckDbSnapshotStore(DuckDbConnectionManager db, ILogger<DuckDbSnapshotStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordSnapshotAsync(MonitoredGroupSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return;
        try
        {
            await _db.ExecuteAsync(conn =>
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

                using var cmd = conn.CreateCommand();
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
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record snapshot for group {Group}.", snapshot.Name);
        }
    }

    public async Task<SnapshotTier> ResolveTierAsync(
        DateTimeOffset since, DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return SnapshotTier.Raw;

        var range = until - since;
        SnapshotTier preferred;
        if (range.TotalHours <= 48)
            preferred = SnapshotTier.Raw;
        else if (range.TotalDays <= 90)
            preferred = SnapshotTier.Hourly;
        else
            preferred = SnapshotTier.Daily;

        try
        {
            return await _db.ExecuteAsync(conn =>
            {
                var tier = preferred;
                while (true)
                {
                    var (table, timeCol) = tier switch
                    {
                        SnapshotTier.Raw => ("snapshots", "timestamp"),
                        SnapshotTier.Hourly => ("snapshot_hourly", "bucket"),
                        _ => ("snapshot_daily", "bucket")
                    };

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(1) FROM {table} WHERE {timeCol} >= $since AND {timeCol} < $until";
                    cmd.Parameters.Add(new DuckDBParameter("since", since.UtcDateTime));
                    cmd.Parameters.Add(new DuckDBParameter("until", until.UtcDateTime));

                    var count = Convert.ToInt64(cmd.ExecuteScalar());
                    if (count > 0 || tier == SnapshotTier.Raw)
                        return tier;

                    _logger.LogInformation(
                        "No data in {Tier} tier for range {Since} to {Until}; falling back.",
                        tier, since, until);

                    tier = tier == SnapshotTier.Daily ? SnapshotTier.Hourly : SnapshotTier.Raw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve snapshot tier; defaulting to raw.");
            return SnapshotTier.Raw;
        }
    }

    public async Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(
        DateTimeOffset since, DateTimeOffset until,
        string? groupName = null, string? replicaName = null, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return Array.Empty<SnapshotDataPoint>();
        try
        {
            var tier = await ResolveTierAsync(since, until, cancellationToken);

            return await _db.ExecuteAsync(conn =>
            {
                string tableName = tier switch
                {
                    SnapshotTier.Raw => "snapshots",
                    SnapshotTier.Hourly => "snapshot_hourly",
                    _ => "snapshot_daily"
                };

                using var cmd = conn.CreateCommand();
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

                // WHERE clause assembled from code-controlled static fragments with
                // DuckDB parameter placeholders. No user input is interpolated.
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
                return (IReadOnlyList<SnapshotDataPoint>)results;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read snapshot data from DuckDB.");
            return Array.Empty<SnapshotDataPoint>();
        }
    }

    public async Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(
        string? groupName = null, string? replicaName = null,
        CancellationToken cancellationToken = default)
    {
        if (!_db.IsInitialized) return new SnapshotFilterOptions();
        try
        {
            return await _db.ExecuteAsync(conn =>
            {
                // Groups — always show all
                using var cmd = conn.CreateCommand();
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

                // Replicas — filtered by selected group
                using var cmd2 = conn.CreateCommand();
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

                // Databases — filtered by selected group and replica
                using var cmd3 = conn.CreateCommand();
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
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read snapshot filters from DuckDB.");
            return new SnapshotFilterOptions();
        }
    }

    /// <summary>
    /// Collects all (ReplicaInfo, DatabaseReplicaState) pairs from a snapshot,
    /// handling both AG and DAG group types.
    /// </summary>
    internal static List<(ReplicaInfo Replica, DatabaseReplicaState Dbs)> CollectReplicas(MonitoredGroupSnapshot snapshot)
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
