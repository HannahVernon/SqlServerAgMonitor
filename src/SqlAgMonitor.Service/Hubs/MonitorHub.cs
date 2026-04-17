using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;

namespace SqlAgMonitor.Service.Hubs;

/// <summary>
/// SignalR hub for real-time AG/DAG monitoring.
///
/// Server → Client push (called via IHubContext from MonitoringWorker):
///   OnSnapshotReceived(groupName, snapshot)
///   OnAlertFired(alertEvent)
///   OnConnectionStateChanged(groupName, state)
///
/// Client → Server invocations (defined below):
///   GetMonitoredGroups()
///   GetCurrentSnapshots()
///   GetSnapshotHistory(since, until, groupName?, replicaName?, databaseName?)
///   GetSnapshotFilters(groupName?, replicaName?)
///   GetAlertHistory(groupName?, since?, limit)
///   ExportToExcel(since, until, groupName?, replicaName?, databaseName?)
/// </summary>
[Authorize]
public sealed class MonitorHub : Hub
{
    private readonly ILogger<MonitorHub> _logger;
    private readonly IConfigurationService _configService;
    private readonly ISnapshotQueryService _snapshotQuery;
    private readonly IEventQueryService _eventQuery;
    private readonly MonitoringWorker _monitoringWorker;

    public MonitorHub(
        ILogger<MonitorHub> logger,
        IConfigurationService configService,
        ISnapshotQueryService snapshotQuery,
        IEventQueryService eventQuery,
        MonitoringWorker monitoringWorker)
    {
        _logger = logger;
        _configService = configService;
        _snapshotQuery = snapshotQuery;
        _eventQuery = eventQuery;
        _monitoringWorker = monitoringWorker;
    }

    public override Task OnConnectedAsync()
    {
        var user = Context.User?.Identity?.Name ?? "anonymous";
        _logger.LogInformation("Client connected: {ConnectionId} (user: {User})", Context.ConnectionId, user);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var user = Context.User?.Identity?.Name ?? "anonymous";
        _logger.LogInformation("Client disconnected: {ConnectionId} (user: {User})", Context.ConnectionId, user);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Returns the list of configured AG/DAG groups.</summary>
    public List<MonitoredGroupInfo> GetMonitoredGroups()
    {
        var config = _configService.Load();
        return config.MonitoredGroups
            .Select(g => new MonitoredGroupInfo
            {
                Name = g.Name,
                GroupType = g.GroupType
            })
            .ToList();
    }

    /// <summary>Returns the latest snapshot for each monitored group.</summary>
    public IReadOnlyList<MonitoredGroupSnapshot> GetCurrentSnapshots()
    {
        return _monitoringWorker.GetLatestSnapshots();
    }

    /// <summary>Returns historical snapshot data points for the statistics/trends view.</summary>
    public async Task<IReadOnlyList<SnapshotDataPoint>> GetSnapshotHistory(
        DateTimeOffset since,
        DateTimeOffset until,
        string? groupName = null,
        string? replicaName = null,
        string? databaseName = null)
    {
        return await _snapshotQuery.GetSnapshotDataAsync(
            since, until, groupName, replicaName, databaseName, Context.ConnectionAborted);
    }

    /// <summary>Returns distinct group/replica/database values for filter dropdowns.</summary>
    public async Task<SnapshotFilterOptions> GetSnapshotFilters(
        string? groupName = null,
        string? replicaName = null)
    {
        return await _snapshotQuery.GetSnapshotFiltersAsync(
            groupName, replicaName, Context.ConnectionAborted);
    }

    /// <summary>Returns alert history events.</summary>
    public async Task<IReadOnlyList<AlertEvent>> GetAlertHistory(
        string? groupName = null,
        DateTimeOffset? since = null,
        int limit = 500)
    {
        limit = Math.Clamp(limit, 1, 5000);
        return await _eventQuery.GetEventsAsync(
            groupName, since, limit, Context.ConnectionAborted);
    }

    /// <summary>Returns an Excel file as a byte array for client-side download.</summary>
    public async Task<byte[]> ExportToExcel(
        DateTimeOffset since,
        DateTimeOffset until,
        string? groupName = null,
        string? replicaName = null,
        string? databaseName = null)
    {
        var data = await _snapshotQuery.GetSnapshotDataAsync(
            since, until, groupName, replicaName, databaseName, Context.ConnectionAborted);

        return ExcelExporter.Export(data);
    }
}

/// <summary>Lightweight DTO for group info sent to clients.</summary>
public class MonitoredGroupInfo
{
    public string Name { get; set; } = string.Empty;
    public string GroupType { get; set; } = string.Empty;
}
