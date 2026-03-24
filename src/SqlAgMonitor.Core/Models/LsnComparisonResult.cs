namespace SqlAgMonitor.Core.Models;

public class LsnComparisonResult
{
    public required string DatabaseName { get; init; }
    public required string PrimaryReplica { get; init; }
    public required string SecondaryReplica { get; init; }
    public long PrimaryLastHardenedLsn { get; init; }
    public long SecondaryLastHardenedLsn { get; init; }
    public long LsnDifference => Math.Abs(PrimaryLastHardenedLsn - SecondaryLastHardenedLsn);
    public bool IsInSync => LsnDifference == 0;
    public SynchronizationState PrimarySyncState { get; init; }
    public SynchronizationState SecondarySyncState { get; init; }
}
