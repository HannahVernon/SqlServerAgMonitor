using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core;
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
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        services.AddSqlAgMonitorCore();
        Services = services.BuildServiceProvider();

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