using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

public interface ISyslogService
{
    Task SendEventAsync(AlertEvent alert, CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
}
