using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.Tests.Monitoring;

public sealed class SqlParsingHelpersTests
{
    #region ParseRole

    [Theory]
    [InlineData(null, ReplicaRole.Unknown)]
    [InlineData("PRIMARY", ReplicaRole.Primary)]
    [InlineData("primary", ReplicaRole.Primary)]
    [InlineData("Primary", ReplicaRole.Primary)]
    [InlineData("SECONDARY", ReplicaRole.Secondary)]
    [InlineData("RESOLVING", ReplicaRole.Resolving)]
    [InlineData("garbage", ReplicaRole.Unknown)]
    [InlineData("", ReplicaRole.Unknown)]
    public void ParseRole_ReturnsExpectedValue(string? input, ReplicaRole expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseRole(input));
    }

    #endregion

    #region ParseSyncState

    [Theory]
    [InlineData(null, SynchronizationState.Unknown)]
    [InlineData("SYNCHRONIZED", SynchronizationState.Synchronized)]
    [InlineData("SYNCHRONIZING", SynchronizationState.Synchronizing)]
    [InlineData("NOT SYNCHRONIZING", SynchronizationState.NotSynchronizing)]
    [InlineData("NOT_SYNCHRONIZING", SynchronizationState.NotSynchronizing)]
    [InlineData("REVERTING", SynchronizationState.Reverting)]
    [InlineData("INITIALIZING", SynchronizationState.Initializing)]
    [InlineData("unknown_value", SynchronizationState.Unknown)]
    public void ParseSyncState_ReturnsExpectedValue(string? input, SynchronizationState expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseSyncState(input));
    }

    #endregion

    #region ParseSyncHealth

    [Theory]
    [InlineData("HEALTHY", SynchronizationHealth.Healthy)]
    [InlineData("PARTIALLY_HEALTHY", SynchronizationHealth.PartiallyHealthy)]
    [InlineData("PARTIALLY HEALTHY", SynchronizationHealth.PartiallyHealthy)]
    [InlineData("NOT_HEALTHY", SynchronizationHealth.NotHealthy)]
    [InlineData("NOT HEALTHY", SynchronizationHealth.NotHealthy)]
    [InlineData(null, SynchronizationHealth.Unknown)]
    [InlineData("bad", SynchronizationHealth.Unknown)]
    public void ParseSyncHealth_ReturnsExpectedValue(string? input, SynchronizationHealth expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseSyncHealth(input));
    }

    #endregion

    #region ParseAvailabilityMode

    [Theory]
    [InlineData("SYNCHRONOUS_COMMIT", AvailabilityMode.SynchronousCommit)]
    [InlineData("SYNCHRONOUS COMMIT", AvailabilityMode.SynchronousCommit)]
    [InlineData("ASYNCHRONOUS_COMMIT", AvailabilityMode.AsynchronousCommit)]
    [InlineData("ASYNCHRONOUS COMMIT", AvailabilityMode.AsynchronousCommit)]
    [InlineData("CONFIGURATION_ONLY", AvailabilityMode.ConfigurationOnly)]
    [InlineData("CONFIGURATION ONLY", AvailabilityMode.ConfigurationOnly)]
    [InlineData(null, AvailabilityMode.Unknown)]
    public void ParseAvailabilityMode_ReturnsExpectedValue(string? input, AvailabilityMode expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseAvailabilityMode(input));
    }

    #endregion

    #region ParseConnectedState

    [Theory]
    [InlineData("CONNECTED", ConnectedState.Connected)]
    [InlineData("DISCONNECTED", ConnectedState.Disconnected)]
    [InlineData(null, ConnectedState.Unknown)]
    [InlineData("other", ConnectedState.Unknown)]
    public void ParseConnectedState_ReturnsExpectedValue(string? input, ConnectedState expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseConnectedState(input));
    }

    #endregion

    #region ParseOperationalState

    [Theory]
    [InlineData("ONLINE", OperationalState.Online)]
    [InlineData("OFFLINE", OperationalState.Offline)]
    [InlineData("PENDING", OperationalState.Pending)]
    [InlineData("PENDING_FAILOVER", OperationalState.PendingFailover)]
    [InlineData("PENDING FAILOVER", OperationalState.PendingFailover)]
    [InlineData("FAILED", OperationalState.Failed)]
    [InlineData("FAILED_NO_QUORUM", OperationalState.FailedNoQuorum)]
    [InlineData("FAILED NO QUORUM", OperationalState.FailedNoQuorum)]
    [InlineData(null, OperationalState.Unknown)]
    public void ParseOperationalState_ReturnsExpectedValue(string? input, OperationalState expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseOperationalState(input));
    }

    #endregion

    #region ParseRecoveryHealth

    [Theory]
    [InlineData("ONLINE", RecoveryHealth.Online)]
    [InlineData("ONLINE_IN_PROGRESS", RecoveryHealth.InProgress)]
    [InlineData("ONLINE IN PROGRESS", RecoveryHealth.InProgress)]
    [InlineData("IN_PROGRESS", RecoveryHealth.InProgress)]
    [InlineData("IN PROGRESS", RecoveryHealth.InProgress)]
    [InlineData(null, RecoveryHealth.Unknown)]
    [InlineData("other", RecoveryHealth.Unknown)]
    public void ParseRecoveryHealth_ReturnsExpectedValue(string? input, RecoveryHealth expected)
    {
        Assert.Equal(expected, SqlParsingHelpers.ParseRecoveryHealth(input));
    }

    #endregion

    #region ComputeOverallHealth

    [Fact]
    public void ComputeOverallHealth_EmptyList_ReturnsUnknown()
    {
        var result = SqlParsingHelpers.ComputeOverallHealth(Array.Empty<ReplicaInfo>());

        Assert.Equal(SynchronizationHealth.Unknown, result);
    }

    [Fact]
    public void ComputeOverallHealth_AllHealthy_ReturnsHealthy()
    {
        var replicas = new List<ReplicaInfo>
        {
            new() { SynchronizationHealth = SynchronizationHealth.Healthy },
            new() { SynchronizationHealth = SynchronizationHealth.Healthy }
        };

        var result = SqlParsingHelpers.ComputeOverallHealth(replicas);

        Assert.Equal(SynchronizationHealth.Healthy, result);
    }

    [Fact]
    public void ComputeOverallHealth_OneNotHealthy_ReturnsNotHealthy()
    {
        var replicas = new List<ReplicaInfo>
        {
            new() { SynchronizationHealth = SynchronizationHealth.Healthy },
            new() { SynchronizationHealth = SynchronizationHealth.NotHealthy }
        };

        var result = SqlParsingHelpers.ComputeOverallHealth(replicas);

        Assert.Equal(SynchronizationHealth.NotHealthy, result);
    }

    [Fact]
    public void ComputeOverallHealth_MixedWithoutNotHealthy_ReturnsPartiallyHealthy()
    {
        var replicas = new List<ReplicaInfo>
        {
            new() { SynchronizationHealth = SynchronizationHealth.Healthy },
            new() { SynchronizationHealth = SynchronizationHealth.PartiallyHealthy }
        };

        var result = SqlParsingHelpers.ComputeOverallHealth(replicas);

        Assert.Equal(SynchronizationHealth.PartiallyHealthy, result);
    }

    #endregion
}
