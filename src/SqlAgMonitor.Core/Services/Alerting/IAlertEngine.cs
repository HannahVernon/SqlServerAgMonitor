using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Alerting;

public interface IAlertEngine
{
    IObservable<AlertEvent> Alerts { get; }
    void EvaluateSnapshot(MonitoredGroupSnapshot snapshot, MonitoredGroupSnapshot? previousSnapshot);
    void MuteAlert(AlertType alertType, string groupName, TimeSpan? duration);
    void UnmuteAlert(AlertType alertType, string groupName);
    IReadOnlyList<MutedAlertInfo> GetMutedAlerts();
}

public record MutedAlertInfo(AlertType AlertType, string GroupName, DateTimeOffset? MutedUntil, bool IsPermanent);
