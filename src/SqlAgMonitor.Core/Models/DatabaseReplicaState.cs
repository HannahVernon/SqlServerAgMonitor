using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class DatabaseReplicaState : ReactiveObject
{
    private string _databaseName = string.Empty;
    private string _replicaServerName = string.Empty;
    private string _agName = string.Empty;
    private bool _isLocal;
    private SynchronizationState _synchronizationState;
    private long _lastHardenedLsn;
    private long _lastCommitLsn;
    private long _logSendQueueSizeKb;
    private long _redoQueueSizeKb;
    private long _logSendRateKbPerSec;
    private long _redoRateKbPerSec;
    private long _estimatedRecoveryTimeSeconds;
    private bool _isSuspended;
    private string? _suspendReason;
    private AvailabilityMode _availabilityMode;
    private long _lsnDifferenceFromPrimary;

    public string DatabaseName { get => _databaseName; set => this.RaiseAndSetIfChanged(ref _databaseName, value); }
    public string ReplicaServerName { get => _replicaServerName; set => this.RaiseAndSetIfChanged(ref _replicaServerName, value); }
    public string AgName { get => _agName; set => this.RaiseAndSetIfChanged(ref _agName, value); }
    public bool IsLocal { get => _isLocal; set => this.RaiseAndSetIfChanged(ref _isLocal, value); }
    public SynchronizationState SynchronizationState { get => _synchronizationState; set => this.RaiseAndSetIfChanged(ref _synchronizationState, value); }
    public long LastHardenedLsn { get => _lastHardenedLsn; set => this.RaiseAndSetIfChanged(ref _lastHardenedLsn, value); }
    public long LastCommitLsn { get => _lastCommitLsn; set => this.RaiseAndSetIfChanged(ref _lastCommitLsn, value); }
    public long LogSendQueueSizeKb { get => _logSendQueueSizeKb; set => this.RaiseAndSetIfChanged(ref _logSendQueueSizeKb, value); }
    public long RedoQueueSizeKb { get => _redoQueueSizeKb; set => this.RaiseAndSetIfChanged(ref _redoQueueSizeKb, value); }
    public long LogSendRateKbPerSec { get => _logSendRateKbPerSec; set => this.RaiseAndSetIfChanged(ref _logSendRateKbPerSec, value); }
    public long RedoRateKbPerSec { get => _redoRateKbPerSec; set => this.RaiseAndSetIfChanged(ref _redoRateKbPerSec, value); }
    public long EstimatedRecoveryTimeSeconds { get => _estimatedRecoveryTimeSeconds; set => this.RaiseAndSetIfChanged(ref _estimatedRecoveryTimeSeconds, value); }
    public bool IsSuspended { get => _isSuspended; set => this.RaiseAndSetIfChanged(ref _isSuspended, value); }
    public string? SuspendReason { get => _suspendReason; set => this.RaiseAndSetIfChanged(ref _suspendReason, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => this.RaiseAndSetIfChanged(ref _availabilityMode, value); }
    public long LsnDifferenceFromPrimary { get => _lsnDifferenceFromPrimary; set => this.RaiseAndSetIfChanged(ref _lsnDifferenceFromPrimary, value); }
}
