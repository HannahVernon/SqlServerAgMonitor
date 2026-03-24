namespace SqlAgMonitor.Core.Models;

public enum OperationalState
{
    Online,
    Offline,
    Pending,
    PendingFailover,
    FailedNoQuorum,
    Unknown
}
