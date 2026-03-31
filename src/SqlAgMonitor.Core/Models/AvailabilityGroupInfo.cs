using System.Collections.ObjectModel;

namespace SqlAgMonitor.Core.Models;

public class AvailabilityGroupInfo : ObservableModel
{
    private string _agName = string.Empty;
    private Guid _agGroupId;
    private SynchronizationHealth _overallHealth;

    public string AgName { get => _agName; set => SetProperty(ref _agName, value); }
    public Guid AgGroupId { get => _agGroupId; set => SetProperty(ref _agGroupId, value); }
    public SynchronizationHealth OverallHealth { get => _overallHealth; set => SetProperty(ref _overallHealth, value); }

    public ObservableCollection<ReplicaInfo> Replicas { get; } = new();
}
