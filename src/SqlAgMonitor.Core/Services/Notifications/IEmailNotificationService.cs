using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

public interface IEmailNotificationService
{
    Task SendAlertEmailAsync(AlertEvent alert, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
