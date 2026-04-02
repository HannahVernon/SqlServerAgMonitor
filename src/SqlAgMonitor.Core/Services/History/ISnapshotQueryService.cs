using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Queries snapshot data and available filter options for statistics display.
/// </summary>
public interface ISnapshotQueryService
{
    Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(DateTimeOffset since, DateTimeOffset until, string? groupName = null, string? replicaName = null, string? databaseName = null, CancellationToken cancellationToken = default);
    Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(string? groupName = null, string? replicaName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the best snapshot tier that actually contains data for the
    /// given time range. Starts with the preferred tier for the range duration
    /// and falls back to lower tiers if the preferred tier is empty.
    /// </summary>
    Task<SnapshotTier> ResolveTierAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken = default);
}
