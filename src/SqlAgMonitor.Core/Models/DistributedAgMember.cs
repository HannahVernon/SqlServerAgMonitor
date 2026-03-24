using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class DistributedAgMember : ReactiveObject
{
    private string _memberAgName = string.Empty;
    private ReplicaRole _distributedRole;
    private AvailabilityMode _availabilityMode;
    private SynchronizationHealth _synchronizationHealth;
    private AvailabilityGroupInfo? _localAgInfo;

    public string MemberAgName { get => _memberAgName; set => this.RaiseAndSetIfChanged(ref _memberAgName, value); }
    public ReplicaRole DistributedRole { get => _distributedRole; set => this.RaiseAndSetIfChanged(ref _distributedRole, value); }
    public AvailabilityMode AvailabilityMode { get => _availabilityMode; set => this.RaiseAndSetIfChanged(ref _availabilityMode, value); }
    public SynchronizationHealth SynchronizationHealth { get => _synchronizationHealth; set => this.RaiseAndSetIfChanged(ref _synchronizationHealth, value); }
    public AvailabilityGroupInfo? LocalAgInfo { get => _localAgInfo; set => this.RaiseAndSetIfChanged(ref _localAgInfo, value); }
}
