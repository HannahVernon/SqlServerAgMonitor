namespace SqlAgMonitor.Core.Models;

public enum HealthLevel
{
    /// <summary>Green: 0-10 LSN diff, fully in sync</summary>
    InSync,
    /// <summary>Yellow: 11-100 LSN diff, slightly behind</summary>
    SlightlyBehind,
    /// <summary>Orange: 101-200 LSN diff, moderately behind</summary>
    ModeratelyBehind,
    /// <summary>Red: >200 LSN diff or disconnected</summary>
    DangerZone
}

public static class HealthLevelExtensions
{
    public static HealthLevel FromLsnDifference(long lsnDifference, bool isDisconnected = false)
    {
        if (isDisconnected) return HealthLevel.DangerZone;

        return lsnDifference switch
        {
            <= 10 => HealthLevel.InSync,
            <= 100 => HealthLevel.SlightlyBehind,
            <= 200 => HealthLevel.ModeratelyBehind,
            _ => HealthLevel.DangerZone
        };
    }
}
