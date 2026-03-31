using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.History;

/// <summary>
/// Composite interface retained for DI convenience — the concrete implementation
/// implements all four focused interfaces through this single type.
/// Consumers should depend on the specific focused interface they need.
/// </summary>
public interface IEventHistoryService
    : IEventRecorder, IEventQueryService, ISnapshotQueryService, IHistoryMaintenanceService
{
}
