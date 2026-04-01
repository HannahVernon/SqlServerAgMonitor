using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Thin facade over focused DuckDB stores. Implements the composite
/// <see cref="IEventHistoryService"/> interface and delegates to:
/// <list type="bullet">
///   <item><see cref="DuckDbConnectionManager"/> — connection lifecycle, locking, schema</item>
///   <item><see cref="DuckDbEventStore"/> — alert event recording and querying</item>
///   <item><see cref="DuckDbSnapshotStore"/> — snapshot recording and querying</item>
///   <item><see cref="DuckDbSnapshotAggregator"/> — roll-up summarization and pruning</item>
/// </list>
/// </summary>
public class DuckDbEventHistoryService : IEventHistoryService
{
    private readonly DuckDbConnectionManager _connectionManager;
    private readonly DuckDbEventStore _eventStore;
    private readonly DuckDbSnapshotStore _snapshotStore;
    private readonly DuckDbSnapshotAggregator _aggregator;

    public DuckDbEventHistoryService(ILoggerFactory loggerFactory, string? dataDirectory = null)
    {
        _connectionManager = new DuckDbConnectionManager(
            loggerFactory.CreateLogger<DuckDbConnectionManager>(), dataDirectory);
        _eventStore = new DuckDbEventStore(
            _connectionManager, loggerFactory.CreateLogger<DuckDbEventStore>());
        _snapshotStore = new DuckDbSnapshotStore(
            _connectionManager, loggerFactory.CreateLogger<DuckDbSnapshotStore>());
        _aggregator = new DuckDbSnapshotAggregator(
            _connectionManager, loggerFactory.CreateLogger<DuckDbSnapshotAggregator>());
    }

    // IHistoryMaintenanceService

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _connectionManager.InitializeAsync(cancellationToken);

    public Task<long> PruneEventsAsync(int? maxAgeDays, int? maxRecords, CancellationToken cancellationToken = default)
        => _eventStore.PruneEventsAsync(maxAgeDays, maxRecords, cancellationToken);

    public Task SummarizeSnapshotsAsync(int rawRetentionHours = 48, int hourlyRetentionDays = 90, int dailyRetentionDays = 730, CancellationToken cancellationToken = default)
        => _aggregator.SummarizeSnapshotsAsync(rawRetentionHours, hourlyRetentionDays, dailyRetentionDays, cancellationToken);

    public ValueTask DisposeAsync()
        => _connectionManager.DisposeAsync();

    // IEventRecorder

    public Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
        => _eventStore.RecordEventAsync(alertEvent, cancellationToken);

    public Task RecordSnapshotAsync(MonitoredGroupSnapshot snapshot, CancellationToken cancellationToken = default)
        => _snapshotStore.RecordSnapshotAsync(snapshot, cancellationToken);

    // IEventQueryService

    public Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default)
        => _eventStore.GetEventsAsync(groupName, since, limit, cancellationToken);

    public Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default)
        => _eventStore.GetEventCountAsync(groupName, cancellationToken);

    // ISnapshotQueryService

    public Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(DateTimeOffset since, DateTimeOffset until, string? groupName = null, string? replicaName = null, string? databaseName = null, CancellationToken cancellationToken = default)
        => _snapshotStore.GetSnapshotDataAsync(since, until, groupName, replicaName, databaseName, cancellationToken);

    public Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(string? groupName = null, string? replicaName = null, CancellationToken cancellationToken = default)
        => _snapshotStore.GetSnapshotFiltersAsync(groupName, replicaName, cancellationToken);
}
