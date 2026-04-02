using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Tests.Helpers;

namespace SqlAgMonitor.Tests.Alerting;

public sealed class AlertEngineTests : IDisposable
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<AlertEngine> _logger;
    private readonly AlertEngine _engine;
    private readonly List<AlertEvent> _receivedAlerts = new();
    private readonly IDisposable _subscription;

    public AlertEngineTests()
    {
        _configService = Substitute.For<IConfigurationService>();
        _logger = Substitute.For<ILogger<AlertEngine>>();

        var config = new AppConfiguration
        {
            Alerts = new AlertSettings { MasterCooldownMinutes = 0 }
        };
        _configService.Load().Returns(config);

        _engine = new AlertEngine(_configService, _logger);
        _subscription = _engine.Alerts.Subscribe(a => _receivedAlerts.Add(a));
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _engine.Dispose();
    }

    private void ConfigureAlerts(Action<AlertSettings> configure)
    {
        var config = _configService.Load();
        configure(config.Alerts);
    }

    #region ConnectionLost / ConnectionRestored

    [Fact]
    public void ConnectionLost_WhenPreviouslyConnected_RaisesAlert()
    {
        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.ConnectionLost, alert.AlertType);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("TestGroup", alert.GroupName);
    }

    [Fact]
    public void ConnectionRestored_WhenPreviouslyDisconnected_RaisesAlert()
    {
        var previous = new SnapshotBuilder().IsConnected(false).Build();
        var current = new SnapshotBuilder().IsConnected(true).Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.ConnectionRestored, alert.AlertType);
        Assert.Equal(AlertSeverity.Information, alert.Severity);
    }

    [Fact]
    public void ConnectionLost_NoPreviousSnapshot_NoAlert()
    {
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, null);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void ConnectionStaysConnected_NoAlert()
    {
        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(true).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void ConnectionStaysDisconnected_NoAlert()
    {
        var previous = new SnapshotBuilder().IsConnected(false).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region HealthDegraded

    [Fact]
    public void HealthDegraded_FromHealthyToNotHealthy_RaisesCritical()
    {
        var previous = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.Healthy).Build();
        var current = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.NotHealthy).Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.HealthDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void HealthDegraded_FromHealthyToPartiallyHealthy_RaisesWarning()
    {
        var previous = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.Healthy).Build();
        var current = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.PartiallyHealthy).Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.HealthDegraded, alert.AlertType);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void HealthDegraded_FromPartiallyHealthyToNotHealthy_NoAlert()
    {
        var previous = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.PartiallyHealthy).Build();
        var current = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.NotHealthy).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void HealthStaysHealthy_NoAlert()
    {
        var previous = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.Healthy).Build();
        var current = new SnapshotBuilder().WithOverallHealth(SynchronizationHealth.Healthy).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region ReplicaDisconnected

    [Fact]
    public void ReplicaDisconnected_WhenPreviouslyConnected_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Connected))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Disconnected))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.ReplicaDisconnected, alert.AlertType);
        Assert.Equal("SERVER1", alert.ReplicaName);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public void ReplicaReconnected_RaisesConnectionRestored()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Disconnected))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Connected))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.ConnectionRestored, alert.AlertType);
        Assert.Equal("SERVER1", alert.ReplicaName);
    }

    [Fact]
    public void NewReplica_NoPreviousSnapshot_NoDisconnectAlert()
    {
        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Disconnected))
            .Build();

        _engine.EvaluateSnapshot(current, null);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region FailoverDetected

    [Fact]
    public void Failover_SecondaryToPrimary_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary)
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Primary)
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.FailoverOccurred, alert.AlertType);
        Assert.Equal("SERVER1", alert.ReplicaName);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("Secondary", alert.Message);
        Assert.Contains("Primary", alert.Message);
    }

    [Fact]
    public void Failover_PrimaryToSecondary_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Primary)
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary)
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.FailoverOccurred, alert.AlertType);
    }

    [Fact]
    public void RoleStaysSame_NoAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Primary)
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Primary)
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region SyncModeChanged

    [Fact]
    public void SyncModeChanged_SyncToAsync_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithAvailabilityMode(AvailabilityMode.SynchronousCommit))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithAvailabilityMode(AvailabilityMode.AsynchronousCommit))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.SyncModeChanged, alert.AlertType);
        Assert.Equal(AlertSeverity.Information, alert.Severity);
        Assert.Contains("SynchronousCommit", alert.Message);
        Assert.Contains("AsynchronousCommit", alert.Message);
    }

    [Fact]
    public void SyncModeStaysSame_NoAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithAvailabilityMode(AvailabilityMode.SynchronousCommit))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.WithAvailabilityMode(AvailabilityMode.SynchronousCommit))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region SyncFellBehind

    [Fact]
    public void SyncFellBehind_CrossesThreshold_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(500_000)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(2_000_000)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.SyncFellBehind, alert.AlertType);
        Assert.Equal("DB1", alert.DatabaseName);
        Assert.Equal("SERVER1", alert.ReplicaName);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public void SyncFellBehind_StaysAboveThreshold_NoRepeatAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(2_000_000)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(3_000_000)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void SyncFellBehind_BelowThreshold_NoAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(100)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(500)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void SyncFellBehind_CustomThreshold_UsesOverride()
    {
        ConfigureAlerts(a =>
        {
            a.AlertTypeOverrides[AlertType.SyncFellBehind.ToString()] = new AlertTypeConfig
            {
                ThresholdValue = 100
            };
        });

        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(50)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(200)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.SyncFellBehind, alert.AlertType);
    }

    [Fact]
    public void SyncFellBehind_NewDatabase_NoPreviousState_AlertsIfAboveThreshold()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary)
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.WithLogBlockDifference(2_000_000)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.SyncFellBehind, alert.AlertType);
    }

    #endregion

    #region SuspendDetected / ResumeDetected

    [Fact]
    public void SuspendDetected_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(false)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(true, "User requested")))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.SuspendDetected, alert.AlertType);
        Assert.Equal("DB1", alert.DatabaseName);
        Assert.Contains("User requested", alert.Message);
    }

    [Fact]
    public void ResumeDetected_RaisesAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(true)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(false)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        var alert = Assert.Single(_receivedAlerts);
        Assert.Equal(AlertType.ResumeDetected, alert.AlertType);
        Assert.Equal(AlertSeverity.Information, alert.Severity);
    }

    [Fact]
    public void SuspendStaysSuspended_NoAlert()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(true)))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary, r =>
                r.AddDatabase("DB1", d => d.IsSuspended(true)))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    #endregion

    #region Cooldown

    [Fact]
    public void Cooldown_SuppressesSecondAlert()
    {
        ConfigureAlerts(a => a.MasterCooldownMinutes = 60);

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);
        Assert.Single(_receivedAlerts);

        _receivedAlerts.Clear();

        var previous2 = new SnapshotBuilder().IsConnected(false).Build();
        var current2 = new SnapshotBuilder().IsConnected(true).Build();

        _engine.EvaluateSnapshot(current2, previous2);
        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void Cooldown_ZeroMinutes_AllowsAllAlerts()
    {
        ConfigureAlerts(a => a.MasterCooldownMinutes = 0);

        var previous1 = new SnapshotBuilder().IsConnected(true).Build();
        var current1 = new SnapshotBuilder().IsConnected(false).Build();
        _engine.EvaluateSnapshot(current1, previous1);

        var previous2 = new SnapshotBuilder().IsConnected(false).Build();
        var current2 = new SnapshotBuilder().IsConnected(true).Build();
        _engine.EvaluateSnapshot(current2, previous2);

        Assert.Equal(2, _receivedAlerts.Count);
    }

    #endregion

    #region Muting

    [Fact]
    public void MutedAlert_NotPublished()
    {
        _engine.MuteAlert(AlertType.ConnectionLost, "TestGroup", null);

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void UnmutedAlert_IsPublished()
    {
        _engine.MuteAlert(AlertType.ConnectionLost, "TestGroup", null);
        _engine.UnmuteAlert(AlertType.ConnectionLost, "TestGroup");

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Single(_receivedAlerts);
    }

    [Fact]
    public void MuteForDifferentGroup_DoesNotAffectOtherGroups()
    {
        _engine.MuteAlert(AlertType.ConnectionLost, "OtherGroup", null);

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Single(_receivedAlerts);
    }

    [Fact]
    public void GetMutedAlerts_ReturnsPermanentMute()
    {
        _engine.MuteAlert(AlertType.SyncFellBehind, "TestGroup", null);

        var muted = _engine.GetMutedAlerts();

        var info = Assert.Single(muted);
        Assert.Equal(AlertType.SyncFellBehind, info.AlertType);
        Assert.Equal("TestGroup", info.GroupName);
        Assert.True(info.IsPermanent);
        Assert.Null(info.MutedUntil);
    }

    [Fact]
    public void GetMutedAlerts_ReturnsTimedMute()
    {
        _engine.MuteAlert(AlertType.SyncFellBehind, "TestGroup", TimeSpan.FromHours(1));

        var muted = _engine.GetMutedAlerts();

        var info = Assert.Single(muted);
        Assert.False(info.IsPermanent);
        Assert.NotNull(info.MutedUntil);
        Assert.True(info.MutedUntil > DateTimeOffset.UtcNow);
    }

    #endregion

    #region AlertType Disabled

    [Fact]
    public void DisabledAlertType_NotPublished()
    {
        ConfigureAlerts(a =>
        {
            a.AlertTypeOverrides[AlertType.ConnectionLost.ToString()] = new AlertTypeConfig
            {
                Enabled = false
            };
        });

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Empty(_receivedAlerts);
    }

    [Fact]
    public void EnabledAlertType_IsPublished()
    {
        ConfigureAlerts(a =>
        {
            a.AlertTypeOverrides[AlertType.ConnectionLost.ToString()] = new AlertTypeConfig
            {
                Enabled = true
            };
        });

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Single(_receivedAlerts);
    }

    #endregion

    #region MultipleReplicas

    [Fact]
    public void MultipleReplicas_IndependentAlerts()
    {
        var previous = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Primary)
            .AddReplica("SERVER2", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Connected))
            .Build();

        var current = new SnapshotBuilder()
            .AddReplica("SERVER1", ReplicaRole.Secondary)
            .AddReplica("SERVER2", ReplicaRole.Secondary, r =>
                r.WithConnectedState(ConnectedState.Disconnected))
            .Build();

        _engine.EvaluateSnapshot(current, previous);

        Assert.Equal(2, _receivedAlerts.Count);
        Assert.Contains(_receivedAlerts, a =>
            a.AlertType == AlertType.FailoverOccurred && a.ReplicaName == "SERVER1");
        Assert.Contains(_receivedAlerts, a =>
            a.AlertType == AlertType.ReplicaDisconnected && a.ReplicaName == "SERVER2");
    }

    #endregion

    #region Alert Metadata

    [Fact]
    public void AlertEvent_HasIncrementingId()
    {
        ConfigureAlerts(a => a.MasterCooldownMinutes = 0);

        var previous1 = new SnapshotBuilder().IsConnected(true).Build();
        var current1 = new SnapshotBuilder().IsConnected(false).Build();
        _engine.EvaluateSnapshot(current1, previous1);

        var previous2 = new SnapshotBuilder().IsConnected(false).Build();
        var current2 = new SnapshotBuilder().IsConnected(true).Build();
        _engine.EvaluateSnapshot(current2, previous2);

        Assert.Equal(2, _receivedAlerts.Count);
        Assert.True(_receivedAlerts[1].Id > _receivedAlerts[0].Id);
    }

    [Fact]
    public void AlertEvent_HasTimestamp()
    {
        var before = DateTimeOffset.UtcNow;

        var previous = new SnapshotBuilder().IsConnected(true).Build();
        var current = new SnapshotBuilder().IsConnected(false).Build();
        _engine.EvaluateSnapshot(current, previous);

        var after = DateTimeOffset.UtcNow;
        var alert = Assert.Single(_receivedAlerts);
        Assert.InRange(alert.Timestamp, before, after);
    }

    #endregion
}
