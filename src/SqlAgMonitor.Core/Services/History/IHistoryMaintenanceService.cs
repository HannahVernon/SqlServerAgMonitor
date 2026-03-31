namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Manages event history lifecycle: initialization, pruning, snapshot summarization, and disposal.
/// </summary>
public interface IHistoryMaintenanceService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default);
    Task SummarizeSnapshotsAsync(int rawRetentionHours = 48, int hourlyRetentionDays = 90, int dailyRetentionDays = 730, CancellationToken cancellationToken = default);
}
