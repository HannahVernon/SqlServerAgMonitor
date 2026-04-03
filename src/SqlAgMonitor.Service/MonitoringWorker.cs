using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
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
    private readonly ConcurrentQueue<AppConfiguration> _pendingReloads = new();
    private readonly SemaphoreSlim _reloadSignal = new(0);

    /// <summary>
    /// Tracks the currently monitored groups by name → group type, so we can
    /// diff against incoming config changes and stop/start as needed.
    /// </summary>
    private readonly Dictionary<string, AvailabilityGroupType> _activeGroups = new();

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

        _configService.ConfigurationChanged += OnConfigurationChanged;

        try
        {
            SubscribeToMonitors();

            await LoadAndStartMonitoringAsync(stoppingToken);

            StartScheduledExport();

            _logger.LogInformation("MonitoringWorker running — monitoring {Count} group(s)",
                _activeGroups.Count);

            // Event loop: wait for config reload signals or cancellation
            while (!stoppingToken.IsCancellationRequested)
            {
                await _reloadSignal.WaitAsync(stoppingToken);

                // Drain all queued reloads — only the latest matters
                AppConfiguration? latestConfig = null;
                while (_pendingReloads.TryDequeue(out var config))
                    latestConfig = config;

                if (latestConfig != null)
                    await ReconcileMonitoringAsync(latestConfig, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MonitoringWorker stopping");
        }
        finally
        {
            _configService.ConfigurationChanged -= OnConfigurationChanged;
        }
    }

    private void OnConfigurationChanged(AppConfiguration config)
    {
        _pendingReloads.Enqueue(config);
        _reloadSignal.Release();
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
                    _ = SafeSendAsync(() =>
                        _hubContext.Clients.All.SendAsync("OnAlertFired", alert));
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

                _activeGroups[group.Name] = groupType;

                _logger.LogInformation("Started monitoring {Group} ({Type})",
                    group.Name, groupType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start monitoring {Group}", group.Name);
            }
        }
    }

    /// <summary>
    /// Compares the new configuration against the currently active groups and
    /// stops removed groups, starts new groups, and restarts changed groups.
    /// Runs on the MonitoringWorker's own thread to avoid cross-thread issues.
    /// </summary>
    private async Task ReconcileMonitoringAsync(AppConfiguration newConfig, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuration changed — reconciling monitored groups");

        var newGroups = new Dictionary<string, (MonitoredGroupConfig Config, AvailabilityGroupType Type)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in newConfig.MonitoredGroups)
        {
            var groupType = Enum.TryParse<AvailabilityGroupType>(group.GroupType, out var gt)
                ? gt : AvailabilityGroupType.AvailabilityGroup;
            newGroups[group.Name] = (group, groupType);
        }

        // Stop groups that were removed
        var removedGroups = _activeGroups.Keys
            .Where(name => !newGroups.ContainsKey(name))
            .ToList();

        foreach (var name in removedGroups)
        {
            await StopGroupAsync(name);
        }

        // Start new groups and restart groups whose type changed
        foreach (var (name, (_, newType)) in newGroups)
        {
            if (_activeGroups.TryGetValue(name, out var currentType))
            {
                if (currentType != newType)
                {
                    _logger.LogInformation("Group {Group} type changed from {Old} to {New} — restarting",
                        name, currentType, newType);
                    await StopGroupAsync(name);
                    await StartGroupAsync(name, newType, cancellationToken);
                }
            }
            else
            {
                await StartGroupAsync(name, newType, cancellationToken);
            }
        }

        _logger.LogInformation("Reconciliation complete — now monitoring {Count} group(s)",
            _activeGroups.Count);

        // Notify connected SignalR clients to refresh
        _ = SafeSendAsync(() =>
            _hubContext.Clients.All.SendAsync("OnConfigurationChanged", cancellationToken));
    }

    private async Task StartGroupAsync(string name, AvailabilityGroupType groupType, CancellationToken cancellationToken)
    {
        try
        {
            if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                await _dagMonitor.StartMonitoringAsync(name, cancellationToken);
            else
                await _agMonitor.StartMonitoringAsync(name, cancellationToken);

            _activeGroups[name] = groupType;
            _logger.LogInformation("Started monitoring {Group} ({Type})", name, groupType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring {Group}", name);
        }
    }

    private async Task StopGroupAsync(string name)
    {
        try
        {
            if (_activeGroups.TryGetValue(name, out var groupType))
            {
                if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                    await _dagMonitor.StopMonitoringAsync(name);
                else
                    await _agMonitor.StopMonitoringAsync(name);
            }

            _activeGroups.Remove(name);

            lock (_snapshotLock)
            {
                _latestSnapshots.Remove(name);
            }

            _logger.LogInformation("Stopped monitoring {Group}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop monitoring {Group}", name);
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
        _ = SafeSendAsync(() => _eventRecorder.RecordSnapshotAsync(snapshot));

        // Push to all connected SignalR clients
        _ = SafeSendAsync(() =>
            _hubContext.Clients.All.SendAsync("OnSnapshotReceived", snapshot.Name, snapshot));
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

        _configService.ConfigurationChanged -= OnConfigurationChanged;
        _subscriptions.Dispose();
        _reloadSignal.Dispose();

        try { await _exportService.StopScheduledExportAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error stopping export scheduler"); }

        try { await _agMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing AG monitor"); }

        try { await _dagMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing DAG monitor"); }

        await base.StopAsync(cancellationToken);
    }

    private async Task SafeSendAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fire-and-forget async operation failed");
        }
    }
}
