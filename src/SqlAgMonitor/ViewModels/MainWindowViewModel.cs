using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Export;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.Views;

namespace SqlAgMonitor.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AgMonitorService _agMonitor;
    private readonly DagMonitorService _dagMonitor;
    private readonly IConfigurationService _configService;
    private readonly IHtmlExportService _exportService;
    private readonly IAlertEngine _alertEngine;
    private readonly IEventRecorder _eventRecorder;
    private readonly IHistoryMaintenanceService _historyMaintenance;
    private readonly IEventQueryService _eventQuery;
    private readonly ISnapshotQueryService _snapshotQuery;
    private readonly IEmailNotificationService _emailService;
    private readonly ISyslogService _syslogService;
    private readonly ISqlConnectionService _connectionService;
    private readonly IAgDiscoveryService _discoveryService;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly Dictionary<string, MonitoredGroupSnapshot> _previousSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private MonitorTabViewModel? _selectedTab;
    private string _statusText = "Ready";
    private string _connectionSummary = "No groups monitored";
    private bool _isAllPaused;
    private string _lastPolledText = string.Empty;
    private bool _isAlertHistoryVisible;
    private AlertHistoryViewModel? _alertHistoryVm;

    public ObservableCollection<MonitorTabViewModel> MonitorTabs { get; } = new();

    public MonitorTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string ConnectionSummary
    {
        get => _connectionSummary;
        set => this.RaiseAndSetIfChanged(ref _connectionSummary, value);
    }

    public bool IsAllPaused
    {
        get => _isAllPaused;
        set => this.RaiseAndSetIfChanged(ref _isAllPaused, value);
    }

    public bool IsNotAllPaused => !_isAllPaused;

    public bool IsAlertHistoryVisible
    {
        get => _isAlertHistoryVisible;
        set => this.RaiseAndSetIfChanged(ref _isAlertHistoryVisible, value);
    }

    public AlertHistoryViewModel AlertHistory => _alertHistoryVm ??= new AlertHistoryViewModel(_eventQuery);

    public string LastPolledText
    {
        get => _lastPolledText;
        set => this.RaiseAndSetIfChanged(ref _lastPolledText, value);
    }

    public ReactiveCommand<Unit, Unit> AddGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeAllCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogDirectoryCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAlertHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenStatisticsCommand { get; }

    public MainWindowViewModel(
        AgMonitorService agMonitor,
        DagMonitorService dagMonitor,
        IConfigurationService configService,
        IHtmlExportService exportService,
        IAlertEngine alertEngine,
        IEventRecorder eventRecorder,
        IHistoryMaintenanceService historyMaintenance,
        IEventQueryService eventQuery,
        ISnapshotQueryService snapshotQuery,
        IEmailNotificationService emailService,
        ISyslogService syslogService,
        ISqlConnectionService connectionService,
        IAgDiscoveryService discoveryService,
        ICredentialStore credentialStore,
        ILoggerFactory loggerFactory)
    {
        _agMonitor = agMonitor;
        _dagMonitor = dagMonitor;
        _configService = configService;
        _exportService = exportService;
        _alertEngine = alertEngine;
        _eventRecorder = eventRecorder;
        _historyMaintenance = historyMaintenance;
        _eventQuery = eventQuery;
        _snapshotQuery = snapshotQuery;
        _emailService = emailService;
        _syslogService = syslogService;
        _connectionService = connectionService;
        _discoveryService = discoveryService;
        _credentialStore = credentialStore;
        _logger = loggerFactory?.CreateLogger<MainWindowViewModel>();

        var canRemove = this.WhenAnyValue(x => x.SelectedTab)
            .Select(tab => tab != null);

        AddGroupCommand = ReactiveCommand.CreateFromTask(OnAddGroupAsync);
        RemoveGroupCommand = ReactiveCommand.CreateFromTask(OnRemoveGroupAsync, canRemove);
        OpenSettingsCommand = ReactiveCommand.Create(OnOpenSettings);
        ExitCommand = ReactiveCommand.CreateFromTask(OnExitAsync);
        PauseAllCommand = ReactiveCommand.Create(OnPauseAll);
        ResumeAllCommand = ReactiveCommand.Create(OnResumeAll);
        AboutCommand = ReactiveCommand.CreateFromTask(OnAboutAsync);

        var canRefresh = this.WhenAnyValue(x => x.SelectedTab)
            .Select(tab => tab != null);
        RefreshCommand = ReactiveCommand.CreateFromTask(OnRefreshAsync, canRefresh);
        OpenLogDirectoryCommand = ReactiveCommand.Create(OnOpenLogDirectory);
        ToggleAlertHistoryCommand = ReactiveCommand.CreateFromTask(OnToggleAlertHistoryAsync);
        OpenStatisticsCommand = ReactiveCommand.CreateFromTask(OnOpenStatisticsAsync);

        StatusText = $"SQL Server AG Monitor v1.0 — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

        // Update "last polled" text every second
        var timerSub = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateLastPolledText());
        _subscriptions.Add(timerSub);

        // Prune old events: 10s initial delay (let app finish startup), then every 24 hours
        var pruneSub = Observable.Timer(TimeSpan.FromSeconds(10), TimeSpan.FromHours(24))
            .SelectMany(_ => Observable.FromAsync(PruneOldEventsAsync))
            .Subscribe();
        _subscriptions.Add(pruneSub);

        // Summarize snapshots: 5 min initial delay (accumulate raw data first), then hourly.
        // The 3-step aggregation: raw → hourly (after RawRetentionHours), hourly → daily
        // (after HourlyRetentionDays), then prune aged daily (after DailyRetentionDays).
        var summarizeSub = Observable.Timer(TimeSpan.FromMinutes(5), TimeSpan.FromHours(1))
            .SelectMany(_ => Observable.FromAsync(SummarizeSnapshotsAsync))
            .Subscribe();
        _subscriptions.Add(summarizeSub);

        SubscribeToSnapshots();
        _ = LoadAndStartMonitoredGroupsAsync();
    }

    private async Task LoadAndStartMonitoredGroupsAsync()
    {
        try
        {
            var config = _configService.Load();
            foreach (var group in config.MonitoredGroups)
            {
                var groupType = Enum.TryParse<AvailabilityGroupType>(group.GroupType, out var gt)
                    ? gt : AvailabilityGroupType.AvailabilityGroup;
                await StartMonitoringGroupAsync(group.Name, groupType);
            }

            if (config.MonitoredGroups.Count > 0)
                StatusText = $"Loaded {config.MonitoredGroups.Count} monitored group(s)";

            // Start scheduled HTML export if configured
            StartScheduledExport();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error loading monitored groups at startup.");
        }
    }

    private void StartScheduledExport()
    {
        try
        {
            _exportService.StartScheduledExportAsync(() =>
                _previousSnapshots.Values.ToList().AsReadOnly());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start scheduled HTML export.");
        }
    }

    private void SubscribeToSnapshots()
    {
        if (_agMonitor is null || _dagMonitor is null)
            return;

        var agSub = _agMonitor.Snapshots
            .Subscribe(snapshot => Dispatcher.UIThread.Post(() => OnSnapshotReceived(snapshot)));
        _subscriptions.Add(agSub);

        var dagSub = _dagMonitor.Snapshots
            .Subscribe(snapshot => Dispatcher.UIThread.Post(() => OnSnapshotReceived(snapshot)));
        _subscriptions.Add(dagSub);

        // Wire alert engine to process alerts
        var alertSub = _alertEngine.Alerts
            .Subscribe(alert =>
            {
                Dispatcher.UIThread.Post(() =>
                    StatusText = $"[{alert.Severity}] {alert.AlertType}: {alert.Message}");

                // Record in history
                _ = _eventRecorder.RecordEventAsync(alert);

                // Refresh alert history panel if visible
                if (_isAlertHistoryVisible)
                        _ = Dispatcher.UIThread.InvokeAsync(() => AlertHistory.LoadEventsAsync());

                    // Send notifications based on config
                    var config = _configService.Load();
                    if (config.Email.Enabled)
                        _ = _emailService.SendAlertEmailAsync(alert);
                    if (config.Syslog.Enabled)
                        _ = _syslogService.SendEventAsync(alert);
                });
            _subscriptions.Add(alertSub);
    }

    private void OnSnapshotReceived(MonitoredGroupSnapshot snapshot)
    {
        var existing = FindTab(snapshot.Name);
        if (existing is null)
        {
            existing = new MonitorTabViewModel { TabTitle = snapshot.Name, GroupType = snapshot.GroupType };
            MonitorTabs.Add(existing);
            SelectedTab ??= existing;
        }

        existing.ApplySnapshot(snapshot);
        ConnectionSummary = $"{MonitorTabs.Count} group(s) monitored";

        // Feed to alert engine
        _previousSnapshots.TryGetValue(snapshot.Name, out var previous);
        _alertEngine.EvaluateSnapshot(snapshot, previous);
        _previousSnapshots[snapshot.Name] = snapshot;

        // Record snapshot statistics to DuckDB
        _ = _eventRecorder.RecordSnapshotAsync(snapshot);
    }

    private MonitorTabViewModel? FindTab(string name)
    {
        foreach (var tab in MonitorTabs)
        {
            if (string.Equals(tab.TabTitle, name, StringComparison.OrdinalIgnoreCase))
                return tab;
        }
        return null;
    }

    private async Task OnAddGroupAsync()
    {
        var window = GetMainWindow();
        if (window == null) return;

        var vm = new AddGroupViewModel(_connectionService, _discoveryService, _credentialStore);
        try
        {
            var addWindow = new AddGroupWindow { DataContext = vm };
            var result = await addWindow.ShowDialog<object?>(window);

            if (result is true && vm.SelectedGroups is { Count: > 0 } groups)
            {
                var config = _configService.Load();

            foreach (var group in groups)
            {
                // Skip if already monitored
                if (config.MonitoredGroups.Any(g =>
                    string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                List<ConnectionConfig> connections;

                if (group.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                {
                    // DAGs: one connection per member from the wizard's Step 3
                    connections = vm.DagMemberConnections
                        .Where(m => string.Equals(m.DagName, group.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(m => new ConnectionConfig
                        {
                            Server = m.Server,
                            AuthType = m.AuthType,
                            Username = m.IsSqlAuth ? m.Username : null,
                            CredentialKey = m.CredentialKey
                        })
                        .ToList();
                }
                else
                {
                    // Regular AGs: single connection from Step 0
                    connections = new List<ConnectionConfig>
                    {
                        new ConnectionConfig
                        {
                            Server = vm.Server,
                            AuthType = vm.IsSqlAuth ? "sql" : "windows",
                            Username = vm.IsSqlAuth ? vm.Username : null,
                            CredentialKey = vm.IsSqlAuth ? $"agmon:{vm.Server}:{vm.Username}" : null
                        }
                    };
                }

                var groupConfig = new MonitoredGroupConfig
                {
                    Name = group.Name,
                    GroupType = group.GroupType.ToString(),
                    PollingIntervalSeconds = vm.PollingIntervalSeconds,
                    Connections = connections
                };

                config.MonitoredGroups.Add(groupConfig);
            }

            _configService.Save(config);

            foreach (var group in groups)
            {
                await StartMonitoringGroupAsync(group.Name, group.GroupType);
            }

            StatusText = groups.Count == 1
                ? $"Now monitoring {groups[0].Name}"
                : $"Now monitoring {groups.Count} group(s)";
            }
        }
        finally
        {
            vm.Dispose();
        }
    }

    private async Task StartMonitoringGroupAsync(string groupName, AvailabilityGroupType groupType)
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
            _logger?.LogError(ex, "Failed to start monitoring {Group}.", groupName);
            StatusText = $"Error starting monitoring for {groupName}: {ex.Message}";
        }
    }

    private async Task OnRemoveGroupAsync()
    {
        var tab = SelectedTab;
        if (tab is null) return;

        var window = GetMainWindow();
        if (window is null) return;

        // Confirmation dialog
        var confirmWindow = new Window
        {
            Title = "Confirm Removal",
            Width = 400,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var confirmed = false;
        var panel = new Avalonia.Controls.StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = $"Stop monitoring \"{tab.TabTitle}\" and remove it?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 14
                }
            }
        };

        var buttonPanel = new Avalonia.Controls.StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
        };

        var removeBtn = new Button { Content = "Remove", MinWidth = 80, Classes = { "accent" } };
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };

        removeBtn.Click += (_, _) => { confirmed = true; confirmWindow.Close(); };
        cancelBtn.Click += (_, _) => { confirmWindow.Close(); };

        buttonPanel.Children.Add(removeBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);
        confirmWindow.Content = panel;

        await confirmWindow.ShowDialog(window);

        if (!confirmed) return;

        var groupName = tab.TabTitle;
        var groupType = tab.GroupType;

        // Stop monitoring
        try
        {
            if (groupType == AvailabilityGroupType.DistributedAvailabilityGroup)
                await _dagMonitor.StopMonitoringAsync(groupName);
            else
                await _agMonitor.StopMonitoringAsync(groupName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error stopping monitoring for {Group}.", groupName);
        }

        // Remove from config
        var config = _configService.Load();
        config.MonitoredGroups.RemoveAll(g =>
            string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        _configService.Save(config);

        // Remove tab
        MonitorTabs.Remove(tab);
        _previousSnapshots.Remove(groupName, out _);
        SelectedTab = MonitorTabs.FirstOrDefault();
        ConnectionSummary = MonitorTabs.Count > 0
            ? $"{MonitorTabs.Count} group(s) monitored"
            : "No groups monitored";
        StatusText = $"Removed {groupName}";
    }

    private void OnOpenSettings()
    {
        var window = GetMainWindow();
        if (window == null) return;

        var vm = new SettingsViewModel(_configService, _emailService);
        vm.LoadFrom(_configService.Load());

        var settingsWindow = new SettingsWindow { DataContext = vm };
        settingsWindow.ShowDialog(window);
    }

    private async Task OnExitAsync()
    {
        _subscriptions.Dispose();

        // Gracefully dispose services that hold resources
        try
        {
            await _exportService.StopScheduledExportAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping export service on shutdown.");
        }

        try { await _agMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing AG monitor on shutdown."); }

        try { await _dagMonitor.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Error disposing DAG monitor on shutdown."); }

        try
        {
            await _historyMaintenance.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing event history service on shutdown.");
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is Views.MainWindow mainWindow)
            {
                mainWindow.ExitApplication();
            }
            desktop.Shutdown();
        }
    }

    private static void OnOpenLogDirectory()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Program.LogDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Program.WriteLog("ERROR", $"Failed to open log directory: {ex.Message}");
        }
    }

    private void OnPauseAll()
    {
        foreach (var tab in MonitorTabs)
            tab.IsPaused = true;
        IsAllPaused = true;
        this.RaisePropertyChanged(nameof(IsNotAllPaused));
        StatusText = "All monitoring paused";
    }

    private void OnResumeAll()
    {
        foreach (var tab in MonitorTabs)
            tab.IsPaused = false;
        IsAllPaused = false;
        this.RaisePropertyChanged(nameof(IsNotAllPaused));
        StatusText = "All monitoring resumed";
    }

    private async Task OnAboutAsync()
    {
        var window = GetMainWindow();
        if (window == null) return;

        var aboutWindow = new Window
        {
            Title = "About",
            Width = 360,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Avalonia.Controls.StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "SQL Server AG Monitor", FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center },
                    new TextBlock { Text = "Version 1.0.0", FontSize = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Opacity = 0.7 },
                    new TextBlock { Text = "by Hannah Vernon", FontSize = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Opacity = 0.7 },
                    new Separator { Margin = new Avalonia.Thickness(0, 4) },
                    new TextBlock { Text = "Avalonia UI • ReactiveUI • .NET 9", FontSize = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Opacity = 0.5 },
                }
            }
        };

        await aboutWindow.ShowDialog(window);
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    private async Task OnRefreshAsync()
    {
        var tab = SelectedTab;
        if (tab == null) return;

        StatusText = $"Refreshing {tab.TabTitle}...";

        try
        {
            MonitoredGroupSnapshot snapshot;
            if (tab.GroupType == AvailabilityGroupType.DistributedAvailabilityGroup)
            {
                snapshot = await _dagMonitor.PollOnceAsync(tab.TabTitle);
            }
            else
            {
                snapshot = await _agMonitor.PollOnceAsync(tab.TabTitle);
            }

            Dispatcher.UIThread.Post(() => OnSnapshotReceived(snapshot));
            StatusText = $"Refreshed {tab.TabTitle}";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    private void UpdateLastPolledText()
    {
        var tab = SelectedTab;
        if (tab?.LastPolledAt is { } polledAt)
        {
            var elapsed = DateTimeOffset.Now - polledAt;
            var timeText = elapsed.TotalSeconds < 2
                ? "just now"
                : $"{(int)elapsed.TotalSeconds}s ago";

            LastPolledText = (tab.IsPaused || IsAllPaused)
                ? $"Paused · last update {timeText}"
                : $"Updated {timeText}";
        }
        else
        {
            LastPolledText = (tab?.IsPaused == true || IsAllPaused)
                ? "Paused"
                : string.Empty;
        }
    }

    private async Task OnToggleAlertHistoryAsync()
    {
        IsAlertHistoryVisible = !IsAlertHistoryVisible;
        if (IsAlertHistoryVisible)
        {
            await AlertHistory.LoadEventsAsync();
        }
    }

    private Task OnOpenStatisticsAsync()
    {
        var window = GetMainWindow();
        if (window == null) return Task.CompletedTask;

        var vm = new StatisticsViewModel(_snapshotQuery, _configService);
        var statsWindow = new Views.StatisticsWindow { DataContext = vm };
        statsWindow.Show(window);
        return Task.CompletedTask;
    }

    private async Task PruneOldEventsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configService.Load();
            if (!config.History.AutoPruneEnabled) return;

            await _historyMaintenance.PruneEventsAsync(config.History.MaxRetentionDays, config.History.MaxRecords, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to prune old events.");
        }
    }

    private async Task SummarizeSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = _configService.Load();
            var retention = config.History.SnapshotRetention;
            await _historyMaintenance.SummarizeSnapshotsAsync(
                retention.RawRetentionHours,
                retention.HourlyRetentionDays,
                retention.DailyRetentionDays,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to summarize snapshots.");
        }
    }
}
