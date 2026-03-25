namespace SqlAgMonitor.Core.Models;

public class LsnComparisonResult
{
    public required string DatabaseName { get; init; }
    public required string PrimaryReplica { get; init; }
    public required string SecondaryReplica { get; init; }
    public decimal PrimaryLastHardenedLsn { get; init; }
    public decimal SecondaryLastHardenedLsn { get; init; }
    public decimal LsnDifference => Math.Abs(PrimaryLastHardenedLsn - SecondaryLastHardenedLsn);
    public bool IsInSync => LsnDifference == 0;
    public SynchronizationState PrimarySyncState { get; init; }
    public SynchronizationState SecondarySyncState { get; init; }
}
