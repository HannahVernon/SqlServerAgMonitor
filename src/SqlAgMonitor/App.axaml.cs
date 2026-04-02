using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
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

            var eventQuery = Services.GetRequiredService<IEventQueryService>();
            var snapshotQuery = Services.GetRequiredService<ISnapshotQueryService>();
            var emailService = Services.GetRequiredService<IEmailNotificationService>();
            var connectionService = Services.GetRequiredService<ISqlConnectionService>();
            var discoveryService = Services.GetRequiredService<IAgDiscoveryService>();
            var credentialStore = Services.GetRequiredService<ICredentialStore>();
            var loggerFactory = Services.GetRequiredService<ILoggerFactory>();
            var maintenanceScheduler = Services.GetRequiredService<MaintenanceScheduler>();

            IMonitoringCoordinator coordinator;

            if (config.Service.Enabled)
            {
                // Service-client mode: connect to remote service via SignalR
                var serviceClient = new ServiceMonitoringClient(
                    configService, loggerFactory.CreateLogger<ServiceMonitoringClient>());
                coordinator = serviceClient;

                // Override query services with hub proxies
                eventQuery = new HubEventQueryService(serviceClient);
                snapshotQuery = new HubSnapshotQueryService(serviceClient);

                // Auto-login in the background after the window is shown
                _ = AutoLoginAsync(config, serviceClient, credentialStore,
                    loggerFactory.CreateLogger<App>());
            }
            else
            {
                // Standalone mode: direct SQL Server monitoring
                var agMonitor = Services.GetRequiredService<AgMonitorService>();
                var dagMonitor = Services.GetRequiredService<DagMonitorService>();
                var exportService = Services.GetRequiredService<IHtmlExportService>();
                var alertEngine = Services.GetRequiredService<IAlertEngine>();
                var alertDispatcher = Services.GetRequiredService<AlertDispatcher>();
                var eventRecorder = Services.GetRequiredService<IEventRecorder>();

                coordinator = new MonitoringCoordinator(
                    agMonitor, dagMonitor, alertEngine, alertDispatcher,
                    eventRecorder, configService, exportService,
                    loggerFactory.CreateLogger<MonitoringCoordinator>());
            }

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    coordinator, maintenanceScheduler, configService,
                    historyMaintenance, eventQuery, snapshotQuery,
                    emailService, connectionService, discoveryService,
                    credentialStore, loggerFactory),
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

    private static async Task AutoLoginAsync(
        AppConfiguration config,
        ServiceMonitoringClient serviceClient,
        ICredentialStore credentialStore,
        ILogger logger)
    {
        var svc = config.Service;
        var username = svc.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            logger.LogWarning("Service mode enabled but no username configured — skipping auto-login");
            return;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(svc.CredentialKey))
        {
            password = await credentialStore.GetPasswordAsync(svc.CredentialKey);
        }

        if (string.IsNullOrEmpty(password))
        {
            logger.LogWarning("Service mode enabled but no stored password — open Settings to configure");
            return;
        }

        try
        {
            var thumbprint = svc.TrustedCertThumbprint;

            // If TLS with no pinned cert, probe to see if the system trusts it
            if (svc.UseTls && string.IsNullOrEmpty(thumbprint))
            {
                thumbprint = await ProbeCertificateAsync(svc, logger);
            }

            // Check protocol version before attempting login
            var versionError = await ServiceMonitoringClient.CheckVersionAsync(svc, thumbprint);
            if (versionError != null)
            {
                logger.LogError("Service version check failed: {Error}", versionError);
                return;
            }

            logger.LogInformation("Auto-login to service as {User}@{Host}:{Port}",
                username, svc.Host, svc.Port);

            var token = await ServiceMonitoringClient.LoginAsync(svc, username, password, thumbprint);
            if (token == null)
            {
                logger.LogError("Auto-login failed — service returned unauthorized");
                return;
            }

            await serviceClient.ConnectAsync(token, thumbprint);
            logger.LogInformation("Auto-login succeeded — connected to service");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-login failed");
        }
    }

    /// <summary>
    /// Probes the service TLS endpoint. If the cert is untrusted, shows a trust dialog
    /// on the UI thread and returns the pinned thumbprint if the user accepts.
    /// </summary>
    private static async Task<string?> ProbeCertificateAsync(ServiceSettings svc, ILogger logger)
    {
        X509Certificate2? capturedCert = null;
        var baseUrl = $"https://{svc.Host}:{Math.Clamp(svc.Port, 1, 65535)}";

        using var probeHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                    return true;
                if (cert != null)
                    capturedCert = new X509Certificate2(cert);
                return false;
            }
        };

        using var probeClient = new HttpClient(probeHandler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };

        try
        {
            await probeClient.GetAsync("/api/auth/login");
            return null; // cert is trusted by system
        }
        catch (HttpRequestException) when (capturedCert != null)
        {
            logger.LogInformation("Service certificate is not trusted by system, prompting user");

            string? thumbprint = null;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var mainWindow = (Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return;

                var dialog = new CertificateTrustDialog(capturedCert);
                await dialog.ShowDialog(mainWindow);
                if (dialog.Accepted)
                {
                    thumbprint = capturedCert.Thumbprint;

                    // Persist the pinned thumbprint
                    var configService = Services.GetRequiredService<IConfigurationService>();
                    var cfg = configService.Load();
                    cfg.Service.TrustedCertThumbprint = thumbprint;
                    configService.Save(cfg);
                }
            });

            return thumbprint;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TLS probe failed");
            return null;
        }
    }
}