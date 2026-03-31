using System.Collections.ObjectModel;

namespace SqlAgMonitor.Core.Models;

public class DistributedAgInfo : ObservableModel
{
    private string _dagName = string.Empty;
    private Guid _dagGroupId;
    private SynchronizationHealth _overallHealth;

    public string DagName { get => _dagName; set => SetProperty(ref _dagName, value); }
    public Guid DagGroupId { get => _dagGroupId; set => SetProperty(ref _dagGroupId, value); }
    public SynchronizationHealth OverallHealth { get => _overallHealth; set => SetProperty(ref _overallHealth, value); }

    public ObservableCollection<DistributedAgMember> Members { get; } = new();
}
