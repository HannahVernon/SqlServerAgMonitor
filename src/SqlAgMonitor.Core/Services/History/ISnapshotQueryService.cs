using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Queries snapshot data and available filter options for statistics display.
/// </summary>
public interface ISnapshotQueryService
{
    Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(DateTimeOffset since, DateTimeOffset until, string? groupName = null, string? replicaName = null, string? databaseName = null, CancellationToken cancellationToken = default);
    Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(string? groupName = null, string? replicaName = null, CancellationToken cancellationToken = default);
}
