using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

public interface IEventHistoryService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);
    Task RecordSnapshotAsync(MonitoredGroupSnapshot snapshot, CancellationToken cancellationToken = default);
    Task SummarizeSnapshotsAsync(int rawRetentionHours = 48, int hourlyRetentionDays = 90, int dailyRetentionDays = 730, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default);
    Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(DateTimeOffset since, DateTimeOffset until, string? groupName = null, string? replicaName = null, string? databaseName = null, CancellationToken cancellationToken = default);
    Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(CancellationToken cancellationToken = default);
}
