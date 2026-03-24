namespace SqlAgMonitor.Core.Services.Connection;

public interface IConnectionMonitor
{
    IObservable<ConnectionStateChange> ConnectionStateChanges { get; }
    bool IsConnected(string server);
}

public record ConnectionStateChange(string Server, bool IsConnected, string? ErrorMessage, DateTimeOffset Timestamp);
