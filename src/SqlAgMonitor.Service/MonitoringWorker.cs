using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.AspNetCore.SignalR;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.Export;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Service.Hubs;

namespace SqlAgMonitor.Service;

/// <summary>
/// Headless monitoring coordinator that subscribes to AG/DAG snapshot observables,
/// feeds them through the alert engine, records events to DuckDB, and pushes
/// real-time updates to connected SignalR clients.
/// </summary>
public sealed class MonitoringWorker : BackgroundService
{
    private readonly ILogger<MonitoringWorker> _logger;
    private readonly AgMonitorService _agMonitor;
    private readonly DagMonitorService _dagMonitor;
    private readonly IAlertEngine _alertEngine;
    private readonly AlertDispatcher _alertDispatcher;
    private readonly IEventRecorder _eventRecorder;
    private readonly IConfigurationService _configService;
    private readonly IHtmlExportService _exportService;
    private readonly IHubContext<MonitorHub> _hubContext;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly Dictionary<string, MonitoredGroupSnapshot> _latestSnapshots = new();
    private readonly object _snapshotLock = new();

    public MonitoringWorker(
        ILogger<MonitoringWorker> logger,
        AgMonitorService agMonitor,
        DagMonitorService dagMonitor,
        IAlertEngine alertEngine,
        AlertDispatcher alertDispatcher,
        IEventRecorder eventRecorder,
        IConfigurationService configService,
        IHtmlExportService exportService,
        IHubContext<MonitorHub> hubContext)
    {
        _logger = logger;
        _agMonitor = agMonitor;
        _dagMonitor = dagMonitor;
        _alertEngine = alertEngine;
        _alertDispatcher = alertDispatcher;
        _eventRecorder = eventRecorder;
        _configService = configService;
        _exportService = exportService;
        _hubContext = hubContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringWorker starting");

        try
        {
            SubscribeToMonitors();

            await LoadAndStartMonitoringAsync(stoppingToken);

            StartScheduledExport();

            _logger.LogInformation("MonitoringWorker running — monitoring {Count} group(s)",
                _latestSnapshots.Count);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MonitoringWorker stopping");
        }
    }

    private void SubscribeToMonitors()
    {
        var agSub = _agMonitor.Snapshots
            .Subscribe(
                snapshot => OnSnapshotReceived(snapshot),
                ex => _logger.LogError(ex, "AG snapshot observable error"));
        _subscriptions.Add(agSub);

        var dagSub = _dagMonitor.Snapshots
            .Subscribe(
                snapshot => OnSnapshotReceived(snapshot),
                ex => _logger.LogError(ex, "DAG snapshot observable error"));
        _subscriptions.Add(dagSub);

        var alertSub = _alertEngine.Alerts
            .Subscribe(
                alert =>
                {
                    _alertDispatcher.Dispatch(alert);
                    _logger.LogInformation("Alert fired: {Type} for {Group}",
                        alert.AlertType, alert.GroupName);

                    // Push to all connected SignalR clients
                    _ = _hubContext.Clients.All.SendAsync("OnAlertFired", alert);
                },
                ex => _logger.LogError(ex, "Alert observable error"));
        _subscriptions.Add(alertSub);
    }

    private async Task LoadAndStartMonitoringAsync(CancellationToken cancellationToken)
    {
        var config = _configService.Load();

        foreach (var group in config.MonitoredGroups)
        {
            try
            {
                var groupType = Enum.TryParse<AvailabilityGroupType>(group.GroupType, out var gt)
                    ? gt : AvailabilityGroupType.AvailabilityGroup;

                if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                    await _dagMonitor.StartMonitoringAsync(group.Name, cancellationToken);
                else
                    await _agMonitor.StartMonitoringAsync(group.Name, cancellationToken);

                _logger.LogInformation("Started monitoring {Group} ({Type})",
                    group.Name, groupType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start monitoring {Group}", group.Name);
            }
        }
    }

    private void OnSnapshotReceived(MonitoredGroupSnapshot snapshot)
    {
        MonitoredGroupSnapshot? previous;
        lock (_snapshotLock)
        {
            _latestSnapshots.TryGetValue(snapshot.Name, out previous);
            _latestSnapshots[snapshot.Name] = snapshot;
        }

        _alertEngine.EvaluateSnapshot(snapshot, previous);
        _ = _eventRecorder.RecordSnapshotAsync(snapshot);

        // Push to all connected SignalR clients
        _ = _hubContext.Clients.All.SendAsync("OnSnapshotReceived", snapshot.Name, snapshot);
    }

    private void StartScheduledExport()
    {
        _exportService.StartScheduledExportAsync(GetLatestSnapshots);
    }

    /// <summary>
    /// Returns the most recent snapshot for each monitored group.
    /// Called by HtmlExportService and available for hub queries.
    /// </summary>
    public IReadOnlyList<MonitoredGroupSnapshot> GetLatestSnapshots()
    {
        lock (_snapshotLock)
        {
            return _latestSnapshots.Values.ToList();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MonitoringWorker shutting down");

        _subscriptions.Dispose();

        try { await _exportService.StopScheduledExportAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping export scheduler"); }

        try { await _agMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing AG monitor"); }

        try { await _dagMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing DAG monitor"); }

        await base.StopAsync(cancellationToken);
    }
}
