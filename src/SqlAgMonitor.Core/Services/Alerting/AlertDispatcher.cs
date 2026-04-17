using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.Core.Services.Alerting;

/// <summary>
/// Dispatches alert events to configured notification channels (email, syslog)
/// and records them to persistent history storage.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly IEventRecorder _eventRecorder;
    private readonly IEmailNotificationService _emailService;
    private readonly ISyslogService _syslogService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<AlertDispatcher> _logger;

    public AlertDispatcher(
        IEventRecorder eventRecorder,
        IEmailNotificationService emailService,
        ISyslogService syslogService,
        IConfigurationService configService,
        ILogger<AlertDispatcher> logger)
    {
        _eventRecorder = eventRecorder;
        _emailService = emailService;
        _syslogService = syslogService;
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Records the alert and forwards it to all enabled notification channels.
    /// Fire-and-forget — errors are logged, not thrown.
    /// </summary>
    public void Dispatch(AlertEvent alert)
    {
        _ = SafeExecuteAsync("event recording", () => _eventRecorder.RecordEventAsync(alert));

        try
        {
            var config = _configService.Load();
            if (config.Email.Enabled)
                _ = SafeExecuteAsync("email notification", () => _emailService.SendAlertEmailAsync(alert));
            if (config.Syslog.Enabled)
                _ = SafeExecuteAsync("syslog notification", () => _syslogService.SendEventAsync(alert));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching alert notifications.");
        }
    }

    private async Task SafeExecuteAsync(string channel, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch alert via {Channel}.", channel);
        }
    }
}
