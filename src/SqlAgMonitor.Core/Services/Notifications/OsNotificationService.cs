using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

/// <summary>
/// Stub implementation that logs notifications. The actual OS notification
/// will be wired up in the Avalonia app layer using platform-specific APIs.
/// </summary>
public class OsNotificationService : IOsNotificationService
{
    private readonly ILogger<OsNotificationService> _logger;

    public OsNotificationService(ILogger<OsNotificationService> logger)
    {
        _logger = logger;
    }

    public void ShowNotification(string title, string message, AlertSeverity severity)
    {
        _logger.LogInformation(
            "OS Notification [{Severity}] {Title}: {Message}",
            severity, title, message);
    }
}
