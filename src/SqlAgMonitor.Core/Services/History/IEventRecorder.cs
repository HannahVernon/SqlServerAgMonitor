using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Records alert events and monitoring snapshots to persistent storage.
/// </summary>
public interface IEventRecorder
{
    Task RecordEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);
    Task RecordSnapshotAsync(MonitoredGroupSnapshot snapshot, CancellationToken cancellationToken = default);
}
