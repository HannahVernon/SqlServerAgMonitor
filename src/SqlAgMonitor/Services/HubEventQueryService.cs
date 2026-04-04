using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Services;

/// <summary>
/// Adapter that implements <see cref="IEventQueryService"/> by delegating to
/// <see cref="ServiceMonitoringClient"/> hub methods. Used in service-client mode
/// so that AlertHistoryViewModel works without any code changes.
/// </summary>
public sealed class HubEventQueryService : IEventQueryService
{
    private readonly ServiceMonitoringClient _client;

    public HubEventQueryService(ServiceMonitoringClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyList<AlertEvent>> GetEventsAsync(
        string? groupName = null, DateTimeOffset? since = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return _client.GetAlertHistoryAsync(groupName, since, limit, cancellationToken);
    }

    public Task<long> GetEventCountAsync(
        string? groupName = null, CancellationToken cancellationToken = default)
    {
        // The hub doesn't expose a dedicated count method; fetch minimal data and count.
        // This keeps the adapter self-contained without requiring a hub API change.
        return Task.Run(async () =>
        {
            var events = await _client.GetAlertHistoryAsync(groupName, limit: 5000, cancellationToken: cancellationToken);
            return (long)events.Count;
        }, cancellationToken);
    }
}
