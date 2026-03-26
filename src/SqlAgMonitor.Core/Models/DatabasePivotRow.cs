namespace SqlAgMonitor.Core.Models;

/// <summary>
/// A pivoted row representing one database, with Last Hardened LSN values
/// per replica stored in an indexer for dynamic column binding.
/// </summary>
public class DatabasePivotRow
{
    private decimal[] _replicaLsns = [];
    private string[] _replicaSyncStates = [];

    public string DatabaseName { get; init; } = string.Empty;
    public decimal MaxLsnDiff { get; init; }
    public string WorstSyncState { get; init; } = string.Empty;
    public bool AnySuspended { get; init; }

    /// <summary>Health color hex for the database row dot, based on MaxLsnDiff.</summary>
    public string HealthColorHex => HealthLevelExtensions.FromLsnDifference(MaxLsnDiff) switch
    {
        HealthLevel.InSync => "#4CAF50",
        HealthLevel.SlightlyBehind => "#FFC107",
        HealthLevel.ModeratelyBehind => "#FF9800",
        HealthLevel.DangerZone => "#F44336",
        _ => "#9E9E9E"
    };

    public void SetReplicaValues(decimal[] lsns, string[] syncStates)
    {
        _replicaLsns = lsns;
        _replicaSyncStates = syncStates;
    }

    /// <summary>
    /// Integer indexer for DataGrid column binding (e.g. Binding path "[0]", "[1]").
    /// Returns the Last Hardened LSN for the replica at the given position.
    /// </summary>
    public decimal this[int index] =>
        index >= 0 && index < _replicaLsns.Length ? _replicaLsns[index] : 0;

    /// <summary>
    /// Gets the sync state for the replica at the given position.
    /// </summary>
    public string GetSyncState(int index) =>
        index >= 0 && index < _replicaSyncStates.Length ? _replicaSyncStates[index] : string.Empty;
}

/// <summary>
/// Metadata for a dynamic replica column in the pivot grid.
/// </summary>
public class ReplicaColumnInfo
{
    public string ReplicaName { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public int Index { get; init; }

    public string Header => IsPrimary ? $"{ReplicaName} (P)" : $"{ReplicaName} (S)";
}
