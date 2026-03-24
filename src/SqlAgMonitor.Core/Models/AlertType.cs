namespace SqlAgMonitor.Core.Models;

public enum AlertType
{
    ReplicaDisconnected,
    SyncFellBehind,
    HealthDegraded,
    FailoverOccurred,
    ConnectionLost,
    ConnectionRestored,
    SuspendDetected,
    ResumeDetected,
    SyncModeChanged,
    Unknown
}
