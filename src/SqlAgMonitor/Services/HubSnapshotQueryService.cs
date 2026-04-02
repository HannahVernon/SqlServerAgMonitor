using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Services;

/// <summary>
/// Adapter that implements <see cref="ISnapshotQueryService"/> by delegating to
/// <see cref="ServiceMonitoringClient"/> hub methods. Used in service-client mode
/// so that StatisticsViewModel works without any code changes.
/// </summary>
public sealed class HubSnapshotQueryService : ISnapshotQueryService
{
    private readonly ServiceMonitoringClient _client;

    public HubSnapshotQueryService(ServiceMonitoringClient client)
    {
        _client = client;
    }

    public Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotDataAsync(
        DateTimeOffset since, DateTimeOffset until,
        string? groupName = null, string? replicaName = null, string? databaseName = null,
        CancellationToken cancellationToken = default)
    {
        return _client.GetSnapshotHistoryAsync(since, until, groupName, replicaName, databaseName, cancellationToken);
    }

    public Task<SnapshotFilterOptions> GetSnapshotFiltersAsync(
        string? groupName = null, string? replicaName = null,
        CancellationToken cancellationToken = default)
    {
        return _client.GetSnapshotFiltersAsync(groupName, replicaName, cancellationToken);
    }

    /// <summary>
    /// Hub clients don't resolve tiers locally — the server handles fallback
    /// in its own GetSnapshotDataAsync. Return the preferred tier for the range
    /// so the UI has a label to display.
    /// </summary>
    public Task<SnapshotTier> ResolveTierAsync(
        DateTimeOffset since, DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var range = until - since;
        SnapshotTier tier;
        if (range.TotalHours <= 48)
            tier = SnapshotTier.Raw;
        else if (range.TotalDays <= 90)
            tier = SnapshotTier.Hourly;
        else
            tier = SnapshotTier.Daily;
        return Task.FromResult(tier);
    }
}
