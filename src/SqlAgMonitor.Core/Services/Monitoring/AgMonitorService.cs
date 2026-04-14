using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;

namespace SqlAgMonitor.Core.Services.Monitoring;

public class AgMonitorService : IAgMonitorService
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<AgMonitorService> _logger;
    private readonly Subject<MonitoredGroupSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, IDisposable> _pollingSubscriptions = new();
    private readonly ConcurrentDictionary<string, ReconnectingConnectionWrapper> _connections = new();
    private bool _useLegacyDbStateSql;
    private bool _disposed;

    public IObservable<MonitoredGroupSnapshot> Snapshots => _snapshots.AsObservable();

    private const string AgStatusSql = @"
        SELECT
            ag.[name]                                   AS [ag_name],
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
        WHERE COALESCE(ag.[is_distributed], 0) = 0
        ORDER BY ag.[name], ar.[replica_server_name];
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
            hdrs.[is_suspended],
            hdrs.[suspend_reason_desc],
            ar.[availability_mode_desc],
            hdrs.[secondary_lag_seconds]
        FROM sys.dm_hadr_database_replica_states hdrs
            INNER JOIN sys.availability_replicas ar
                ON hdrs.[replica_id] = ar.[replica_id]
                AND hdrs.[group_id] = ar.[group_id]
            INNER JOIN sys.availability_groups ag
                ON hdrs.[group_id] = ag.[group_id]
            INNER JOIN sys.databases d
                ON hdrs.[database_id] = d.[database_id]
        WHERE COALESCE(ag.[is_distributed], 0) = 0
        ORDER BY ag.[name], ar.[replica_server_name], d.[name];
    ";

    /// <summary>Fallback query for SQL Server 2014 where secondary_lag_seconds does not exist.</summary>
    private const string DatabaseStateSqlLegacy = @"
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
            ar.[availability_mode_desc],
            NULL AS [secondary_lag_seconds]
        FROM sys.dm_hadr_database_replica_states hdrs
            INNER JOIN sys.availability_replicas ar
                ON hdrs.[replica_id] = ar.[replica_id]
                AND hdrs.[group_id] = ar.[group_id]
            INNER JOIN sys.availability_groups ag
                ON hdrs.[group_id] = ag.[group_id]
            INNER JOIN sys.databases d
                ON hdrs.[database_id] = d.[database_id]
        WHERE COALESCE(ag.[is_distributed], 0) = 0
        ORDER BY ag.[name], ar.[replica_server_name], d.[name];
    ";

    public AgMonitorService(
        ISqlConnectionService connectionService,
        IConfigurationService configService,
        ILogger<AgMonitorService> logger)
    {
        _connectionService = connectionService;
        _configService = configService;
        _logger = logger;
    }

    public Task StartMonitoringAsync(string groupName, CancellationToken cancellationToken = default)
    {
        if (_pollingSubscriptions.ContainsKey(groupName))
        {
            _logger.LogWarning("Already monitoring {Group}.", groupName);
            return Task.CompletedTask;
        }

        var config = _configService.Load();
        var groupConfig = config.MonitoredGroups.FirstOrDefault(g => g.Name == groupName);
        if (groupConfig == null)
        {
            _logger.LogError("No configuration found for group {Group}.", groupName);
            return Task.CompletedTask;
        }

        // Minimum 5s polling interval to avoid overwhelming SQL Server with DMV queries
        var interval = Math.Max(5, groupConfig.PollingIntervalSeconds ?? config.GlobalPollingIntervalSeconds);

        var subscription = Observable
            .Timer(TimeSpan.Zero, TimeSpan.FromSeconds(interval))
            .SelectMany(_ => Observable.FromAsync(ct => PollGroupAsync(groupName, groupConfig, blocking: false, ct)))
            .Where(snapshot => snapshot != null)
            .Subscribe(
                snapshot => _snapshots.OnNext(snapshot!),
                ex => _logger.LogError(ex, "Polling error for {Group}.", groupName));

        _pollingSubscriptions[groupName] = subscription;
        _logger.LogInformation("Started monitoring {Group} every {Interval}s.", groupName, interval);
        return Task.CompletedTask;
    }

    public Task StopMonitoringAsync(string groupName, CancellationToken cancellationToken = default)
    {
        if (_pollingSubscriptions.TryRemove(groupName, out var subscription))
        {
            subscription.Dispose();
            _logger.LogInformation("Stopped monitoring {Group}.", groupName);
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
            throw new InvalidOperationException($"No configuration found for group '{groupName}'.");

        return await PollGroupAsync(groupName, groupConfig, blocking: true, cancellationToken)
            ?? throw new InvalidOperationException("Poll returned no result.");
    }

    private async Task<MonitoredGroupSnapshot?> PollGroupAsync(
        string groupName, MonitoredGroupConfig groupConfig, bool blocking, CancellationToken cancellationToken)
    {
        var connConfig = groupConfig.Connections.FirstOrDefault();
        if (connConfig == null)
        {
            return new MonitoredGroupSnapshot
            {
                Name = groupName,
                GroupType = Enum.TryParse<AvailabilityGroupType>(groupConfig.GroupType, out var gt)
                    ? gt : AvailabilityGroupType.AvailabilityGroup,
                Timestamp = DateTimeOffset.UtcNow,
                OverallHealth = SynchronizationHealth.Unknown,
                ErrorMessage = "No connection configured.",
                IsConnected = false
            };
        }

        var wrapper = GetOrCreateWrapper(groupName, connConfig);

        // Acquire connection lease — non-blocking for timer polls, blocking for manual refresh
        ConnectionLease? lease;
        if (blocking)
        {
            lease = await wrapper.AcquireAsync(cancellationToken);
        }
        else
        {
            lease = await wrapper.TryAcquireAsync(cancellationToken);
            if (lease == null)
            {
                _logger.LogDebug("Poll {Group}: skipped — previous poll still running.", groupName);
                return null;
            }
        }

        await using (lease)
        {
            try
            {
                var connection = lease.Connection;
                var replicas = await QueryReplicasAsync(connection, cancellationToken);
                var dbStates = await QueryDatabaseStatesAsync(connection, cancellationToken);

                _logger.LogDebug(
                    "Poll {Group}: {ReplicaCount} replica(s), {DbStateCount} database state(s) from {Server}.",
                    groupName, replicas.Count, dbStates.Count, connConfig.Server);

                if (replicas.Count == 0)
                {
                    _logger.LogWarning(
                        "Poll {Group}: DMV query returned 0 replicas from {Server}. "
                        + "Verify the AG name matches and the service account has VIEW SERVER STATE.",
                        groupName, connConfig.Server);
                }

                var agInfo = BuildAgInfo(groupName, replicas, dbStates);

                return new MonitoredGroupSnapshot
                {
                    Name = groupName,
                    GroupType = Enum.TryParse<AvailabilityGroupType>(groupConfig.GroupType, out var groupType)
                        ? groupType : AvailabilityGroupType.AvailabilityGroup,
                    Timestamp = DateTimeOffset.UtcNow,
                    AgInfo = agInfo,
                    OverallHealth = agInfo.OverallHealth,
                    IsConnected = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling group {Group}.", groupName);
                lease.Invalidate();
                return new MonitoredGroupSnapshot
                {
                    Name = groupName,
                    GroupType = Enum.TryParse<AvailabilityGroupType>(groupConfig.GroupType, out var gt2)
                        ? gt2 : AvailabilityGroupType.AvailabilityGroup,
                    Timestamp = DateTimeOffset.UtcNow,
                    OverallHealth = SynchronizationHealth.Unknown,
                    ErrorMessage = ex.Message,
                    IsConnected = false
                };
            }
        }
    }

    private ReconnectingConnectionWrapper GetOrCreateWrapper(string groupName, ConnectionConfig connConfig)
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
                connConfig.AuthType,
                connConfig.Encrypt,
                connConfig.TrustServerCertificate);
            _connections[key] = wrapper;
        }
        return wrapper;
    }

    private async Task<List<ReplicaInfo>> QueryReplicasAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var replicas = new List<ReplicaInfo>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = AgStatusSql;

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
        if (!_useLegacyDbStateSql)
        {
            try
            {
                return await ExecuteDbStateQueryAsync(connection, DatabaseStateSql, cancellationToken);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 207)
            {
                // "Invalid column name 'secondary_lag_seconds'" — SQL Server < 2016
                _logger.LogWarning("secondary_lag_seconds not available; using legacy query.");
                _useLegacyDbStateSql = true;
            }
        }

        return await ExecuteDbStateQueryAsync(connection, DatabaseStateSqlLegacy, cancellationToken);
    }

    private static async Task<List<DatabaseReplicaState>> ExecuteDbStateQueryAsync(
        SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var dbStates = new List<DatabaseReplicaState>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

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
                AvailabilityMode = SqlParsingHelpers.ParseAvailabilityMode(reader.IsDBNull(13) ? null : reader.GetString(13)),
                SecondaryLagSeconds = reader.IsDBNull(14) ? 0 : Convert.ToInt64(reader.GetValue(14))
            });
        }

        return dbStates;
    }

    private AvailabilityGroupInfo BuildAgInfo(
        string groupName, List<ReplicaInfo> replicas, List<DatabaseReplicaState> dbStates)
    {
        var agInfo = new AvailabilityGroupInfo { AgName = groupName };

        // Filter to the specific AG
        var agReplicas = replicas
            .Where(r => string.Equals(r.AgName, groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var agDbStates = dbStates
            .Where(d => string.Equals(d.AgName, groupName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Compute log block differences from primary (slot-stripped)
        var primaryStates = agDbStates
            .Where(d => agReplicas.Any(r => r.ReplicaServerName == d.ReplicaServerName && r.Role == ReplicaRole.Primary))
            .GroupBy(d => d.DatabaseName)
            .ToDictionary(g => g.Key, g => g.First().LastHardenedLsn);

        foreach (var dbState in agDbStates)
        {
            if (primaryStates.TryGetValue(dbState.DatabaseName, out var primaryLsn))
            {
                dbState.LogBlockDifference = LsnHelper.ComputeLogBlockDiff(primaryLsn, dbState.LastHardenedLsn);
                dbState.VlfDifference = LsnHelper.ComputeVlfDiff(primaryLsn, dbState.LastHardenedLsn);
            }
        }

        foreach (var replica in agReplicas)
        {
            var replicaDbStates = agDbStates.Where(d => d.ReplicaServerName == replica.ReplicaServerName).ToList();
            replica.DatabaseCount = replicaDbStates.Count;
            foreach (var dbs in replicaDbStates)
                replica.DatabaseStates.Add(dbs);
            agInfo.Replicas.Add(replica);
        }

        agInfo.OverallHealth = SqlParsingHelpers.ComputeOverallHealth(agReplicas.AsReadOnly());
        return agInfo;
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
