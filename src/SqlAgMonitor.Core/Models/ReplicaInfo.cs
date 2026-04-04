using System.Collections.ObjectModel;

namespace SqlAgMonitor.Core.Models;

public class ReplicaInfo : ObservableModel
{
    private string _agName = string.Empty;
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

    public string AgName { get => _agName; set => SetProperty(ref _agName, value); }
    public string ReplicaServerName { get => _replicaServerName; set => SetProperty(ref _replicaServerName, value); }
    public ReplicaRole Role { get => _role; set => SetProperty(ref _role, value); }
    public OperationalState OperationalState { get => _operationalState; set => SetProperty(ref _operationalState, value); }
    public ConnectedState ConnectedState { get => _connectedState; set => SetProperty(ref _connectedState, value); }
    public RecoveryHealth RecoveryHealth { get => _recoveryHealth; set => SetProperty(ref _recoveryHealth, value); }
    public SynchronizationHealth SynchronizationHealth { get => _synchronizationHealth; set => SetProperty(ref _synchronizationHealth, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => SetProperty(ref _availabilityMode, value); }
    public string? FailoverMode { get => _failoverMode; set => SetProperty(ref _failoverMode, value); }
    public int DatabaseCount { get => _databaseCount; set => SetProperty(ref _databaseCount, value); }
    public string? EndpointUrl { get => _endpointUrl; set => SetProperty(ref _endpointUrl, value); }

    public ObservableCollection<DatabaseReplicaState> DatabaseStates { get; set; } = new();
}
