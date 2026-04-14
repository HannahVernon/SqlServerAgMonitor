using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

public interface IEmailNotificationService
{
    Task SendAlertEmailAsync(AlertEvent alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the SMTP connection by sending a test email.
    /// Returns null on success, or an error message on failure.
    /// </summary>
    Task<string?> TestConnectionAsync(CancellationToken cancellationToken = default);
}
