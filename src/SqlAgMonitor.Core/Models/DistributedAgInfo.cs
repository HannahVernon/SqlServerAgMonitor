using System.Collections.ObjectModel;
using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class DistributedAgInfo : ReactiveObject
{
    private string _dagName = string.Empty;
    private Guid _dagGroupId;
    private SynchronizationHealth _overallHealth;

    public string DagName { get => _dagName; set => this.RaiseAndSetIfChanged(ref _dagName, value); }
    public Guid DagGroupId { get => _dagGroupId; set => this.RaiseAndSetIfChanged(ref _dagGroupId, value); }
    public SynchronizationHealth OverallHealth { get => _overallHealth; set => this.RaiseAndSetIfChanged(ref _overallHealth, value); }

    public ObservableCollection<DistributedAgMember> Members { get; } = new();
}
