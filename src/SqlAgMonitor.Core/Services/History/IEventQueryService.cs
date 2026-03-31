using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Queries alert events from persistent storage.
/// </summary>
public interface IEventQueryService
{
    Task<IReadOnlyList<AlertEvent>> GetEventsAsync(string? groupName = null, DateTimeOffset? since = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<long> GetEventCountAsync(string? groupName = null, CancellationToken cancellationToken = default);
}
