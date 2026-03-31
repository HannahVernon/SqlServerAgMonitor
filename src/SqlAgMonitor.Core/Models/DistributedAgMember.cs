namespace SqlAgMonitor.Core.Models;

public class DistributedAgMember : ObservableModel
{
    private string _memberAgName = string.Empty;
    private ReplicaRole _distributedRole;
    private AvailabilityMode _availabilityMode;
    private SynchronizationHealth _synchronizationHealth;
    private AvailabilityGroupInfo? _localAgInfo;

    public string MemberAgName { get => _memberAgName; set => SetProperty(ref _memberAgName, value); }
    public ReplicaRole DistributedRole { get => _distributedRole; set => SetProperty(ref _distributedRole, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => SetProperty(ref _availabilityMode, value); }
    public SynchronizationHealth SynchronizationHealth { get => _synchronizationHealth; set => SetProperty(ref _synchronizationHealth, value); }
    public AvailabilityGroupInfo? LocalAgInfo { get => _localAgInfo; set => SetProperty(ref _localAgInfo, value); }
}
