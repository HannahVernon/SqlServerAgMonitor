namespace SqlAgMonitor.Core.Models;

/// <summary>
/// A pivoted row representing one database, with Last Hardened LSN values
/// per replica stored in an indexer for dynamic column binding.
/// LSN values are formatted as VLF:Block hex notation (slot stripped).
/// </summary>
public class DatabasePivotRow
{
    private string[] _replicaLsnDisplay = [];
    private string[] _replicaSyncStates = [];

    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>Maximum log block position difference from primary across all secondaries.</summary>
    public decimal MaxLogBlockDiff { get; init; }

    /// <summary>Worst secondary lag in seconds across all secondaries for this database.</summary>
    public long SecondaryLagSeconds { get; init; }

    public string WorstSyncState { get; init; } = string.Empty;
    public bool AnySuspended { get; init; }

    /// <summary>Health color hex for the database row dot, based on MaxLogBlockDiff.</summary>
    public string HealthColorHex => HealthLevelExtensions.FromLogBlockDifference(MaxLogBlockDiff) switch
    {
        HealthLevel.InSync => "#4CAF50",
        HealthLevel.SlightlyBehind => "#FFC107",
        HealthLevel.ModeratelyBehind => "#FF9800",
        HealthLevel.DangerZone => "#F44336",
        _ => "#9E9E9E"
    };

    public void SetReplicaValues(string[] lsnDisplayValues, string[] syncStates)
    {
        _replicaLsnDisplay = lsnDisplayValues;
        _replicaSyncStates = syncStates;
    }

    /// <summary>
    /// String indexer for DataGrid column binding (e.g. Binding path "[0]", "[1]").
    /// Returns the Last Hardened LSN formatted as VLF:Block hex for the replica at the given position.
    /// </summary>
    public string this[int index] =>
        index >= 0 && index < _replicaLsnDisplay.Length ? _replicaLsnDisplay[index] : "00000000:00000000";

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
