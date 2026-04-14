using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Alerting;

/// <summary>
/// Evaluates AG/DAG monitoring snapshots and publishes alert events when state transitions
/// are detected. Alerts flow through a three-stage gate before publication:
/// <list type="number">
///   <item><description>Per-alert-type enable/disable check (configuration).</description></item>
///   <item><description>Per-group mute check (timed or permanent, with auto-expiration).</description></item>
///   <item><description>Global cooldown — suppresses all alerts if the last published alert
///     was within <see cref="AlertSettings.MasterCooldownMinutes"/> (default 5 minutes).
///     Prevents alert storms during cascading failures.</description></item>
/// </list>
/// Alert types detected: ConnectionLost, ConnectionRestored, FailoverDetected,
/// RoleChanged, SyncModeChanged, SyncFellBehind, SuspendDetected, ResumeDetected.
/// </summary>
public sealed class AlertEngine : IAlertEngine, IDisposable
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<AlertEngine> _logger;
    private readonly Subject<AlertEvent> _alertSubject = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _mutedAlerts = new();
    private readonly object _cooldownLock = new();
    private DateTimeOffset _lastAlertTime = DateTimeOffset.MinValue;
    private long _nextId;

    public IObservable<AlertEvent> Alerts => _alertSubject;

    public AlertEngine(IConfigurationService configService, ILogger<AlertEngine> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public void EvaluateSnapshot(MonitoredGroupSnapshot snapshot, MonitoredGroupSnapshot? previousSnapshot)
    {
        var config = _configService.Load();
        var alertSettings = config.Alerts;
        var alerts = new List<AlertEvent>();

        DetectConnectionLostRestored(snapshot, previousSnapshot, alerts);
        DetectHealthDegraded(snapshot, previousSnapshot, alerts);

        if (snapshot.AgInfo != null)
        {
            var previousReplicas = previousSnapshot?.AgInfo?.Replicas;
            EvaluateReplicas(snapshot.Name, snapshot.AgInfo.Replicas, previousReplicas, alertSettings, alerts);
        }

        if (snapshot.DagInfo != null)
        {
            foreach (var member in snapshot.DagInfo.Members)
            {
                if (member.LocalAgInfo == null) continue;

                var previousMember = previousSnapshot?.DagInfo?.Members
                    .FirstOrDefault(m => m.MemberAgName == member.MemberAgName);

                EvaluateReplicas(
                    snapshot.Name,
                    member.LocalAgInfo.Replicas,
                    previousMember?.LocalAgInfo?.Replicas,
                    alertSettings,
                    alerts);
            }
        }

        foreach (var alert in alerts)
        {
            PublishIfNotMuted(alert, alertSettings);
        }
    }

    /// <summary>
    /// Mutes a specific alert type for a group. When <paramref name="duration"/> is null,
    /// the mute is permanent (<see cref="DateTimeOffset.MaxValue"/> sentinel). Timed mutes
    /// auto-expire: <see cref="IsMuted"/> and <see cref="GetMutedAlerts"/> both clean up
    /// expired entries on access.
    /// </summary>
    public void MuteAlert(AlertType alertType, string groupName, TimeSpan? duration)
    {
        var key = MuteKey(alertType, groupName);

        if (duration.HasValue)
        {
            _mutedAlerts[key] = DateTimeOffset.UtcNow + duration.Value;
        }
        else
        {
            // Permanent mute: use MaxValue as sentinel
            _mutedAlerts[key] = DateTimeOffset.MaxValue;
        }

        _logger.LogInformation(
            "Muted alert {AlertType} for group {GroupName} until {Until}",
            alertType, groupName, duration.HasValue ? (DateTimeOffset.UtcNow + duration.Value).ToString("o") : "permanent");
    }

    public void UnmuteAlert(AlertType alertType, string groupName)
    {
        var key = MuteKey(alertType, groupName);
        _mutedAlerts.TryRemove(key, out _);
        _logger.LogInformation("Unmuted alert {AlertType} for group {GroupName}", alertType, groupName);
    }

    /// <summary>
    /// Returns all active mutes, removing any that have expired. Thread-safe via
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Permanent mutes have a null
    /// <c>MutedUntil</c> and <c>IsPermanent = true</c>.
    /// </summary>
    public IReadOnlyList<MutedAlertInfo> GetMutedAlerts()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = new List<string>();
        var result = new List<MutedAlertInfo>();

        foreach (var kvp in _mutedAlerts)
        {
            if (kvp.Value != DateTimeOffset.MaxValue && kvp.Value <= now)
            {
                expiredKeys.Add(kvp.Key);
                continue;
            }

            ParseMuteKey(kvp.Key, out var alertType, out var groupName);
            var isPermanent = kvp.Value == DateTimeOffset.MaxValue;
            result.Add(new MutedAlertInfo(alertType, groupName, isPermanent ? null : kvp.Value, isPermanent));
        }

        // Auto-unmute expired entries
        foreach (var key in expiredKeys)
        {
            _mutedAlerts.TryRemove(key, out _);
        }

        return result;
    }

    public void Dispose()
    {
        _alertSubject.OnCompleted();
        _alertSubject.Dispose();
    }

    private void DetectConnectionLostRestored(
        MonitoredGroupSnapshot current,
        MonitoredGroupSnapshot? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.IsConnected && !current.IsConnected)
        {
            alerts.Add(CreateAlert(
                AlertType.ConnectionLost,
                current.Name,
                null,
                null,
                $"Connection lost to monitored group '{current.Name}'",
                AlertSeverity.Critical));
        }
        else if (!previous.IsConnected && current.IsConnected)
        {
            alerts.Add(CreateAlert(
                AlertType.ConnectionRestored,
                current.Name,
                null,
                null,
                $"Connection restored to monitored group '{current.Name}'",
                AlertSeverity.Information));
        }
    }

    private void DetectHealthDegraded(
        MonitoredGroupSnapshot current,
        MonitoredGroupSnapshot? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.OverallHealth == SynchronizationHealth.Healthy &&
            current.OverallHealth is SynchronizationHealth.PartiallyHealthy or SynchronizationHealth.NotHealthy)
        {
            var severity = current.OverallHealth == SynchronizationHealth.NotHealthy
                ? AlertSeverity.Critical
                : AlertSeverity.Warning;

            alerts.Add(CreateAlert(
                AlertType.HealthDegraded,
                current.Name,
                null,
                null,
                $"Health degraded for group '{current.Name}': {previous.OverallHealth} → {current.OverallHealth}",
                severity));
        }
    }

    private void EvaluateReplicas(
        string groupName,
        IEnumerable<ReplicaInfo> currentReplicas,
        IEnumerable<ReplicaInfo>? previousReplicas,
        AlertSettings alertSettings,
        List<AlertEvent> alerts)
    {
        var previousByName = previousReplicas?
            .ToDictionary(r => r.ReplicaServerName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ReplicaInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var replica in currentReplicas)
        {
            previousByName.TryGetValue(replica.ReplicaServerName, out var prevReplica);

            DetectReplicaDisconnected(groupName, replica, prevReplica, alerts);
            DetectConnectionRestored(groupName, replica, prevReplica, alerts);
            DetectFailover(groupName, replica, prevReplica, alerts);
            DetectSyncModeChanged(groupName, replica, prevReplica, alerts);
            DetectDatabaseStateChanges(groupName, replica, prevReplica, alertSettings, alerts);
        }
    }

    private void DetectReplicaDisconnected(
        string groupName,
        ReplicaInfo current,
        ReplicaInfo? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.ConnectedState != ConnectedState.Disconnected &&
            current.ConnectedState == ConnectedState.Disconnected)
        {
            alerts.Add(CreateAlert(
                AlertType.ReplicaDisconnected,
                groupName,
                current.ReplicaServerName,
                null,
                $"Replica '{current.ReplicaServerName}' disconnected from group '{groupName}'",
                AlertSeverity.Critical));
        }
    }

    private void DetectConnectionRestored(
        string groupName,
        ReplicaInfo current,
        ReplicaInfo? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.ConnectedState == ConnectedState.Disconnected &&
            current.ConnectedState == ConnectedState.Connected)
        {
            alerts.Add(CreateAlert(
                AlertType.ConnectionRestored,
                groupName,
                current.ReplicaServerName,
                null,
                $"Replica '{current.ReplicaServerName}' reconnected to group '{groupName}'",
                AlertSeverity.Information));
        }
    }

    private void DetectFailover(
        string groupName,
        ReplicaInfo current,
        ReplicaInfo? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.Role != current.Role &&
            previous.Role is ReplicaRole.Primary or ReplicaRole.Secondary &&
            current.Role is ReplicaRole.Primary or ReplicaRole.Secondary)
        {
            alerts.Add(CreateAlert(
                AlertType.FailoverOccurred,
                groupName,
                current.ReplicaServerName,
                null,
                $"Failover detected on replica '{current.ReplicaServerName}' in group '{groupName}': {previous.Role} → {current.Role}",
                AlertSeverity.Warning));
        }
    }

    private void DetectSyncModeChanged(
        string groupName,
        ReplicaInfo current,
        ReplicaInfo? previous,
        List<AlertEvent> alerts)
    {
        if (previous == null) return;

        if (previous.AvailabilityMode != current.AvailabilityMode)
        {
            alerts.Add(CreateAlert(
                AlertType.SyncModeChanged,
                groupName,
                current.ReplicaServerName,
                null,
                $"Availability mode changed on replica '{current.ReplicaServerName}' in group '{groupName}': {previous.AvailabilityMode} → {current.AvailabilityMode}",
                AlertSeverity.Information));
        }
    }

    private void DetectDatabaseStateChanges(
        string groupName,
        ReplicaInfo currentReplica,
        ReplicaInfo? previousReplica,
        AlertSettings alertSettings,
        List<AlertEvent> alerts)
    {
        var previousDbByName = previousReplica?.DatabaseStates
            .ToDictionary(d => d.DatabaseName, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DatabaseReplicaState>(StringComparer.OrdinalIgnoreCase);

        // SyncFellBehind: default 1,000,000 bytes (~1 MB). Only alert on the
        // threshold-crossing transition (not while continuously above), so repeated
        // polls with a large diff don't produce duplicate alerts.
        // Any VLF difference also triggers SyncFellBehind since it means the secondary
        // is at least one full VLF behind.
        var syncBehindThreshold = GetThreshold(alertSettings, AlertType.SyncFellBehind, defaultValue: 1_000_000);

        foreach (var db in currentReplica.DatabaseStates)
        {
            previousDbByName.TryGetValue(db.DatabaseName, out var prevDb);

            // SyncFellBehind: log block difference exceeds threshold or VLFs differ
            bool isBehind = db.VlfDifference > 0 || db.LogBlockDifference > syncBehindThreshold;
            if (isBehind)
            {
                bool wasBelowThreshold = prevDb == null
                    || (prevDb.VlfDifference == 0 && prevDb.LogBlockDifference <= syncBehindThreshold);
                if (wasBelowThreshold)
                {
                    var lagDescription = LsnHelper.FormatLag(db.LastHardenedLsn, db.LastHardenedLsn)
                        is "in sync" ? $"{LsnHelper.FormatBytes(db.LogBlockDifference)} behind"
                        : LsnHelper.FormatLag(
                            /* primary LSN is not directly available here; approximate from diff */
                            db.LastHardenedLsn + (decimal)db.LogBlockDifference * 100_000m,
                            db.LastHardenedLsn);

                    alerts.Add(CreateAlert(
                        AlertType.SyncFellBehind,
                        groupName,
                        currentReplica.ReplicaServerName,
                        db.DatabaseName,
                        $"Database '{db.DatabaseName}' on replica '{currentReplica.ReplicaServerName}' fell behind — {(db.VlfDifference > 0 ? $"{db.VlfDifference} VLF(s) + " : "")}{LsnHelper.FormatBytes(db.LogBlockDifference)} (threshold: {LsnHelper.FormatBytes(syncBehindThreshold)})",
                        AlertSeverity.Warning));
                }
            }

            if (prevDb == null) continue;

            // SuspendDetected
            if (!prevDb.IsSuspended && db.IsSuspended)
            {
                alerts.Add(CreateAlert(
                    AlertType.SuspendDetected,
                    groupName,
                    currentReplica.ReplicaServerName,
                    db.DatabaseName,
                    $"Database '{db.DatabaseName}' on replica '{currentReplica.ReplicaServerName}' was suspended" +
                        (db.SuspendReason != null ? $" (reason: {db.SuspendReason})" : string.Empty),
                    AlertSeverity.Warning));
            }

            // ResumeDetected
            if (prevDb.IsSuspended && !db.IsSuspended)
            {
                alerts.Add(CreateAlert(
                    AlertType.ResumeDetected,
                    groupName,
                    currentReplica.ReplicaServerName,
                    db.DatabaseName,
                    $"Database '{db.DatabaseName}' on replica '{currentReplica.ReplicaServerName}' was resumed",
                    AlertSeverity.Information));
            }
        }
    }

    /// <summary>
    /// Publishes an alert only if (1) the alert type is enabled, (2) it is not muted for
    /// the group, and (3) the global cooldown has elapsed. This three-stage gate prevents
    /// alert flooding during cascading failures.
    /// </summary>
    private void PublishIfNotMuted(AlertEvent alert, AlertSettings alertSettings)
    {
        var alertTypeConfig = GetAlertTypeConfig(alertSettings, alert.AlertType);

        if (alertTypeConfig is { Enabled: false })
        {
            _logger.LogDebug("Alert {AlertType} is disabled by configuration", alert.AlertType);
            return;
        }

        if (IsMuted(alert.AlertType, alert.GroupName))
        {
            _logger.LogDebug(
                "Alert {AlertType} for group {GroupName} is muted, skipping",
                alert.AlertType, alert.GroupName);
            return;
        }

        if (!TryPassCooldown(alertSettings))
        {
            _logger.LogDebug("Alert {AlertType} suppressed by master cooldown", alert.AlertType);
            return;
        }

        _logger.LogInformation(
            "Alert raised: {AlertType} [{Severity}] for group {GroupName}: {Message}",
            alert.AlertType, alert.Severity, alert.GroupName, alert.Message);

        _alertSubject.OnNext(alert);
    }

    /// <summary>
    /// Global cooldown gate. After any alert is published, all subsequent alerts are
    /// suppressed for <see cref="AlertSettings.MasterCooldownMinutes"/> (default 5 min,
    /// clamped to ≥ 0). Uses a lock to ensure exactly one alert passes per cooldown window.
    /// </summary>
    private bool TryPassCooldown(AlertSettings alertSettings)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, alertSettings.MasterCooldownMinutes));
        var now = DateTimeOffset.UtcNow;

        lock (_cooldownLock)
        {
            if (now - _lastAlertTime < cooldown)
            {
                return false;
            }

            _lastAlertTime = now;
            return true;
        }
    }

    /// <summary>
    /// Checks whether an alert is currently muted. Timed mutes that have expired are
    /// automatically removed from the dictionary. Permanent mutes use
    /// <see cref="DateTimeOffset.MaxValue"/> as a sentinel and never expire.
    /// </summary>
    private bool IsMuted(AlertType alertType, string groupName)
    {
        var key = MuteKey(alertType, groupName);

        if (!_mutedAlerts.TryGetValue(key, out var mutedUntil))
        {
            return false;
        }

        // Permanent mute
        if (mutedUntil == DateTimeOffset.MaxValue)
        {
            return true;
        }

        // Timed mute: check expiration and auto-unmute
        if (DateTimeOffset.UtcNow >= mutedUntil)
        {
            _mutedAlerts.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    private AlertEvent CreateAlert(
        AlertType alertType,
        string groupName,
        string? replicaName,
        string? databaseName,
        string message,
        AlertSeverity severity)
    {
        return new AlertEvent
        {
            Id = Interlocked.Increment(ref _nextId),
            Timestamp = DateTimeOffset.UtcNow,
            AlertType = alertType,
            GroupName = groupName,
            ReplicaName = replicaName,
            DatabaseName = databaseName,
            Message = message,
            Severity = severity
        };
    }

    /// <summary>
    /// Returns the configured threshold for an alert type, falling back to
    /// <paramref name="defaultValue"/> if no override exists. Used by SyncFellBehind
    /// detection where the default threshold is 1,000,000 log blocks (~1 MB).
    /// </summary>
    private static int GetThreshold(AlertSettings settings, AlertType alertType, int defaultValue)
    {
        if (settings.AlertTypeOverrides.TryGetValue(alertType.ToString(), out var config) &&
            config.ThresholdValue.HasValue)
        {
            return config.ThresholdValue.Value;
        }

        return defaultValue;
    }

    private static AlertTypeConfig? GetAlertTypeConfig(AlertSettings settings, AlertType alertType)
    {
        settings.AlertTypeOverrides.TryGetValue(alertType.ToString(), out var config);
        return config;
    }

    private static string MuteKey(AlertType alertType, string groupName)
        => $"{alertType}|{groupName}";

    private static void ParseMuteKey(string key, out AlertType alertType, out string groupName)
    {
        var separatorIndex = key.IndexOf('|');
        alertType = Enum.Parse<AlertType>(key[..separatorIndex]);
        groupName = key[(separatorIndex + 1)..];
    }
}
