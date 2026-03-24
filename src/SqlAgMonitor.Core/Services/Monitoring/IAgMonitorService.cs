using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Monitoring;

public interface IAgMonitorService : IAsyncDisposable
{
    IObservable<MonitoredGroupSnapshot> Snapshots { get; }
    Task StartMonitoringAsync(string groupName, CancellationToken cancellationToken = default);
    Task StopMonitoringAsync(string groupName, CancellationToken cancellationToken = default);
    Task<MonitoredGroupSnapshot> PollOnceAsync(string groupName, CancellationToken cancellationToken = default);
}
