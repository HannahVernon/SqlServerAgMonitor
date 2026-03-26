namespace SqlAgMonitor.Core.Models;

public enum HealthLevel
{
    /// <summary>Green: log block difference ≤ 1MB offset — fully in sync</summary>
    InSync,
    /// <summary>Yellow: log block difference ≤ 100MB offset — slightly behind</summary>
    SlightlyBehind,
    /// <summary>Orange: log block difference ≤ 10GB offset (within same VLF range) — moderately behind</summary>
    ModeratelyBehind,
    /// <summary>Red: large difference, VLF boundary crossed, or disconnected</summary>
    DangerZone
}

public static class HealthLevelExtensions
{
    /// <summary>
    /// Determines health level from a log block position difference.
    /// The difference is computed by stripping the slot from numeric(25,0) LSN values
    /// before subtracting. Within the same VLF, the value represents byte-offset
    /// distance in the transaction log. Across VLF boundaries each boundary adds ~10^10.
    /// </summary>
    public static HealthLevel FromLogBlockDifference(decimal logBlockDiff, bool isDisconnected = false)
    {
        if (isDisconnected) return HealthLevel.DangerZone;

        return logBlockDiff switch
        {
            <= 1_000_000m => HealthLevel.InSync,            // ≤ ~1 MB log offset
            <= 100_000_000m => HealthLevel.SlightlyBehind,  // ≤ ~100 MB log offset
            <= 10_000_000_000m => HealthLevel.ModeratelyBehind, // within same VLF range
            _ => HealthLevel.DangerZone                     // VLF boundary crossed or massive lag
        };
    }
}
