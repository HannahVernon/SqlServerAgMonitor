using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

public interface IEventHistoryService : IAsyncDisposable
{
    Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);
    Task RecordErrorAsync(string errorType, string message, string? stackTrace, string? context, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default);
}
