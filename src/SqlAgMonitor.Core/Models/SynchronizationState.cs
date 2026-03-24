namespace SqlAgMonitor.Core.Models;

public enum SynchronizationState
{
    Synchronized,
    Synchronizing,
    NotSynchronizing,
    Reverting,
    Initializing,
    Unknown
}
