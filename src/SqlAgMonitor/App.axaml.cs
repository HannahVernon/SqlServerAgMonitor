using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
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
        Services = services.BuildServiceProvider();

        // Restore saved theme
        var configService = Services.GetRequiredService<IConfigurationService>();
        var config = configService.Load();
        new SqlAgMonitor.Services.ThemeService().SetTheme(config.Theme);

        // Initialize event history database
        var historyService = Services.GetRequiredService<IEventHistoryService>();
        _ = historyService.InitializeAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var agMonitor = Services.GetRequiredService<AgMonitorService>();
            var dagMonitor = Services.GetRequiredService<DagMonitorService>();
            var loggerFactory = Services.GetRequiredService<ILoggerFactory>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(agMonitor, dagMonitor, loggerFactory),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}