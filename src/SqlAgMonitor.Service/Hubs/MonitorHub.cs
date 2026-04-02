using Microsoft.AspNetCore.SignalR;

namespace SqlAgMonitor.Service.Hubs;

/// <summary>
/// SignalR hub for real-time AG/DAG monitoring.
///
/// Server → Client push:
///   OnSnapshotReceived(groupName, snapshot)
///   OnAlertFired(alertEvent)
///   OnConnectionStateChanged(groupName, state)
///
/// Client → Server invocations:
///   GetMonitoredGroups()
///   GetCurrentSnapshots()
///   GetSnapshotHistory(filters)
///   GetSnapshotFilters()
///   GetAlertHistory(timeRange)
///   ExportToExcel(filters)
/// </summary>
public sealed class MonitorHub : Hub
{
    private readonly ILogger<MonitorHub> _logger;

    public MonitorHub(ILogger<MonitorHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // TODO: Phase 2 — add hub invocation methods for GetMonitoredGroups,
    // GetCurrentSnapshots, GetSnapshotHistory, GetSnapshotFilters,
    // GetAlertHistory, ExportToExcel
}
