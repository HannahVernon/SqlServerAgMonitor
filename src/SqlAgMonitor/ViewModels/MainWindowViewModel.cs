using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Services;
using SqlAgMonitor.Views;

namespace SqlAgMonitor.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private readonly AgMonitorService _agMonitor;
    private readonly DagMonitorService _dagMonitor;
    private readonly ILogger? _logger;
    private readonly CompositeDisposable _subscriptions = new();

    private MonitorTabViewModel? _selectedTab;
    private string _statusText = "Ready";
    private string _connectionSummary = "No groups monitored";
    private bool _isAllPaused;

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
        _logger = loggerFactory?.CreateLogger<MainWindowViewModel>();

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
        var window = GetMainWindow();
        if (window == null) return;

        var addWindow = new AddGroupWindow
        {
            DataContext = new AddGroupViewModel()
        };
        addWindow.ShowDialog(window);
    }

    private void OnOpenSettings()
    {
        var window = GetMainWindow();
        if (window == null) return;

        var configService = App.Services.GetRequiredService<IConfigurationService>();
        var vm = new SettingsViewModel();
        vm.LoadFrom(configService.Load());

        var settingsWindow = new SettingsWindow { DataContext = vm };
        settingsWindow.ShowDialog(window);
    }

    private void OnExit()
    {
        _subscriptions.Dispose();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mw)
                mw.ForceClose();
        }
    }

    private void OnSetTheme(string theme)
    {
        var themeService = new ThemeService();
        themeService.SetTheme(theme);

        var configService = App.Services.GetRequiredService<IConfigurationService>();
        var config = configService.Load();
        config.Theme = theme;
        configService.Save(config);

        StatusText = $"Theme changed to {theme}";
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

    private async void OnAbout()
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
}
