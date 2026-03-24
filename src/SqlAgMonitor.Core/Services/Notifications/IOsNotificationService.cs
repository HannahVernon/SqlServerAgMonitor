using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

public interface IOsNotificationService
{
    void ShowNotification(string title, string message, AlertSeverity severity);
}
