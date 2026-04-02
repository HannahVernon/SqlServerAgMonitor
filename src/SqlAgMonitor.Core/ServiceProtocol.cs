namespace SqlAgMonitor.Core;

/// <summary>
/// Shared protocol version between the service and desktop client.
/// Increment <see cref="Current"/> whenever a breaking API change is made
/// (new required endpoints, changed request/response shapes, etc.).
/// </summary>
public static class ServiceProtocol
{
    /// <summary>
    /// The current protocol version. Both sides must agree on this value.
    /// </summary>
    public const int Current = 1;
}
