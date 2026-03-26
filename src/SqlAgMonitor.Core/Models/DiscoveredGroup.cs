using ReactiveUI;

namespace SqlAgMonitor.Core.Models;

public class DiscoveredGroup : ReactiveObject
{
    private bool _isSelected = true;

    public required string Name { get; init; }
    public required AvailabilityGroupType GroupType { get; init; }
    public required ReplicaRole LocalRole { get; init; }
    public required string ServerName { get; init; }
    public List<string> ReplicaServers { get; init; } = new();

    /// <summary>Whether this group is selected for monitoring in the wizard.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
