namespace SqlAgMonitor.Core.Models;

public class DatabaseReplicaState : ObservableModel
{
    private string _databaseName = string.Empty;
    private string _replicaServerName = string.Empty;
    private string _agName = string.Empty;
    private bool _isLocal;
    private SynchronizationState _synchronizationState;
    private decimal _lastHardenedLsn;
    private decimal _lastCommitLsn;
    private long _logSendQueueSizeKb;
    private long _redoQueueSizeKb;
    private long _logSendRateKbPerSec;
    private long _redoRateKbPerSec;
    private bool _isSuspended;
    private string? _suspendReason;
    private AvailabilityMode _availabilityMode;
    private decimal _logBlockDifference;
    private long _secondaryLagSeconds;

    public string DatabaseName { get => _databaseName; set => SetProperty(ref _databaseName, value); }
    public string ReplicaServerName { get => _replicaServerName; set => SetProperty(ref _replicaServerName, value); }
    public string AgName { get => _agName; set => SetProperty(ref _agName, value); }
    public bool IsLocal { get => _isLocal; set => SetProperty(ref _isLocal, value); }
    public SynchronizationState SynchronizationState { get => _synchronizationState; set => SetProperty(ref _synchronizationState, value); }
    public decimal LastHardenedLsn { get => _lastHardenedLsn; set => SetProperty(ref _lastHardenedLsn, value); }
    public decimal LastCommitLsn { get => _lastCommitLsn; set => SetProperty(ref _lastCommitLsn, value); }
    public long LogSendQueueSizeKb { get => _logSendQueueSizeKb; set => SetProperty(ref _logSendQueueSizeKb, value); }
    public long RedoQueueSizeKb { get => _redoQueueSizeKb; set => SetProperty(ref _redoQueueSizeKb, value); }
    public long LogSendRateKbPerSec { get => _logSendRateKbPerSec; set => SetProperty(ref _logSendRateKbPerSec, value); }
    public long RedoRateKbPerSec { get => _redoRateKbPerSec; set => SetProperty(ref _redoRateKbPerSec, value); }
    public bool IsSuspended { get => _isSuspended; set => SetProperty(ref _isSuspended, value); }
    public string? SuspendReason { get => _suspendReason; set => SetProperty(ref _suspendReason, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => SetProperty(ref _availabilityMode, value); }

    /// <summary>
    /// Log block position difference from the primary replica, computed by stripping
    /// the slot component from the numeric(25,0) LSN before subtracting.
    /// </summary>
    public decimal LogBlockDifference { get => _logBlockDifference; set => SetProperty(ref _logBlockDifference, value); }

    /// <summary>
    /// Synchronization delay in seconds (from sys.dm_hadr_database_replica_states.secondary_lag_seconds).
    /// Only populated on the primary replica for secondary database rows. SQL Server 2016+.
    /// </summary>
    public long SecondaryLagSeconds { get => _secondaryLagSeconds; set => SetProperty(ref _secondaryLagSeconds, value); }
}
