using System.Collections.ObjectModel;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Tests.Helpers;

/// <summary>
/// Fluent builder for constructing <see cref="MonitoredGroupSnapshot"/> instances
/// with nested replicas and database states for unit testing.
/// </summary>
public class SnapshotBuilder
{
    private string _name = "TestGroup";
    private AvailabilityGroupType _groupType = AvailabilityGroupType.AvailabilityGroup;
    private DateTimeOffset _timestamp = DateTimeOffset.UtcNow;
    private bool _isConnected = true;
    private SynchronizationHealth _overallHealth = SynchronizationHealth.Healthy;
    private string? _errorMessage;
    private readonly List<ReplicaBuilder> _replicas = new();

    public SnapshotBuilder WithName(string name) { _name = name; return this; }
    public SnapshotBuilder WithGroupType(AvailabilityGroupType type) { _groupType = type; return this; }
    public SnapshotBuilder WithTimestamp(DateTimeOffset ts) { _timestamp = ts; return this; }
    public SnapshotBuilder IsConnected(bool connected) { _isConnected = connected; return this; }
    public SnapshotBuilder WithOverallHealth(SynchronizationHealth health) { _overallHealth = health; return this; }
    public SnapshotBuilder WithError(string error) { _errorMessage = error; return this; }

    public SnapshotBuilder AddReplica(string serverName, ReplicaRole role, Action<ReplicaBuilder>? configure = null)
    {
        var builder = new ReplicaBuilder(serverName, role, _name);
        configure?.Invoke(builder);
        _replicas.Add(builder);
        return this;
    }

    public MonitoredGroupSnapshot Build()
    {
        var agInfo = new AvailabilityGroupInfo
        {
            AgName = _name,
            OverallHealth = _overallHealth
        };

        foreach (var rb in _replicas)
        {
            agInfo.Replicas.Add(rb.Build());
        }

        return new MonitoredGroupSnapshot
        {
            Name = _name,
            GroupType = _groupType,
            Timestamp = _timestamp,
            IsConnected = _isConnected,
            OverallHealth = _overallHealth,
            ErrorMessage = _errorMessage,
            AgInfo = agInfo
        };
    }
}

public class ReplicaBuilder
{
    private readonly string _serverName;
    private readonly string _agName;
    private ReplicaRole _role;
    private ConnectedState _connectedState = ConnectedState.Connected;
    private AvailabilityMode _availabilityMode = AvailabilityMode.SynchronousCommit;
    private SynchronizationHealth _syncHealth = SynchronizationHealth.Healthy;
    private OperationalState _operationalState = OperationalState.Online;
    private RecoveryHealth _recoveryHealth = RecoveryHealth.Online;
    private readonly List<DatabaseStateBuilder> _databases = new();

    public ReplicaBuilder(string serverName, ReplicaRole role, string agName)
    {
        _serverName = serverName;
        _role = role;
        _agName = agName;
    }

    public ReplicaBuilder WithRole(ReplicaRole role) { _role = role; return this; }
    public ReplicaBuilder WithConnectedState(ConnectedState state) { _connectedState = state; return this; }
    public ReplicaBuilder WithAvailabilityMode(AvailabilityMode mode) { _availabilityMode = mode; return this; }
    public ReplicaBuilder WithSyncHealth(SynchronizationHealth health) { _syncHealth = health; return this; }

    public ReplicaBuilder AddDatabase(string dbName, Action<DatabaseStateBuilder>? configure = null)
    {
        var builder = new DatabaseStateBuilder(dbName, _serverName, _agName);
        configure?.Invoke(builder);
        _databases.Add(builder);
        return this;
    }

    public ReplicaInfo Build()
    {
        var replica = new ReplicaInfo
        {
            AgName = _agName,
            ReplicaServerName = _serverName,
            Role = _role,
            ConnectedState = _connectedState,
            AvailabilityMode = _availabilityMode,
            SynchronizationHealth = _syncHealth,
            OperationalState = _operationalState,
            RecoveryHealth = _recoveryHealth,
            DatabaseCount = _databases.Count
        };

        foreach (var db in _databases)
        {
            replica.DatabaseStates.Add(db.Build());
        }

        return replica;
    }
}

public class DatabaseStateBuilder
{
    private readonly string _databaseName;
    private readonly string _replicaServerName;
    private readonly string _agName;
    private long _logBlockDifference;
    private bool _isSuspended;
    private string? _suspendReason;
    private SynchronizationState _syncState = SynchronizationState.Synchronized;

    public DatabaseStateBuilder(string databaseName, string replicaServerName, string agName)
    {
        _databaseName = databaseName;
        _replicaServerName = replicaServerName;
        _agName = agName;
    }

    public DatabaseStateBuilder WithLogBlockDifference(long diff) { _logBlockDifference = diff; return this; }
    public DatabaseStateBuilder IsSuspended(bool suspended, string? reason = null)
    {
        _isSuspended = suspended;
        _suspendReason = reason;
        return this;
    }
    public DatabaseStateBuilder WithSyncState(SynchronizationState state) { _syncState = state; return this; }

    public DatabaseReplicaState Build()
    {
        return new DatabaseReplicaState
        {
            DatabaseName = _databaseName,
            ReplicaServerName = _replicaServerName,
            AgName = _agName,
            LogBlockDifference = _logBlockDifference,
            IsSuspended = _isSuspended,
            SuspendReason = _suspendReason,
            SynchronizationState = _syncState,
            IsLocal = true
        };
    }
}
