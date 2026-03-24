namespace SqlAgMonitor.Core.Models;

public class MonitoredGroupSnapshot
{
    public required string Name { get; init; }
    public required AvailabilityGroupType GroupType { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public AvailabilityGroupInfo? AgInfo { get; init; }
    public DistributedAgInfo? DagInfo { get; init; }
    public SynchronizationHealth OverallHealth { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsConnected { get; init; }
}
