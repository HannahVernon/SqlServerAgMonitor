namespace SqlAgMonitor.Core.Models;

public class DiscoveredGroup
{
    public required string Name { get; init; }
    public required AvailabilityGroupType GroupType { get; init; }
    public required ReplicaRole LocalRole { get; init; }
    public required string ServerName { get; init; }
    public List<string> ReplicaServers { get; init; } = new();
}
