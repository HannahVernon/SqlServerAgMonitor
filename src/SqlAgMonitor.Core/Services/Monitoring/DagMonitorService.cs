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
/// Monitors Distributed Availability Groups by querying the DAG's own replicas and
/// database-level states from a single connection. A distributed AG appears in the
/// system views just like a regular AG — its "replicas" are the member AGs and its
/// database_replica_states show per-member LSN data for both local and remote members.
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

    /// <summary>
    /// Queries the distributed AG's own replicas (one per member AG). The
    /// replica_server_name is the listener/AG name of each member, and the
    /// replica states show the DAG-level role, health, and connectivity.
    /// </summary>
    private const string DagReplicasSql = @"
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
    /// Queries database-level replica states for the distributed AG. Unlike regular
    /// AGs we do NOT filter on is_local — the primary member reports LSN data for
    /// both local and remote members, giving us the complete cross-member picture
    /// from a single connection.
    /// </summary>
    private const string DagDatabaseStateSql = @"
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
        WHERE ag.[is_distributed] = 1
        ORDER BY ag.[name], ar.[replica_server_name], d.[name];
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

        if (_connections.TryRemove(groupName + ":0", out var conn))
            _ = conn.DisposeAsync();

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
            var connConfig = groupConfig.Connections.FirstOrDefault();
            if (connConfig == null)
            {
                return new MonitoredGroupSnapshot
                {
                    Name = groupName,
                    GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
                    Timestamp = DateTimeOffset.UtcNow,
                    OverallHealth = SynchronizationHealth.Unknown,
                    ErrorMessage = "No connection configured for DAG.",
                    IsConnected = false
                };
            }

            var connection = await GetOrCreateConnectionAsync(groupName, connConfig, cancellationToken);

            // Run queries sequentially (no MARS required)
            var replicas = await QueryReplicasAsync(connection, cancellationToken);
            var dbStates = await QueryDatabaseStatesAsync(connection, cancellationToken);

            var dagInfo = BuildDagInfo(groupName, replicas, dbStates);

            return new MonitoredGroupSnapshot
            {
                Name = groupName,
                GroupType = AvailabilityGroupType.DistributedAvailabilityGroup,
                Timestamp = DateTimeOffset.UtcNow,
                DagInfo = dagInfo,
                OverallHealth = dagInfo.OverallHealth,
                IsConnected = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling DAG {Group}.", groupName);

            // Invalidate connection on error so it reconnects on next poll
            if (_connections.TryGetValue(groupName + ":0", out var wrapper))
                wrapper.InvalidateConnection();

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
    }

    private async Task<SqlConnection> GetOrCreateConnectionAsync(
        string groupName, ConnectionConfig connConfig, CancellationToken cancellationToken)
    {
        var key = groupName + ":0";
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

    private async Task<List<ReplicaInfo>> QueryReplicasAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var replicas = new List<ReplicaInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DagReplicasSql;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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

    private async Task<List<DatabaseReplicaState>> QueryDatabaseStatesAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var dbStates = new List<DatabaseReplicaState>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = DagDatabaseStateSql;

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(reader.IsDBNull(13) ? null : reader.GetString(13))
            });
        }

        return dbStates;
    }

    /// <summary>
    /// Builds the DistributedAgInfo model from the DAG's own replica and database
    /// state data. Each DAG "replica" is a member AG; each member gets a
    /// DistributedAgMember with a single-replica AvailabilityGroupInfo so the UI
    /// can render it the same way as regular AG topology + pivot grid.
    /// </summary>
    private DistributedAgInfo BuildDagInfo(
        string groupName, List<ReplicaInfo> replicas, List<DatabaseReplicaState> dbStates)
    {
        var dagInfo = new DistributedAgInfo { DagName = groupName };

        // Filter to the specific DAG
        var dagReplicas = replicas
            .Where(r => string.Equals(r.AgName, groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dagDbStates = dbStates
            .Where(d => string.Equals(d.AgName, groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dagReplicas.Count == 0)
        {
            _logger.LogWarning("No replicas found for DAG {Group}.", groupName);
            dagInfo.OverallHealth = SynchronizationHealth.Unknown;
            return dagInfo;
        }

        // Compute LSN differences from primary member
        var primaryReplica = dagReplicas.FirstOrDefault(r => r.Role == ReplicaRole.Primary);
        if (primaryReplica != null)
        {
            var primaryLsnByDb = dagDbStates
                .Where(d => string.Equals(d.ReplicaServerName, primaryReplica.ReplicaServerName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().LastHardenedLsn, StringComparer.OrdinalIgnoreCase);

            foreach (var dbState in dagDbStates)
            {
                if (primaryLsnByDb.TryGetValue(dbState.DatabaseName, out var primaryLsn))
                {
                    dbState.LsnDifferenceFromPrimary = Math.Abs(primaryLsn - dbState.LastHardenedLsn);
                }
            }
        }

        // Build a DistributedAgMember for each DAG replica (member AG)
        foreach (var replica in dagReplicas)
        {
            var memberDbStates = dagDbStates
                .Where(d => string.Equals(d.ReplicaServerName, replica.ReplicaServerName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            replica.DatabaseCount = memberDbStates.Count;
            foreach (var dbs in memberDbStates)
                replica.DatabaseStates.Add(dbs);

            var memberAgInfo = new AvailabilityGroupInfo { AgName = replica.ReplicaServerName };
            memberAgInfo.Replicas.Add(replica);
            memberAgInfo.OverallHealth = replica.SynchronizationHealth;

            var member = new DistributedAgMember
            {
                MemberAgName = replica.ReplicaServerName,
                DistributedRole = replica.Role,
                AvailabilityMode = replica.AvailabilityMode,
                SynchronizationHealth = replica.SynchronizationHealth,
                LocalAgInfo = memberAgInfo
            };

            dagInfo.Members.Add(member);
        }

        dagInfo.OverallHealth = ComputeDagOverallHealth(dagInfo);
        return dagInfo;
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
}
