using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Monitoring;

/// <summary>
/// Shared parsing helpers for converting SQL Server DMV string descriptors
/// into strongly-typed enum values.
/// </summary>
public static class SqlParsingHelpers
{
    public static ReplicaRole ParseRole(string? desc) => desc?.ToUpperInvariant() switch
    {
        "PRIMARY" => ReplicaRole.Primary,
        "SECONDARY" => ReplicaRole.Secondary,
        "RESOLVING" => ReplicaRole.Resolving,
        _ => ReplicaRole.Unknown
    };

    public static SynchronizationState ParseSyncState(string? desc) => desc?.ToUpperInvariant() switch
    {
        "SYNCHRONIZED" => SynchronizationState.Synchronized,
        "SYNCHRONIZING" => SynchronizationState.Synchronizing,
        "NOT SYNCHRONIZING" or "NOT_SYNCHRONIZING" => SynchronizationState.NotSynchronizing,
        "REVERTING" => SynchronizationState.Reverting,
        "INITIALIZING" => SynchronizationState.Initializing,
        _ => SynchronizationState.Unknown
    };

    public static SynchronizationHealth ParseSyncHealth(string? desc) => desc?.ToUpperInvariant() switch
    {
        "HEALTHY" => SynchronizationHealth.Healthy,
        "PARTIALLY_HEALTHY" or "PARTIALLY HEALTHY" => SynchronizationHealth.PartiallyHealthy,
        "NOT_HEALTHY" or "NOT HEALTHY" => SynchronizationHealth.NotHealthy,
        _ => SynchronizationHealth.Unknown
    };

    public static AvailabilityMode ParseAvailabilityMode(string? desc) => desc?.ToUpperInvariant() switch
    {
        "SYNCHRONOUS_COMMIT" or "SYNCHRONOUS COMMIT" => AvailabilityMode.SynchronousCommit,
        "ASYNCHRONOUS_COMMIT" or "ASYNCHRONOUS COMMIT" => AvailabilityMode.AsynchronousCommit,
        "CONFIGURATION_ONLY" or "CONFIGURATION ONLY" => AvailabilityMode.ConfigurationOnly,
        _ => AvailabilityMode.Unknown
    };

    public static ConnectedState ParseConnectedState(string? desc) => desc?.ToUpperInvariant() switch
    {
        "CONNECTED" => ConnectedState.Connected,
        "DISCONNECTED" => ConnectedState.Disconnected,
        _ => ConnectedState.Unknown
    };

    public static OperationalState ParseOperationalState(string? desc) => desc?.ToUpperInvariant() switch
    {
        "ONLINE" => OperationalState.Online,
        "OFFLINE" => OperationalState.Offline,
        "PENDING" => OperationalState.Pending,
        "PENDING_FAILOVER" or "PENDING FAILOVER" => OperationalState.PendingFailover,
        "FAILED_NO_QUORUM" or "FAILED NO QUORUM" => OperationalState.FailedNoQuorum,
        _ => OperationalState.Unknown
    };

    public static RecoveryHealth ParseRecoveryHealth(string? desc) => desc?.ToUpperInvariant() switch
    {
        "ONLINE" => RecoveryHealth.Online,
        "IN_PROGRESS" or "IN PROGRESS" => RecoveryHealth.InProgress,
        _ => RecoveryHealth.Unknown
    };

    public static SynchronizationHealth ComputeOverallHealth(IReadOnlyList<ReplicaInfo> replicas)
    {
        if (replicas.Count == 0) return SynchronizationHealth.Unknown;
        if (replicas.All(r => r.SynchronizationHealth == SynchronizationHealth.Healthy))
            return SynchronizationHealth.Healthy;
        if (replicas.Any(r => r.SynchronizationHealth == SynchronizationHealth.NotHealthy))
            return SynchronizationHealth.NotHealthy;
        return SynchronizationHealth.PartiallyHealthy;
    }
}
