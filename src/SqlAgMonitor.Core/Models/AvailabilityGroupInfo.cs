using System.Collections.ObjectModel;
using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class AvailabilityGroupInfo : ReactiveObject
{
    private string _agName = string.Empty;
    private Guid _agGroupId;
    private SynchronizationHealth _overallHealth;

    public string AgName { get => _agName; set => this.RaiseAndSetIfChanged(ref _agName, value); }
    public Guid AgGroupId { get => _agGroupId; set => this.RaiseAndSetIfChanged(ref _agGroupId, value); }
    public SynchronizationHealth OverallHealth { get => _overallHealth; set => this.RaiseAndSetIfChanged(ref _overallHealth, value); }

    public ObservableCollection<ReplicaInfo> Replicas { get; } = new();
}
