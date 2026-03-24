namespace SqlAgMonitor.Core.Models;

public class AlertEvent
{
    public required long Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required AlertType AlertType { get; init; }
    public required string GroupName { get; init; }
    public string? ReplicaName { get; init; }
    public string? DatabaseName { get; init; }
    public required string Message { get; init; }
    public required AlertSeverity Severity { get; init; }
    public bool EmailSent { get; init; }
    public bool SyslogSent { get; init; }
}

public enum AlertSeverity
{
    Information,
    Warning,
    Critical
}
