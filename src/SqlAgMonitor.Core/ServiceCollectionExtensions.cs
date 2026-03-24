using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Export;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Monitoring;
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSqlAgMonitorCore(this IServiceCollection services)
    {
        // Configuration
        services.AddSingleton<IConfigurationService, JsonConfigurationService>();

        // Credentials
        services.AddSingleton<IPasswordStrengthValidator, PasswordStrengthValidator>();
        services.AddSingleton<ICredentialStore>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var validator = sp.GetRequiredService<IPasswordStrengthValidator>();
            return PlatformCredentialStoreFactory.Create(loggerFactory, validator);
        });

        // Connection
        services.AddSingleton<SqlConnectionService>();
        services.AddSingleton<ISqlConnectionService>(sp => sp.GetRequiredService<SqlConnectionService>());
        services.AddSingleton<IConnectionMonitor>(sp => sp.GetRequiredService<SqlConnectionService>());

        // Monitoring
        services.AddSingleton<IAgDiscoveryService, AgDiscoveryService>();
        services.AddSingleton<AgMonitorService>();
        services.AddSingleton<DagMonitorService>();
        services.AddSingleton<IAgControlService, AgControlService>();

        // Alerting
        services.AddSingleton<IAlertEngine, AlertEngine>();

        // Notifications
        services.AddSingleton<IEmailNotificationService, SmtpEmailNotificationService>();
        services.AddSingleton<ISyslogService, SyslogService>();
        services.AddSingleton<IOsNotificationService, OsNotificationService>();

        // History
        services.AddSingleton<DuckDbEventHistoryService>();
        services.AddSingleton<IEventHistoryService>(sp => sp.GetRequiredService<DuckDbEventHistoryService>());
        services.AddSingleton<FileErrorLogger>();

        // Export
        services.AddSingleton<IHtmlExportService, HtmlExportService>();

        return services;
    }
}
