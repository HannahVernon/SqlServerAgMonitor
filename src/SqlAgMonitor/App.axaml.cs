using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Export;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.Services;
using SqlAgMonitor.ViewModels;
using SqlAgMonitor.Views;

namespace SqlAgMonitor;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider(Program.LogFilePath));
        });
        services.AddSqlAgMonitorCore();

        // UI-layer services
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ILayoutStateService, LayoutStateService>();

        Services = services.BuildServiceProvider();

        // Restore saved theme
        var configService = Services.GetRequiredService<IConfigurationService>();
        var config = configService.Load();
        Services.GetRequiredService<IThemeService>().SetTheme(config.Theme);

        // Initialize event history database (errors are logged; DuckDB degrades gracefully if unavailable)
        var historyMaintenance = Services.GetRequiredService<IHistoryMaintenanceService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await historyMaintenance.InitializeAsync();
            }
            catch (Exception ex)
            {
                var logger = Services.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<App>();
                logger.LogError(ex, "DuckDB initialization failed. Event history will retry on first use.");
            }
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the app alive when the window is closed (minimized to tray)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var agMonitor = Services.GetRequiredService<AgMonitorService>();
            var dagMonitor = Services.GetRequiredService<DagMonitorService>();
            var exportService = Services.GetRequiredService<IHtmlExportService>();
            var alertEngine = Services.GetRequiredService<IAlertEngine>();
            var eventRecorder = Services.GetRequiredService<IEventRecorder>();
            var eventQuery = Services.GetRequiredService<IEventQueryService>();
            var snapshotQuery = Services.GetRequiredService<ISnapshotQueryService>();
            var emailService = Services.GetRequiredService<IEmailNotificationService>();
            var syslogService = Services.GetRequiredService<ISyslogService>();
            var connectionService = Services.GetRequiredService<ISqlConnectionService>();
            var discoveryService = Services.GetRequiredService<IAgDiscoveryService>();
            var credentialStore = Services.GetRequiredService<ICredentialStore>();
            var loggerFactory = Services.GetRequiredService<ILoggerFactory>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    agMonitor, dagMonitor, configService, exportService,
                    alertEngine, eventRecorder, historyMaintenance, eventQuery,
                    snapshotQuery, emailService, syslogService, connectionService,
                    discoveryService, credentialStore, loggerFactory),
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SqlAgMonitor/Assets/app-icon.png")))
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayShow_OnClick(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void TrayExit_OnClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.DataContext is MainWindowViewModel vm)
        {
            vm.ExitCommand.Execute().Subscribe();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window != null)
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
        }
    }
}