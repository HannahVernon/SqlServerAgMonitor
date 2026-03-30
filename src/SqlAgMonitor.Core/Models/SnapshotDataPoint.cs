namespace SqlAgMonitor.Core.Models;

/// <summary>
/// A single data point from the snapshots, snapshot_hourly, or snapshot_daily tables.
/// For raw data, min/max/avg are the same as the raw value.
/// For summary tiers, they represent the aggregated range within the bucket.
/// </summary>
public class SnapshotDataPoint
{
    public DateTimeOffset Timestamp { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public string ReplicaName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public int SampleCount { get; init; } = 1;

    // Queue metrics
    public long LogSendQueueKbMin { get; init; }
    public long LogSendQueueKbMax { get; init; }
    public double LogSendQueueKbAvg { get; init; }
    public long RedoQueueKbMin { get; init; }
    public long RedoQueueKbMax { get; init; }
    public double RedoQueueKbAvg { get; init; }

    // Rate metrics
    public long LogSendRateMin { get; init; }
    public long LogSendRateMax { get; init; }
    public double LogSendRateAvg { get; init; }
    public long RedoRateMin { get; init; }
    public long RedoRateMax { get; init; }
    public double RedoRateAvg { get; init; }

    // Sync drift
    public decimal LogBlockDiffMin { get; init; }
    public decimal LogBlockDiffMax { get; init; }
    public double LogBlockDiffAvg { get; init; }

    // Replication lag
    public long SecondaryLagMin { get; init; }
    public long SecondaryLagMax { get; init; }
    public double SecondaryLagAvg { get; init; }

    // Categorical / state
    public string Role { get; init; } = string.Empty;
    public string SyncState { get; init; } = string.Empty;
    public bool AnySuspended { get; init; }

    // LSN (last value in bucket)
    public decimal LastHardenedLsn { get; init; }
    public decimal LastCommitLsn { get; init; }

    /// <summary>Which data tier this point came from.</summary>
    public SnapshotTier Tier { get; init; }
}

public enum SnapshotTier
{
    Raw,
    Hourly,
    Daily
}

/// <summary>
/// Available filter values for the statistics window dropdowns.
/// </summary>
public class SnapshotFilterOptions
{
    public IReadOnlyList<string> GroupNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReplicaNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DatabaseNames { get; init; } = Array.Empty<string>();
}
