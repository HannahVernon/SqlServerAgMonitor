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
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
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
        var historyService = Services.GetRequiredService<IEventHistoryService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await historyService.InitializeAsync();
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
            var loggerFactory = Services.GetRequiredService<ILoggerFactory>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(agMonitor, dagMonitor, loggerFactory),
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