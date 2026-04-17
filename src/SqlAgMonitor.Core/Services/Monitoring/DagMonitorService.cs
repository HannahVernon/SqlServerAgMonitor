using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;

namespace SqlAgMonitor.Core.Services.Monitoring;

/// <summary>
/// Monitors Distributed Availability Groups by polling each member server independently.
/// Each member connection queries its local AG's replica and database states, then results
/// are merged into a unified DistributedAgInfo model with cross-member LSN comparisons.
/// </summary>
public class DagMonitorService : IAgMonitorService
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<DagMonitorService> _logger;
    private readonly Subject<MonitoredGroupSnapshot> _snapshots = new();
    private readonly ISubject<MonitoredGroupSnapshot> _syncSnapshots;
    private readonly ConcurrentDictionary<string, IDisposable> _pollingSubscriptions = new();
    private readonly ConcurrentDictionary<string, ReconnectingConnectionWrapper> _connections = new();
    private bool _useLegacyDbStateSql;
    private bool _disposed;

    public IObservable<MonitoredGroupSnapshot> Snapshots => _syncSnapshots.AsObservable();

    /// <summary>
    /// Queries the distributed AG's own replicas to get DAG-level topology
    /// (member AG names, roles, health). Same from any member server.
    /// </summary>
    private const string DagTopologySql = @"
        SELECT
            ag.[name]                                   AS [dag_name],
            ar.[replica_server_name],
            ars.[role_desc],
            ars.[operational_state_desc],
            ars.[connected_state_desc],
            ars.[recovery_health_desc],
            ars.[synchronization_health_desc],
            ar.[availability_mode_desc],
            ar.[failover_mode_desc],
            ar.[endpoint_url]
        FROM sys.availability_replicas ar
            INNER JOIN sys.availability_groups ag
                ON ar.[group_id] = ag.[group_id]
            INNER JOIN sys.dm_hadr_availability_replica_states ars
                ON ar.[replica_id] = ars.[replica_id]
        WHERE ag.[is_distributed] = 1
        ORDER BY ag.[name], ar.[replica_server_name];
    ";

    /// <summary>
    /// Gets the local member AG's replica status by drilling through
    /// fn_hadr_distributed_ag_replica to the underlying local AG.
    /// </summary>
    private const string LocalAgReplicasSql = @"
        SELECT
            [ag1].[name]                                AS [local_ag_name],
            [ar1].[replica_server_name],
            [ars1].[role_desc],
            [ars1].[operational_state_desc],
            [ars1].[connected_state_desc],
            [ars1].[recovery_health_desc],
            [ars1].[synchronization_health_desc],
            [ar1].[availability_mode_desc],
            [ar1].[failover_mode_desc],
            [ar1].[endpoint_url]
        FROM [sys].[availability_groups] [dag]
            INNER JOIN [sys].[availability_replicas] [ar]
                ON [dag].[group_id] = [ar].[group_id]
            CROSS APPLY [sys].[fn_hadr_distributed_ag_replica]([dag].[group_id], [ar].[replica_id]) [hdar]
            INNER JOIN [sys].[availability_groups] [ag1]
                ON [hdar].[group_id] = [ag1].[group_id]
            INNER JOIN [sys].[availability_replicas] [ar1]
                ON [ag1].[group_id] = [ar1].[group_id]
            INNER JOIN [sys].[dm_hadr_availability_replica_states] [ars1]
                ON [ar1].[replica_id] = [ars1].[replica_id]
        WHERE [dag].[is_distributed] = 1
        ORDER BY [ag1].[name], [ar1].[replica_server_name];
    ";

    /// <summary>
    /// Gets database-level states for the local member AG by drilling through
    /// fn_hadr_distributed_ag_replica. Returns all replicas' database states
    /// (not just is_local) so the primary sees secondaries' LSN progress.
    /// </summary>
    private const string LocalAgDbStatesSql = @"
        SELECT
            [ag1].[name]                                AS [local_ag_name],
            [d].[name]                                  AS [database_name],
            [ar1].[replica_server_name],
            [hdrs].[is_local],
            [hdrs].[synchronization_state_desc],
            [hdrs].[last_hardened_lsn],
            [hdrs].[last_commit_lsn],
            [hdrs].[log_send_queue_size],
            [hdrs].[redo_queue_size],
            [hdrs].[log_send_rate],
            [hdrs].[redo_rate],
            [hdrs].[is_suspended],
            [hdrs].[suspend_reason_desc],
            [ar1].[availability_mode_desc],
            [hdrs].[secondary_lag_seconds]
        FROM [sys].[availability_groups] [dag]
            INNER JOIN [sys].[availability_replicas] [ar]
                ON [dag].[group_id] = [ar].[group_id]
            CROSS APPLY [sys].[fn_hadr_distributed_ag_replica]([dag].[group_id], [ar].[replica_id]) [hdar]
            INNER JOIN [sys].[availability_groups] [ag1]
                ON [hdar].[group_id] = [ag1].[group_id]
            INNER JOIN [sys].[dm_hadr_database_replica_states] [hdrs]
                ON [ag1].[group_id] = [hdrs].[group_id]
            INNER JOIN [sys].[availability_replicas] [ar1]
                ON [hdrs].[replica_id] = [ar1].[replica_id]
                AND [hdrs].[group_id] = [ar1].[group_id]
            INNER JOIN [sys].[databases] [d]
                ON [hdrs].[database_id] = [d].[database_id]
        WHERE [dag].[is_distributed] = 1
        ORDER BY [ag1].[name], [ar1].[replica_server_name], [d].[name];
    ";

    /// <summary>Fallback query for SQL Server 2014 where secondary_lag_seconds does not exist.</summary>
    private const string LocalAgDbStatesSqlLegacy = @"
        SELECT
            [ag1].[name]                                AS [local_ag_name],
            [d].[name]                                  AS [database_name],
            [ar1].[replica_server_name],
            [hdrs].[is_local],
            [hdrs].[synchronization_state_desc],
            [hdrs].[last_hardened_lsn],
            [hdrs].[last_commit_lsn],
            [hdrs].[log_send_queue_size],
            [hdrs].[redo_queue_size],
            [hdrs].[log_send_rate],
            [hdrs].[redo_rate],
            [hdrs].[is_suspended],
            [hdrs].[suspend_reason_desc],
            [ar1].[availability_mode_desc],
            NULL AS [secondary_lag_seconds]
        FROM [sys].[availability_groups] [dag]
            INNER JOIN [sys].[availability_replicas] [ar]
                ON [dag].[group_id] = [ar].[group_id]
            CROSS APPLY [sys].[fn_hadr_distributed_ag_replica]([dag].[group_id], [ar].[replica_id]) [hdar]
            INNER JOIN [sys].[availability_groups] [ag1]
                ON [hdar].[group_id] = [ag1].[group_id]
            INNER JOIN [sys].[dm_hadr_database_replica_states] [hdrs]
                ON [ag1].[group_id] = [hdrs].[group_id]
            INNER JOIN [sys].[availability_replicas] [ar1]
                ON [hdrs].[replica_id] = [ar1].[replica_id]
                AND [hdrs].[group_id] = [ar1].[group_id]
            INNER JOIN [sys].[databases] [d]
                ON [hdrs].[database_id] = [d].[database_id]
        WHERE [dag].[is_distributed] = 1
        ORDER BY [ag1].[name], [ar1].[replica_server_name], [d].[name];
    ";

    public DagMonitorService(
        ISqlConnectionService connectionService,
        IConfigurationService configService,
        ILogger<DagMonitorService> logger)
    {
        _connectionService = connectionService;
        _configService = configService;
        _logger = logger;
        _syncSnapshots = Subject.Synchronize(_snapshots);
    }

    public Task StartMonitoringAsync(string groupName, CancellationToken cancellationToken = default)
    {
        if (_pollingSubscriptions.ContainsKey(groupName))
        {
            _logger.LogWarning("Already monitoring DAG {Group}.", groupName);
            return Task.CompletedTask;
        }

        var config = _configService.Load();
        var groupConfig = config.MonitoredGroups.FirstOrDefault(g => g.Name == groupName);
        if (groupConfig == null)
        {
            _logger.LogError("No configuration found for DAG {Group}.", groupName);
            return Task.CompletedTask;
        }

        // Minimum 5s polling interval to avoid overwhelming SQL Server with DMV queries
        var interval = Math.Max(5, groupConfig.PollingIntervalSeconds ?? config.GlobalPollingIntervalSeconds);

        var subscription = Observable
            .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(interval))
            .SelectMany(_ => Observable.FromAsync(async ct =>
            {
                try
                {
                    return await PollGroupAsync(groupName, groupConfig, blocking: false, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DAG poll cycle failed for {Group} — will retry next interval.", groupName);
                    return new MonitoredGroupSnapshot
                    {
                        Name = groupName,
                        GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
                        Timestamp = DateTimeOffset.UtcNow,
                        OverallHealth = SynchronizationHealth.Unknown,
                        ErrorMessage = ex.Message,
                        IsConnected = false
                    };
                }
            }))
            .Where(snapshot => snapshot != null)
            .Subscribe(
                snapshot => _syncSnapshots.OnNext(snapshot!),
                ex => _logger.LogError(ex, "DAG polling error for {Group}.", groupName));

        _pollingSubscriptions[groupName] = subscription;
        _logger.LogInformation("Started DAG monitoring for {Group} with {Count} member connection(s), every {Interval}s.",
            groupName, groupConfig.Connections.Count, interval);
        return Task.CompletedTask;
    }

    public Task StopMonitoringAsync(string groupName, CancellationToken cancellationToken = default)
    {
        if (_pollingSubscriptions.TryRemove(groupName, out var subscription))
        {
            subscription.Dispose();
            _logger.LogInformation("Stopped DAG monitoring for {Group}.", groupName);
        }

        var keysToRemove = _connections.Keys.Where(k => k.StartsWith(groupName + ":")).ToList();
        foreach (var key in keysToRemove)
        {
            if (_connections.TryRemove(key, out var conn))
                _ = conn.DisposeAsync();
        }

        return Task.CompletedTask;
    }

    public async Task<MonitoredGroupSnapshot> PollOnceAsync(string groupName, CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        var groupConfig = config.MonitoredGroups.FirstOrDefault(g => g.Name == groupName);
        if (groupConfig == null)
            throw new InvalidOperationException($"No configuration found for DAG '{groupName}'.");

        return await PollGroupAsync(groupName, groupConfig, blocking: true, cancellationToken)
            ?? throw new InvalidOperationException("Poll returned no result.");
    }

    private async Task<MonitoredGroupSnapshot?> PollGroupAsync(
        string groupName, MonitoredGroupConfig groupConfig, bool blocking, CancellationToken cancellationToken)
    {
        if (groupConfig.Connections.Count == 0)
            return CreateErrorSnapshot(groupName, "No connections configured for DAG.");

        try
        {
            // Poll all member servers concurrently — each acquires its own lease
            var pollTasks = groupConfig.Connections
                .Select((conn, idx) => PollMemberAsync(groupName, idx, conn, blocking, cancellationToken))
                .ToList();

            var memberResults = await Task.WhenAll(pollTasks);

            // If all members were skipped (all leases busy), skip this cycle
            if (memberResults.All(r => r == null))
                return null;

            var dagInfo = MergeResults(groupName,
                memberResults.Where(r => r != null).ToArray()!);

            // Use the primary member's server time if available, else first successful
            var primaryResult = memberResults.FirstOrDefault(r => r is { IsSuccess: true })
                ?? memberResults.FirstOrDefault(r => r != null);
            var snapshotTime = primaryResult?.ServerTime ?? DateTimeOffset.UtcNow;

            return new MonitoredGroupSnapshot
            {
                Name = groupName,
                GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
                Timestamp = snapshotTime,
                DagInfo = dagInfo,
                OverallHealth = dagInfo.OverallHealth,
                IsConnected = memberResults.Any(r => r is { IsSuccess: true })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling DAG {Group}.", groupName);
            return CreateErrorSnapshot(groupName, ex.Message);
        }
    }

    private async Task<MemberPollResult?> PollMemberAsync(
        string groupName, int index, ConnectionConfig connConfig, bool blocking, CancellationToken ct)
    {
        var server = connConfig.Server;
        var wrapper = GetOrCreateWrapper(groupName, index, connConfig);

        ConnectionLease? lease;
        if (blocking)
        {
            lease = await wrapper.AcquireAsync(ct);
        }
        else
        {
            lease = await wrapper.TryAcquireAsync(ct);
            if (lease == null)
                return null; // Previous poll still using this connection
        }

        await using (lease)
        {
            try
            {
                var connection = lease.Connection;
                var dagTopology = await QueryDagTopologyAsync(connection, ct);
                var localReplicas = await QueryLocalAgReplicasAsync(connection, ct);
                var localDbStates = await QueryLocalAgDbStatesAsync(connection, ct);
                var serverTime = await QueryServerTimeAsync(connection, ct);

                return new MemberPollResult
                {
                    Server = server,
                    IsSuccess = true,
                    DagTopology = dagTopology,
                    LocalReplicas = localReplicas,
                    LocalDbStates = localDbStates,
                    ServerTime = serverTime
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll DAG member {Server} for {Group}.", server, groupName);
                lease.Invalidate();

                return new MemberPollResult
                {
                    Server = server,
                    IsSuccess = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }

    private ReconnectingConnectionWrapper GetOrCreateWrapper(
        string groupName, int index, ConnectionConfig connConfig)
    {
        var key = $"{groupName}:{index}";
        if (!_connections.TryGetValue(key, out var wrapper))
        {
            wrapper = new ReconnectingConnectionWrapper(
                _connectionService, _logger,
                connConfig.Server, connConfig.Username,
                connConfig.CredentialKey, connConfig.AuthType,
                connConfig.Encrypt, connConfig.TrustServerCertificate);
            _connections[key] = wrapper;
        }
        return wrapper;
    }

    /// <summary>
    /// Queries SYSDATETIMEOFFSET() from the SQL Server to get the server's local time with UTC offset.
    /// Falls back to DateTimeOffset.UtcNow if the query fails.
    /// </summary>
    private static async Task<DateTimeOffset> QueryServerTimeAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT SYSDATETIMEOFFSET();";
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is DateTimeOffset dto)
                return dto;
        }
        catch
        {
            // Non-critical — fall back to monitor machine's UTC time
        }
        return DateTimeOffset.UtcNow;
    }

    private async Task<List<DagTopologyRow>> QueryDagTopologyAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var rows = new List<DagTopologyRow>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DagTopologySql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DagTopologyRow
            {
                DagName = reader.GetString(0),
                MemberAgName = reader.GetString(1),
                RoleDesc = reader.IsDBNull(2) ? null : reader.GetString(2),
                OperationalStateDesc = reader.IsDBNull(3) ? null : reader.GetString(3),
                ConnectedStateDesc = reader.IsDBNull(4) ? null : reader.GetString(4),
                RecoveryHealthDesc = reader.IsDBNull(5) ? null : reader.GetString(5),
                SynchronizationHealthDesc = reader.IsDBNull(6) ? null : reader.GetString(6),
                AvailabilityModeDesc = reader.IsDBNull(7) ? null : reader.GetString(7),
                FailoverModeDesc = reader.IsDBNull(8) ? null : reader.GetString(8),
                EndpointUrl = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return rows;
    }

    private async Task<List<ReplicaInfo>> QueryLocalAgReplicasAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var replicas = new List<ReplicaInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = LocalAgReplicasSql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            replicas.Add(new ReplicaInfo
            {
                AgName = reader.GetString(0),
                ReplicaServerName = reader.GetString(1),
                Role = SqlParsingHelpers.ParseRole(reader.IsDBNull(2) ? null : reader.GetString(2)),
                OperationalState = SqlParsingHelpers.ParseOperationalState(reader.IsDBNull(3) ? null : reader.GetString(3)),
                ConnectedState = SqlParsingHelpers.ParseConnectedState(reader.IsDBNull(4) ? null : reader.GetString(4)),
                RecoveryHealth = SqlParsingHelpers.ParseRecoveryHealth(reader.IsDBNull(5) ? null : reader.GetString(5)),
                SynchronizationHealth = SqlParsingHelpers.ParseSyncHealth(reader.IsDBNull(6) ? null : reader.GetString(6)),
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(reader.IsDBNull(7) ? null : reader.GetString(7)),
                FailoverMode = reader.IsDBNull(8) ? null : reader.GetString(8),
                EndpointUrl = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return replicas;
    }

    private async Task<List<DatabaseReplicaState>> QueryLocalAgDbStatesAsync(
        SqlConnection connection, CancellationToken ct)
    {
        if (!_useLegacyDbStateSql)
        {
            try
            {
                return await ExecuteDbStateQueryAsync(connection, LocalAgDbStatesSql, ct);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
            {
                _logger.LogWarning("secondary_lag_seconds not available; using legacy query.");
                _useLegacyDbStateSql = true;
            }
        }

        return await ExecuteDbStateQueryAsync(connection, LocalAgDbStatesSqlLegacy, ct);
    }

    private static async Task<List<DatabaseReplicaState>> ExecuteDbStateQueryAsync(
        SqlConnection connection, string sql, CancellationToken ct)
    {
        var dbStates = new List<DatabaseReplicaState>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            dbStates.Add(new DatabaseReplicaState
            {
                AgName = reader.GetString(0),
                DatabaseName = reader.GetString(1),
                ReplicaServerName = reader.GetString(2),
                IsLocal = !reader.IsDBNull(3) && reader.GetBoolean(3),
                SynchronizationState = SqlParsingHelpers.ParseSyncState(reader.IsDBNull(4) ? null : reader.GetString(4)),
                LastHardenedLsn = reader.IsDBNull(5) ? 0 : reader.GetDecimal(5),
                LastCommitLsn = reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                LogSendQueueSizeKb = reader.IsDBNull(7) ? 0 : Convert.ToInt64(reader.GetValue(7)),
                RedoQueueSizeKb = reader.IsDBNull(8) ? 0 : Convert.ToInt64(reader.GetValue(8)),
                LogSendRateKbPerSec = reader.IsDBNull(9) ? 0 : Convert.ToInt64(reader.GetValue(9)),
                RedoRateKbPerSec = reader.IsDBNull(10) ? 0 : Convert.ToInt64(reader.GetValue(10)),
                IsSuspended = !reader.IsDBNull(11) && reader.GetBoolean(11),
                SuspendReason = reader.IsDBNull(12) ? null : reader.GetString(12),
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(reader.IsDBNull(13) ? null : reader.GetString(13)),
                SecondaryLagSeconds = reader.IsDBNull(14) ? 0 : Convert.ToInt64(reader.GetValue(14))
            });
        }

        return dbStates;
    }

    /// <summary>
    /// Merges poll results from all member servers into a unified DistributedAgInfo.
    /// DAG topology comes from the first successful member. Each member contributes
    /// its local AG's replicas and database states.
    /// </summary>
    private DistributedAgInfo MergeResults(string groupName, MemberPollResult[] results)
    {
        var dagInfo = new DistributedAgInfo { DagName = groupName };
        var successful = results.Where(r => r.IsSuccess).ToList();

        if (successful.Count == 0)
        {
            dagInfo.OverallHealth = SynchronizationHealth.Unknown;
            return dagInfo;
        }

        // Use DAG topology from first successful member
        var topology = successful.First().DagTopology
            .Where(t => string.Equals(t.DagName, groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (topology.Count == 0)
        {
            _logger.LogWarning("No DAG topology found for {Group}.", groupName);
            dagInfo.OverallHealth = SynchronizationHealth.Unknown;
            return dagInfo;
        }

        // Build a DistributedAgMember for each DAG member
        foreach (var topo in topology)
        {
            var member = new DistributedAgMember
            {
                MemberAgName = topo.MemberAgName,
                DistributedRole = SqlParsingHelpers.ParseRole(topo.RoleDesc),
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(topo.AvailabilityModeDesc),
                SynchronizationHealth = SqlParsingHelpers.ParseSyncHealth(topo.SynchronizationHealthDesc)
            };

            // Find the poll result that has this member's local AG data.
            // Each member server's fn_hadr_distributed_ag_replica resolves to its local AG.
            // We match by checking which poll result has replicas.
            MemberPollResult? matchingPoll = null;
            foreach (var poll in successful)
            {
                if (poll.LocalReplicas.Count > 0)
                {
                    // Check if this poll result hasn't been claimed by another member yet
                    matchingPoll = poll;
                    break;
                }
            }

            // Better matching: try each successful poll and use the one whose server
            // matches this member's topology endpoint, or take any unclaimed one
            matchingPoll = null;
            foreach (var poll in successful)
            {
                // The poll.Server is the connection endpoint. Each member server resolves
                // fn_hadr_distributed_ag_replica to its own local AG only. So each poll
                // result naturally belongs to one member. We just need to associate them.
                // Since we can't directly match topology member names to connection servers,
                // we rely on order or use a heuristic.
                if (poll.LocalReplicas.Count > 0 && !poll.Claimed)
                {
                    matchingPoll = poll;
                    poll.Claimed = true;
                    break;
                }
            }

            if (matchingPoll != null)
            {
                var agInfo = BuildLocalAgInfo(matchingPoll.LocalReplicas, matchingPoll.LocalDbStates);
                member.LocalAgInfo = agInfo;
                member.SynchronizationHealth = agInfo.OverallHealth;
            }

            dagInfo.Members.Add(member);
        }

        // Compute cross-member LSN differences
        ComputeCrossMemberLsnDifferences(dagInfo);

        dagInfo.OverallHealth = ComputeDagOverallHealth(dagInfo);
        return dagInfo;
    }

    private AvailabilityGroupInfo BuildLocalAgInfo(
        List<ReplicaInfo> replicas, List<DatabaseReplicaState> dbStates)
    {
        var agName = replicas.FirstOrDefault()?.AgName ?? "Unknown";
        var agInfo = new AvailabilityGroupInfo { AgName = agName };

        // Compute log block differences from primary within this AG
        var primaryReplica = replicas.FirstOrDefault(r => r.Role == ReplicaRole.Primary);
        if (primaryReplica != null)
        {
            var primaryLsnByDb = dbStates
                .Where(d => string.Equals(d.ReplicaServerName, primaryReplica.ReplicaServerName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().LastHardenedLsn, StringComparer.OrdinalIgnoreCase);

            foreach (var dbState in dbStates)
            {
                if (primaryLsnByDb.TryGetValue(dbState.DatabaseName, out var primaryLsn))
                {
                    dbState.LogBlockDifference = LsnHelper.ComputeLogBlockDiff(primaryLsn, dbState.LastHardenedLsn);
                    dbState.VlfDifference = LsnHelper.ComputeVlfDiff(primaryLsn, dbState.LastHardenedLsn);
                }
            }
        }

        foreach (var replica in replicas)
        {
            var replicaDbStates = dbStates
                .Where(d => string.Equals(d.ReplicaServerName, replica.ReplicaServerName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            replica.DatabaseCount = replicaDbStates.Count;
            foreach (var dbs in replicaDbStates)
                replica.DatabaseStates.Add(dbs);

            agInfo.Replicas.Add(replica);
        }

        agInfo.OverallHealth = SqlParsingHelpers.ComputeOverallHealth(replicas.AsReadOnly());
        return agInfo;
    }

    /// <summary>
    /// Compares last_hardened_lsn across DAG members for the same database,
    /// using slot-stripped log block position differences.
    /// Uses each member's primary replica as the source of that member's LSN.
    /// </summary>
    private void ComputeCrossMemberLsnDifferences(DistributedAgInfo dagInfo)
    {
        var primaryMember = dagInfo.Members.FirstOrDefault(m => m.DistributedRole == ReplicaRole.Primary);
        var secondaryMembers = dagInfo.Members.Where(m => m.DistributedRole == ReplicaRole.Secondary).ToList();

        if (primaryMember?.LocalAgInfo == null || secondaryMembers.Count == 0)
            return;

        // Get primary member's database LSNs (from its primary replica)
        var primaryReplica = primaryMember.LocalAgInfo.Replicas
            .FirstOrDefault(r => r.Role == ReplicaRole.Primary);
        if (primaryReplica == null) return;

        var primaryLsnByDb = primaryReplica.DatabaseStates
            .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().LastHardenedLsn, StringComparer.OrdinalIgnoreCase);

        foreach (var secMember in secondaryMembers)
        {
            if (secMember.LocalAgInfo == null) continue;

            var secPrimaryReplica = secMember.LocalAgInfo.Replicas
                .FirstOrDefault(r => r.Role == ReplicaRole.Primary);
            if (secPrimaryReplica == null) continue;

            foreach (var dbState in secPrimaryReplica.DatabaseStates)
            {
                if (primaryLsnByDb.TryGetValue(dbState.DatabaseName, out var primaryLsn))
                {
                    dbState.LogBlockDifference = LsnHelper.ComputeLogBlockDiff(primaryLsn, dbState.LastHardenedLsn);
                    dbState.VlfDifference = LsnHelper.ComputeVlfDiff(primaryLsn, dbState.LastHardenedLsn);
                }
            }
        }
    }

    private static SynchronizationHealth ComputeDagOverallHealth(DistributedAgInfo dagInfo)
    {
        if (dagInfo.Members.Count == 0) return SynchronizationHealth.Unknown;
        if (dagInfo.Members.All(m => m.SynchronizationHealth == SynchronizationHealth.Healthy))
            return SynchronizationHealth.Healthy;
        if (dagInfo.Members.Any(m => m.SynchronizationHealth == SynchronizationHealth.NotHealthy))
            return SynchronizationHealth.NotHealthy;
        if (dagInfo.Members.Any(m => m.SynchronizationHealth == SynchronizationHealth.Unknown))
            return SynchronizationHealth.PartiallyHealthy;
        return SynchronizationHealth.PartiallyHealthy;
    }

    private static MonitoredGroupSnapshot CreateErrorSnapshot(string groupName, string errorMessage)
    {
        return new MonitoredGroupSnapshot
        {
            Name = groupName,
            GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
            Timestamp = DateTimeOffset.UtcNow,
            OverallHealth = SynchronizationHealth.Unknown,
            ErrorMessage = errorMessage,
            IsConnected = false
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            foreach (var sub in _pollingSubscriptions.Values)
                sub.Dispose();
            _pollingSubscriptions.Clear();

            foreach (var conn in _connections.Values)
                await conn.DisposeAsync();
            _connections.Clear();

            _snapshots.OnCompleted();
            _snapshots.Dispose();
            _disposed = true;
        }
    }

    private sealed class MemberPollResult
    {
        public required string Server { get; init; }
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public List<DagTopologyRow> DagTopology { get; init; } = [];
        public List<ReplicaInfo> LocalReplicas { get; init; } = [];
        public List<DatabaseReplicaState> LocalDbStates { get; init; } = [];
        public DateTimeOffset ServerTime { get; init; }
        public bool Claimed { get; set; }
    }

    private sealed class DagTopologyRow
    {
        public required string DagName { get; init; }
        public required string MemberAgName { get; init; }
        public string? RoleDesc { get; init; }
        public string? OperationalStateDesc { get; init; }
        public string? ConnectedStateDesc { get; init; }
        public string? RecoveryHealthDesc { get; init; }
        public string? SynchronizationHealthDesc { get; init; }
        public string? AvailabilityModeDesc { get; init; }
        public string? FailoverModeDesc { get; init; }
        public string? EndpointUrl { get; init; }
    }
}
