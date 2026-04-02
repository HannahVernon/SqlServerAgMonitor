using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
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
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.Services;
using SqlAgMonitor.Views;

namespace SqlAgMonitor.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly IMonitoringCoordinator _coordinator;
    private readonly MaintenanceScheduler _maintenanceScheduler;
    private readonly IConfigurationService _configService;
    private readonly IHistoryMaintenanceService _historyMaintenance;
    private readonly IEventQueryService _eventQuery;
    private readonly ISnapshotQueryService _snapshotQuery;
    private readonly IEmailNotificationService _emailService;
    private readonly ISqlConnectionService _connectionService;
    private readonly IAgDiscoveryService _discoveryService;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly string _versionText;

    private object? _selectedTab;
    private string _statusText = "Ready";
    private string _connectionSummary = "No groups monitored";
    private bool _isAllPaused;
    private string _lastPolledText = string.Empty;
    private AlertHistoryViewModel? _alertHistoryVm;
    private bool _isServiceMode;
    private bool _isServiceConnected;
    private string _serviceConnectionText = string.Empty;

    public ObservableCollection<MonitorTabViewModel> MonitorTabs => _coordinator.MonitorTabs;
    public ObservableCollection<object> AllTabs { get; } = new();

    public object? SelectedTab
    {
        get => _selectedTab;
        set => this.RaiseAndSetIfChanged(ref _selectedTab, value);
    }

    private MonitorTabViewModel? SelectedMonitorTab => SelectedTab as MonitorTabViewModel;

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

    public AlertHistoryViewModel AlertHistory => _alertHistoryVm ??= new AlertHistoryViewModel(_eventQuery);

    public string LastPolledText
    {
        get => _lastPolledText;
        set => this.RaiseAndSetIfChanged(ref _lastPolledText, value);
    }

    public bool IsServiceMode
    {
        get => _isServiceMode;
        set => this.RaiseAndSetIfChanged(ref _isServiceMode, value);
    }

    public bool IsServiceConnected
    {
        get => _isServiceConnected;
        set => this.RaiseAndSetIfChanged(ref _isServiceConnected, value);
    }

    public string ServiceConnectionText
    {
        get => _serviceConnectionText;
        set => this.RaiseAndSetIfChanged(ref _serviceConnectionText, value);
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
        IMonitoringCoordinator coordinator,
        MaintenanceScheduler maintenanceScheduler,
        IConfigurationService configService,
        IHistoryMaintenanceService historyMaintenance,
        IEventQueryService eventQuery,
        ISnapshotQueryService snapshotQuery,
        IEmailNotificationService emailService,
        ISqlConnectionService connectionService,
        IAgDiscoveryService discoveryService,
        ICredentialStore credentialStore,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _maintenanceScheduler = maintenanceScheduler;
        _configService = configService;
        _historyMaintenance = historyMaintenance;
        _eventQuery = eventQuery;
        _snapshotQuery = snapshotQuery;
        _emailService = emailService;
        _connectionService = connectionService;
        _discoveryService = discoveryService;
        _credentialStore = credentialStore;
        _logger = loggerFactory?.CreateLogger<MainWindowViewModel>();

        var canRemove = this.WhenAnyValue(x => x.SelectedTab)
            .Select(tab => tab is MonitorTabViewModel);

        AddGroupCommand = ReactiveCommand.CreateFromTask(OnAddGroupAsync);
        RemoveGroupCommand = ReactiveCommand.CreateFromTask(OnRemoveGroupAsync, canRemove);
        OpenSettingsCommand = ReactiveCommand.Create(OnOpenSettings);
        ExitCommand = ReactiveCommand.CreateFromTask(OnExitAsync);
        PauseAllCommand = ReactiveCommand.Create(OnPauseAll);
        ResumeAllCommand = ReactiveCommand.Create(OnResumeAll);
        AboutCommand = ReactiveCommand.CreateFromTask(OnAboutAsync);

        var canRefresh = this.WhenAnyValue(x => x.SelectedTab)
            .Select(tab => tab is MonitorTabViewModel);
        RefreshCommand = ReactiveCommand.CreateFromTask(OnRefreshAsync, canRefresh);
        OpenLogDirectoryCommand = ReactiveCommand.Create(OnOpenLogDirectory);
        ToggleAlertHistoryCommand = ReactiveCommand.CreateFromTask(OnToggleAlertHistoryAsync);
        OpenStatisticsCommand = ReactiveCommand.CreateFromTask(OnOpenStatisticsAsync);

        var infoVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        _versionText = infoVersion != null ? $"v{infoVersion}" : "v0.0.0";
        StatusText = $"SQL Server AG Monitor {_versionText} — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

        // Initialize AllTabs with Alert History as the first tab
        AllTabs.Add(AlertHistory);
        foreach (var tab in MonitorTabs)
            AllTabs.Add(tab);
        SelectedTab = AllTabs.FirstOrDefault();

        MonitorTabs.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    var insertIndex = e.NewStartingIndex + 1; // +1 for Alert History at index 0
                    if (insertIndex <= AllTabs.Count)
                        AllTabs.Insert(insertIndex, item!);
                    else
                        AllTabs.Add(item!);
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (var item in e.OldItems)
                    AllTabs.Remove(item!);
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                // Rebuild: keep Alert History, re-add monitor tabs
                while (AllTabs.Count > 1)
                    AllTabs.RemoveAt(AllTabs.Count - 1);
                foreach (var tab in MonitorTabs)
                    AllTabs.Add(tab);
            }
        };

        // Update "last polled" text every second
        var timerSub = Observable.Interval(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateLastPolledText());
        _subscriptions.Add(timerSub);

        // Wire coordinator events to UI state
        _coordinator.SnapshotProcessed += snapshot =>
        {
            SelectedTab ??= AllTabs.FirstOrDefault();
            ConnectionSummary = $"{MonitorTabs.Count} group(s) monitored";
        };

        _coordinator.AlertRaised += alert =>
        {
            StatusText = $"[{alert.Severity}] {alert.AlertType}: {alert.Message}";
            _ = Dispatcher.UIThread.InvokeAsync(() => AlertHistory.LoadEventsAsync());
        };

        if (_coordinator is ServiceMonitoringClient serviceClient)
        {
            IsServiceMode = true;
            ServiceConnectionText = "Connecting…";
            serviceClient.ConnectionStateChanged += connected =>
            {
                IsServiceConnected = connected;
                ServiceConnectionText = connected ? "● Connected" : "○ Disconnected";
            };
            serviceClient.VersionMismatchDetected += error =>
            {
                ServiceConnectionText = "⚠ Version mismatch";
                StatusText = error;
            };
        }

        _coordinator.SubscribeToMonitors();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _coordinator.LoadAndStartAsync();
        var groupCount = MonitorTabs.Count;
        if (groupCount > 0)
        {
            StatusText = $"Loaded {groupCount} monitored group(s)";
            // Select the first monitor tab (index 1, after Alert History)
            if (AllTabs.Count > 1)
                SelectedTab = AllTabs[1];
        }
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
                await _coordinator.StartGroupAsync(group.Name, group.GroupType);
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

    private async Task OnRemoveGroupAsync()
    {
        var tab = SelectedMonitorTab;
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
            await _coordinator.StopGroupAsync(groupName, groupType);
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
        SelectedTab = MonitorTabs.Count > 0
            ? MonitorTabs.FirstOrDefault()
            : AllTabs.FirstOrDefault();
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
        _coordinator.Dispose();
        _maintenanceScheduler.Dispose();

        // Gracefully dispose services that hold resources
        try
        {
            await _coordinator.DisposeMonitorsAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error disposing monitors on shutdown.");
        }

        try
        {
            await _historyMaintenance.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Error disposing event history service on shutdown.");
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
                    new TextBlock { Text = $"Version {_versionText}", FontSize = 14, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Opacity = 0.7 },
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
        var tab = SelectedMonitorTab;
        if (tab == null) return;

        StatusText = $"Refreshing {tab.TabTitle}...";

        try
        {
            var snapshot = await _coordinator.PollOnceAsync(tab.TabTitle, tab.GroupType);
            StatusText = $"Refreshed {tab.TabTitle}";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh failed: {ex.Message}";
        }
    }

    private void UpdateLastPolledText()
    {
        var tab = SelectedMonitorTab;
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
        SelectedTab = AlertHistory;
        await AlertHistory.LoadEventsAsync();
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
}
