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
/// Monitors Distributed Availability Groups by polling multiple servers simultaneously,
/// merging topology and database-state data, and computing cross-member LSN comparisons.
/// </summary>
public class DagMonitorService : IAgMonitorService
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<DagMonitorService> _logger;
    private readonly Subject<MonitoredGroupSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, IDisposable> _pollingSubscriptions = new();
    private readonly ConcurrentDictionary<string, ReconnectingConnectionWrapper> _connections = new();
    private bool _disposed;

    public IObservable<MonitoredGroupSnapshot> Snapshots => _snapshots.AsObservable();

    private const string DagTopologySql = @"
        SELECT
            ag.[name]                                   AS [distributed_ag_name],
            ars.[role_desc]                              AS [distributed_ag_role],
            ag1.[name]                                   AS [local_ag_name],
            ar.[replica_server_name],
            arsl.[role_desc]                             AS [local_role],
            arsl.[operational_state_desc],
            arsl.[connected_state_desc],
            arsl.[recovery_health_desc],
            arsl.[synchronization_health_desc],
            ar.[availability_mode_desc]
        FROM sys.availability_groups ag
            INNER JOIN sys.dm_hadr_availability_replica_states ars
                ON ag.[group_id] = ars.[group_id]
            CROSS APPLY sys.fn_hadr_distributed_ag_replica(ag.[group_id], ars.[replica_id]) hdar
            INNER JOIN sys.availability_groups ag1
                ON hdar.[group_id] = ag1.[group_id]
            INNER JOIN sys.availability_replicas ar
                ON ag1.[group_id] = ar.[group_id]
            INNER JOIN sys.dm_hadr_availability_replica_states arsl
                ON ar.[replica_id] = arsl.[replica_id]
        WHERE ag.[is_distributed] = 1;
    ";

    private const string DatabaseStateSql = @"
        SELECT
            ag.[name]                                   AS [ag_name],
            d.[name]                                    AS [database_name],
            ar.[replica_server_name],
            hdrs.[is_local],
            hdrs.[synchronization_state_desc],
            hdrs.[last_hardened_lsn],
            hdrs.[last_commit_lsn],
            hdrs.[log_send_queue_size],
            hdrs.[redo_queue_size],
            hdrs.[log_send_rate],
            hdrs.[redo_rate],
            hdrs.[estimated_recovery_time],
            hdrs.[is_suspended],
            hdrs.[suspend_reason_desc],
            ar.[availability_mode_desc]
        FROM sys.dm_hadr_database_replica_states hdrs
            INNER JOIN sys.availability_replicas ar
                ON hdrs.[replica_id] = ar.[replica_id]
                AND hdrs.[group_id] = ar.[group_id]
            INNER JOIN sys.availability_groups ag
                ON hdrs.[group_id] = ag.[group_id]
            INNER JOIN sys.databases d
                ON hdrs.[database_id] = d.[database_id]
        WHERE hdrs.[is_local] = 1
        ORDER BY ag.[name], d.[name];
    ";

    public DagMonitorService(
        ISqlConnectionService connectionService,
        IConfigurationService configService,
        ILogger<DagMonitorService> logger)
    {
        _connectionService = connectionService;
        _configService = configService;
        _logger = logger;
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

        var interval = groupConfig.PollingIntervalSeconds ?? config.GlobalPollingIntervalSeconds;

        var subscription = Observable
            .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(interval))
            .SelectMany(_ => Observable.FromAsync(ct => PollGroupAsync(groupName, groupConfig, ct)))
            .Subscribe(
                snapshot => _snapshots.OnNext(snapshot),
                ex => _logger.LogError(ex, "DAG polling error for {Group}.", groupName));

        _pollingSubscriptions[groupName] = subscription;
        _logger.LogInformation("Started DAG monitoring for {Group} every {Interval}s.", groupName, interval);
        return Task.CompletedTask;
    }

    public Task StopMonitoringAsync(string groupName, CancellationToken cancellationToken = default)
    {
        if (_pollingSubscriptions.TryRemove(groupName, out var subscription))
        {
            subscription.Dispose();
            _logger.LogInformation("Stopped DAG monitoring for {Group}.", groupName);
        }

        // Dispose all connections for this group
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

        return await PollGroupAsync(groupName, groupConfig, cancellationToken);
    }

    private async Task<MonitoredGroupSnapshot> PollGroupAsync(
        string groupName, MonitoredGroupConfig groupConfig, CancellationToken cancellationToken)
    {
        try
        {
            if (groupConfig.Connections.Count == 0)
            {
                return CreateErrorSnapshot(groupName, groupConfig, "No connections configured for DAG.");
            }

            // Poll all configured servers simultaneously
            var pollTasks = groupConfig.Connections
                .Select((connConfig, index) => PollSingleServerAsync(groupName, index, connConfig, cancellationToken))
                .ToList();

            var serverResults = await Task.WhenAll(pollTasks);

            var dagInfo = MergeServerResults(groupName, serverResults);

            return new MonitoredGroupSnapshot
            {
                Name = groupName,
                GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
                Timestamp = DateTimeOffset.UtcNow,
                DagInfo = dagInfo,
                OverallHealth = dagInfo.OverallHealth,
                IsConnected = serverResults.Any(r => r.IsSuccess)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling DAG {Group}.", groupName);
            return CreateErrorSnapshot(groupName, groupConfig, ex.Message);
        }
    }

    private async Task<ServerPollResult> PollSingleServerAsync(
        string groupName, int connectionIndex, ConnectionConfig connConfig, CancellationToken cancellationToken)
    {
        var serverName = connConfig.Server;
        try
        {
            var connection = await GetOrCreateConnectionAsync(groupName, connectionIndex, connConfig, cancellationToken);

            var topologyTask = QueryDagTopologyAsync(connection, cancellationToken);
            var dbStatesTask = QueryDatabaseStatesAsync(connection, cancellationToken);
            await Task.WhenAll(topologyTask, dbStatesTask);

            return new ServerPollResult
            {
                ServerName = serverName,
                IsSuccess = true,
                TopologyRows = topologyTask.Result,
                DatabaseStates = dbStatesTask.Result
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to poll DAG server {Server} in group {Group}.", serverName, groupName);

            // Invalidate the connection so it reconnects on next poll
            var key = $"{groupName}:{connectionIndex}";
            if (_connections.TryGetValue(key, out var wrapper))
                wrapper.InvalidateConnection();

            return new ServerPollResult
            {
                ServerName = serverName,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<SqlConnection> GetOrCreateConnectionAsync(
        string groupName, int connectionIndex, ConnectionConfig connConfig, CancellationToken cancellationToken)
    {
        var key = $"{groupName}:{connectionIndex}";
        if (!_connections.TryGetValue(key, out var wrapper))
        {
            wrapper = new ReconnectingConnectionWrapper(
                _connectionService,
                _logger,
                connConfig.Server,
                connConfig.Username,
                connConfig.CredentialKey,
                connConfig.AuthType);
            _connections[key] = wrapper;
        }

        return await wrapper.GetConnectionAsync(cancellationToken);
    }

    private async Task<List<DagTopologyRow>> QueryDagTopologyAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<DagTopologyRow>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DagTopologySql;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DagTopologyRow
            {
                DistributedAgName = reader.GetString(0),
                DistributedAgRole = reader.IsDBNull(1) ? null : reader.GetString(1),
                LocalAgName = reader.GetString(2),
                ReplicaServerName = reader.GetString(3),
                LocalRole = reader.IsDBNull(4) ? null : reader.GetString(4),
                OperationalStateDesc = reader.IsDBNull(5) ? null : reader.GetString(5),
                ConnectedStateDesc = reader.IsDBNull(6) ? null : reader.GetString(6),
                RecoveryHealthDesc = reader.IsDBNull(7) ? null : reader.GetString(7),
                SynchronizationHealthDesc = reader.IsDBNull(8) ? null : reader.GetString(8),
                AvailabilityModeDesc = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return rows;
    }

    private async Task<List<DatabaseReplicaState>> QueryDatabaseStatesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var dbStates = new List<DatabaseReplicaState>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DatabaseStateSql;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            dbStates.Add(new DatabaseReplicaState
            {
                AgName = reader.GetString(0),
                DatabaseName = reader.GetString(1),
                ReplicaServerName = reader.GetString(2),
                IsLocal = reader.GetBoolean(3),
                SynchronizationState = SqlParsingHelpers.ParseSyncState(reader.IsDBNull(4) ? null : reader.GetString(4)),
                LastHardenedLsn = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                LastCommitLsn = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                LogSendQueueSizeKb = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
                RedoQueueSizeKb = reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                LogSendRateKbPerSec = reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                RedoRateKbPerSec = reader.IsDBNull(10) ? 0 : reader.GetInt64(10),
                EstimatedRecoveryTimeSeconds = reader.IsDBNull(11) ? 0 : reader.GetInt64(11),
                IsSuspended = !reader.IsDBNull(12) && reader.GetBoolean(12),
                SuspendReason = reader.IsDBNull(13) ? null : reader.GetString(13),
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(reader.IsDBNull(14) ? null : reader.GetString(14))
            });
        }

        return dbStates;
    }

    /// <summary>
    /// Merges poll results from all servers into a unified DistributedAgInfo model.
    /// Each server reports its local AG topology and database states; this method
    /// combines them to form the complete DAG picture with cross-member LSN comparisons.
    /// </summary>
    private DistributedAgInfo MergeServerResults(string groupName, ServerPollResult[] results)
    {
        var dagInfo = new DistributedAgInfo { DagName = groupName };

        var successfulResults = results.Where(r => r.IsSuccess).ToList();
        if (successfulResults.Count == 0)
        {
            dagInfo.OverallHealth = SynchronizationHealth.Unknown;
            return dagInfo;
        }

        // Collect all topology rows and deduplicate by (LocalAgName, ReplicaServerName)
        var allTopologyRows = successfulResults.SelectMany(r => r.TopologyRows).ToList();

        // Build members keyed by local AG name
        var membersByAgName = new Dictionary<string, DistributedAgMember>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in allTopologyRows)
        {
            if (!membersByAgName.TryGetValue(row.LocalAgName, out var member))
            {
                member = new DistributedAgMember
                {
                    MemberAgName = row.LocalAgName,
                    DistributedRole = SqlParsingHelpers.ParseRole(row.DistributedAgRole),
                    AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(row.AvailabilityModeDesc)
                };
                membersByAgName[row.LocalAgName] = member;
            }

            // Build local AG replicas from topology rows (deduplicate by server name)
            if (member.LocalAgInfo == null)
            {
                member.LocalAgInfo = new AvailabilityGroupInfo { AgName = row.LocalAgName };
            }

            var existingReplica = member.LocalAgInfo.Replicas
                .FirstOrDefault(r => string.Equals(r.ReplicaServerName, row.ReplicaServerName, StringComparison.OrdinalIgnoreCase));

            if (existingReplica == null)
            {
                member.LocalAgInfo.Replicas.Add(new ReplicaInfo
                {
                    ReplicaServerName = row.ReplicaServerName,
                    Role = SqlParsingHelpers.ParseRole(row.LocalRole),
                    OperationalState = SqlParsingHelpers.ParseOperationalState(row.OperationalStateDesc),
                    ConnectedState = SqlParsingHelpers.ParseConnectedState(row.ConnectedStateDesc),
                    RecoveryHealth = SqlParsingHelpers.ParseRecoveryHealth(row.RecoveryHealthDesc),
                    SynchronizationHealth = SqlParsingHelpers.ParseSyncHealth(row.SynchronizationHealthDesc),
                    AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(row.AvailabilityModeDesc)
                });
            }
        }

        // Attach database states to the appropriate member AG
        foreach (var result in successfulResults)
        {
            foreach (var dbState in result.DatabaseStates)
            {
                if (membersByAgName.TryGetValue(dbState.AgName, out var member) && member.LocalAgInfo != null)
                {
                    var replica = member.LocalAgInfo.Replicas
                        .FirstOrDefault(r => string.Equals(r.ReplicaServerName, dbState.ReplicaServerName, StringComparison.OrdinalIgnoreCase));

                    if (replica != null)
                    {
                        // Avoid duplicate database states from overlapping server polls
                        var alreadyAdded = replica.DatabaseStates
                            .Any(d => string.Equals(d.DatabaseName, dbState.DatabaseName, StringComparison.OrdinalIgnoreCase));

                        if (!alreadyAdded)
                        {
                            replica.DatabaseStates.Add(dbState);
                            replica.DatabaseCount = replica.DatabaseStates.Count;
                        }
                    }
                }
            }
        }

        // Compute health for each member's local AG
        foreach (var member in membersByAgName.Values)
        {
            if (member.LocalAgInfo != null)
            {
                member.LocalAgInfo.OverallHealth = SqlParsingHelpers.ComputeOverallHealth(
                    member.LocalAgInfo.Replicas.ToList().AsReadOnly());
                member.SynchronizationHealth = member.LocalAgInfo.OverallHealth;
            }

            dagInfo.Members.Add(member);
        }

        // Compute cross-member LSN comparisons
        ComputeCrossMemberLsnComparisons(dagInfo, successfulResults);

        dagInfo.OverallHealth = ComputeDagOverallHealth(dagInfo);
        return dagInfo;
    }

    /// <summary>
    /// Compares last_hardened_lsn for the same database name across different member AGs.
    /// The "primary" side is the member whose distributed role is PRIMARY.
    /// </summary>
    private void ComputeCrossMemberLsnComparisons(DistributedAgInfo dagInfo, List<ServerPollResult> results)
    {
        var primaryMember = dagInfo.Members.FirstOrDefault(m => m.DistributedRole == ReplicaRole.Primary);
        var secondaryMembers = dagInfo.Members.Where(m => m.DistributedRole == ReplicaRole.Secondary).ToList();

        if (primaryMember?.LocalAgInfo == null || secondaryMembers.Count == 0)
            return;

        // Collect all database states from all successful server results, grouped by AG name + database name
        // We use data from the servers' local queries, which report is_local=1 states
        var allDbStates = results
            .Where(r => r.IsSuccess)
            .SelectMany(r => r.DatabaseStates)
            .ToList();

        // Get the primary member's local database states (from the primary AG)
        var primaryDbStates = allDbStates
            .Where(d => string.Equals(d.AgName, primaryMember.MemberAgName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var secondaryMember in secondaryMembers)
        {
            var secondaryDbStates = allDbStates
                .Where(d => string.Equals(d.AgName, secondaryMember.MemberAgName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var (dbName, primaryState) in primaryDbStates)
            {
                if (secondaryDbStates.TryGetValue(dbName, out var secondaryState))
                {
                    // Set LsnDifferenceFromPrimary on the secondary state
                    secondaryState.LsnDifferenceFromPrimary = Math.Abs(primaryState.LastHardenedLsn - secondaryState.LastHardenedLsn);
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

    private static MonitoredGroupSnapshot CreateErrorSnapshot(
        string groupName, MonitoredGroupConfig groupConfig, string errorMessage)
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

    /// <summary>Intermediate result from polling a single server in the DAG.</summary>
    private sealed class ServerPollResult
    {
        public required string ServerName { get; init; }
        public bool IsSuccess { get; init; }
        public string? ErrorMessage { get; init; }
        public List<DagTopologyRow> TopologyRows { get; init; } = [];
        public List<DatabaseReplicaState> DatabaseStates { get; init; } = [];
    }

    /// <summary>Raw row from the DAG topology query.</summary>
    private sealed class DagTopologyRow
    {
        public required string DistributedAgName { get; init; }
        public string? DistributedAgRole { get; init; }
        public required string LocalAgName { get; init; }
        public required string ReplicaServerName { get; init; }
        public string? LocalRole { get; init; }
        public string? OperationalStateDesc { get; init; }
        public string? ConnectedStateDesc { get; init; }
        public string? RecoveryHealthDesc { get; init; }
        public string? SynchronizationHealthDesc { get; init; }
        public string? AvailabilityModeDesc { get; init; }
    }
}
