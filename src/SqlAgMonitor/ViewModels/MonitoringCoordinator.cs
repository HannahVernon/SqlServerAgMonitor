using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.Export;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.ViewModels;

/// <summary>
/// Coordinates AG/DAG monitoring: subscribes to snapshot streams, manages tabs,
/// feeds snapshots to the alert engine, and records statistics.
/// </summary>
public sealed class MonitoringCoordinator : IMonitoringCoordinator
{
    private readonly AgMonitorService _agMonitor;
    private readonly DagMonitorService _dagMonitor;
    private readonly IAlertEngine _alertEngine;
    private readonly AlertDispatcher _alertDispatcher;
    private readonly IEventRecorder _eventRecorder;
    private readonly IConfigurationService _configService;
    private readonly IHtmlExportService _exportService;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly Dictionary<string, MonitoredGroupSnapshot> _previousSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<MonitorTabViewModel> MonitorTabs { get; } = new();

    /// <summary>
    /// Raised on the UI thread after a snapshot is processed and tabs are updated.
    /// The subscriber can update derived UI state (e.g., status text, connection summary).
    /// </summary>
    public event Action<MonitoredGroupSnapshot>? SnapshotProcessed;

    /// <summary>
    /// Raised on the UI thread when an alert fires. The subscriber should update status text
    /// and refresh the alert history panel if visible.
    /// </summary>
    public event Action<AlertEvent>? AlertRaised;

    public MonitoringCoordinator(
        AgMonitorService agMonitor,
        DagMonitorService dagMonitor,
        IAlertEngine alertEngine,
        AlertDispatcher alertDispatcher,
        IEventRecorder eventRecorder,
        IConfigurationService configService,
        IHtmlExportService exportService,
        ILogger<MonitoringCoordinator> logger)
    {
        _agMonitor = agMonitor;
        _dagMonitor = dagMonitor;
        _alertEngine = alertEngine;
        _alertDispatcher = alertDispatcher;
        _eventRecorder = eventRecorder;
        _configService = configService;
        _exportService = exportService;
        _logger = logger;
    }

    /// <summary>
    /// Wires AG/DAG snapshot observables and alert engine subscriptions.
    /// Call once at startup after construction.
    /// </summary>
    public void SubscribeToMonitors()
    {
        var agSub = _agMonitor.Snapshots
            .Subscribe(snapshot => Dispatcher.UIThread.Post(() => OnSnapshotReceived(snapshot)));
        _subscriptions.Add(agSub);

        var dagSub = _dagMonitor.Snapshots
            .Subscribe(snapshot => Dispatcher.UIThread.Post(() => OnSnapshotReceived(snapshot)));
        _subscriptions.Add(dagSub);

        var alertSub = _alertEngine.Alerts
            .Subscribe(alert =>
            {
                _alertDispatcher.Dispatch(alert);
                Dispatcher.UIThread.Post(() => AlertRaised?.Invoke(alert));
            });
        _subscriptions.Add(alertSub);
    }

    /// <summary>
    /// Loads configured groups from settings and starts monitoring each one.
    /// Also starts scheduled HTML export if configured.
    /// </summary>
    public async Task LoadAndStartAsync()
    {
        try
        {
            var config = _configService.Load();
            foreach (var group in config.MonitoredGroups)
            {
                var groupType = Enum.TryParse<AvailabilityGroupType>(group.GroupType, out var gt)
                    ? gt : AvailabilityGroupType.AvailabilityGroup;
                await StartGroupAsync(group.Name, groupType);
            }

            StartScheduledExport();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading monitored groups at startup.");
        }
    }

    public async Task StartGroupAsync(string groupName, AvailabilityGroupType groupType)
    {
        try
        {
            if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                await _dagMonitor.StartMonitoringAsync(groupName);
            else
                await _agMonitor.StartMonitoringAsync(groupName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start monitoring {Group}.", groupName);
        }
    }

    public async Task StopGroupAsync(string groupName, AvailabilityGroupType groupType)
    {
        if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
            await _dagMonitor.StopMonitoringAsync(groupName);
        else
            await _agMonitor.StopMonitoringAsync(groupName);
    }

    public async Task<MonitoredGroupSnapshot> PollOnceAsync(string groupName, AvailabilityGroupType groupType)
    {
        var snapshot = groupType == AvailabilityGroupType.DistributedAvailabilityGroup
            ? await _dagMonitor.PollOnceAsync(groupName)
            : await _agMonitor.PollOnceAsync(groupName);

        // Process through the same pipeline as subscription-based snapshots
        // so the tab, alert engine, and event recorder are all updated.
        Dispatcher.UIThread.VerifyAccess();
        OnSnapshotReceived(snapshot);

        return snapshot;
    }

    public MonitorTabViewModel? FindTab(string name)
    {
        foreach (var tab in MonitorTabs)
        {
            if (string.Equals(tab.TabTitle, name, StringComparison.OrdinalIgnoreCase))
                return tab;
        }
        return null;
    }

    /// <summary>
    /// Provides read-only access to the most recent snapshots for scheduled export.
    /// </summary>
    public IReadOnlyList<MonitoredGroupSnapshot> GetLatestSnapshots()
        => _previousSnapshots.Values.ToList().AsReadOnly();

    public async Task DisposeMonitorsAsync()
    {
        try { await _agMonitor.DisposeAsync(); } catch { }
        try { await _dagMonitor.DisposeAsync(); } catch { }
        try { await _exportService.StopScheduledExportAsync(); } catch { }
    }

    public void Dispose() => _subscriptions.Dispose();

    private void OnSnapshotReceived(MonitoredGroupSnapshot snapshot)
    {
        var existing = FindTab(snapshot.Name);
        if (existing is null)
        {
            existing = new MonitorTabViewModel { TabTitle = snapshot.Name, GroupType = snapshot.GroupType };
            MonitorTabs.Add(existing);
        }

        existing.ApplySnapshot(snapshot);

        // Feed to alert engine
        _previousSnapshots.TryGetValue(snapshot.Name, out var previous);
        _alertEngine.EvaluateSnapshot(snapshot, previous);
        _previousSnapshots[snapshot.Name] = snapshot;

        // Record snapshot statistics
        _ = _eventRecorder.RecordSnapshotAsync(snapshot);

        SnapshotProcessed?.Invoke(snapshot);
    }

    private void StartScheduledExport()
    {
        try
        {
            _exportService.StartScheduledExportAsync(() => GetLatestSnapshots());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start scheduled HTML export.");
        }
    }
}
