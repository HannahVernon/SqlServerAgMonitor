using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AgMonitorService _agMonitor;
    private readonly DagMonitorService _dagMonitor;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _subscriptions = new();

    private MonitorTabViewModel? _selectedTab;
    private string _statusText = "Ready";
    private string _connectionSummary = "No groups monitored";

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

    public ReactiveCommand<Unit, Unit> AddGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<string, Unit> SetThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeAllCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }

    public MainWindowViewModel()
        : this(null!, null!, null!)
    {
    }

    public MainWindowViewModel(AgMonitorService agMonitor, DagMonitorService dagMonitor, ILoggerFactory loggerFactory)
    {
        _agMonitor = agMonitor;
        _dagMonitor = dagMonitor;
        _logger = loggerFactory?.CreateLogger<MainWindowViewModel>()!;

        AddGroupCommand = ReactiveCommand.Create(OnAddGroup);
        OpenSettingsCommand = ReactiveCommand.Create(OnOpenSettings);
        ExitCommand = ReactiveCommand.Create(OnExit);
        SetThemeCommand = ReactiveCommand.Create<string>(OnSetTheme);
        PauseAllCommand = ReactiveCommand.Create(OnPauseAll);
        ResumeAllCommand = ReactiveCommand.Create(OnResumeAll);
        AboutCommand = ReactiveCommand.Create(OnAbout);

        StatusText = $"SQL Server AG Monitor v1.0 — {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";

        SubscribeToSnapshots();
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

    private void OnAddGroup()
    {
        // TODO: Open add AG/DAG wizard
    }

    private void OnOpenSettings()
    {
        // TODO: Open settings dialog
    }

    private void OnExit()
    {
        _subscriptions.Dispose();
        Environment.Exit(0);
    }

    private void OnSetTheme(string theme)
    {
        // TODO: Apply theme
    }

    private void OnPauseAll()
    {
        foreach (var tab in MonitorTabs)
            tab.IsPaused = true;
        StatusText = "All monitoring paused";
    }

    private void OnResumeAll()
    {
        foreach (var tab in MonitorTabs)
            tab.IsPaused = false;
        StatusText = "All monitoring resumed";
    }

    private void OnAbout()
    {
        // TODO: Show about dialog
    }
}
