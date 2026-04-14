namespace SqlAgMonitor.Core.Models;

public enum HealthLevel
{
    /// <summary>Green: same VLF and block offset ≤ 1 MB — fully in sync</summary>
    InSync,
    /// <summary>Yellow: same VLF and block offset ≤ 100 MB — slightly behind</summary>
    SlightlyBehind,
    /// <summary>Orange: same VLF and block offset ≤ 1 GB — moderately behind</summary>
    ModeratelyBehind,
    /// <summary>Red: different VLFs, large offset, or disconnected</summary>
    DangerZone
}

public static class HealthLevelExtensions
{
    /// <summary>
    /// Determines health level from block-offset byte difference and VLF gap.
    /// </summary>
    public static HealthLevel FromLogBlockDifference(long blockDiff, long vlfDiff = 0, bool isDisconnected = false)
    {
        if (isDisconnected) return HealthLevel.DangerZone;
        if (vlfDiff > 0) return HealthLevel.DangerZone;

        return blockDiff switch
        {
            <= 1_000_000L => HealthLevel.InSync,
            <= 100_000_000L => HealthLevel.SlightlyBehind,
            <= 1_000_000_000L => HealthLevel.ModeratelyBehind,
            _ => HealthLevel.DangerZone
        };
    }
}
