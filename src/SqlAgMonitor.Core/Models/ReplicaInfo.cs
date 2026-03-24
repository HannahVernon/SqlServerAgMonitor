using System.Collections.ObjectModel;
using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class ReplicaInfo : ReactiveObject
{
    private string _replicaServerName = string.Empty;
    private ReplicaRole _role;
    private OperationalState _operationalState;
    private ConnectedState _connectedState;
    private RecoveryHealth _recoveryHealth;
    private SynchronizationHealth _synchronizationHealth;
    private AvailabilityMode _availabilityMode;
    private string? _failoverMode;
    private int _databaseCount;
    private string? _endpointUrl;

    public string ReplicaServerName { get => _replicaServerName; set => this.RaiseAndSetIfChanged(ref _replicaServerName, value); }
    public ReplicaRole Role { get => _role; set => this.RaiseAndSetIfChanged(ref _role, value); }
    public OperationalState OperationalState { get => _operationalState; set => this.RaiseAndSetIfChanged(ref _operationalState, value); }
    public ConnectedState ConnectedState { get => _connectedState; set => this.RaiseAndSetIfChanged(ref _connectedState, value); }
    public RecoveryHealth RecoveryHealth { get => _recoveryHealth; set => this.RaiseAndSetIfChanged(ref _recoveryHealth, value); }
    public SynchronizationHealth SynchronizationHealth { get => _synchronizationHealth; set => this.RaiseAndSetIfChanged(ref _synchronizationHealth, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => this.RaiseAndSetIfChanged(ref _availabilityMode, value); }
    public string? FailoverMode { get => _failoverMode; set => this.RaiseAndSetIfChanged(ref _failoverMode, value); }
    public int DatabaseCount { get => _databaseCount; set => this.RaiseAndSetIfChanged(ref _databaseCount, value); }
    public string? EndpointUrl { get => _endpointUrl; set => this.RaiseAndSetIfChanged(ref _endpointUrl, value); }

    public ObservableCollection<DatabaseReplicaState> DatabaseStates { get; } = new();
}
