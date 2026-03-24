using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Monitoring;

public interface IAgControlService
{
    Task<bool> FailoverAsync(string agName, string targetReplica, CancellationToken cancellationToken = default);
    Task<bool> ForceFailoverAsync(string agName, string targetReplica, CancellationToken cancellationToken = default);
    Task<bool> SetAvailabilityModeAsync(string agName, string replicaName, AvailabilityMode mode, CancellationToken cancellationToken = default);
    Task<bool> SuspendDatabaseAsync(string agName, string databaseName, CancellationToken cancellationToken = default);
    Task<bool> ResumeDatabaseAsync(string agName, string databaseName, CancellationToken cancellationToken = default);
}
