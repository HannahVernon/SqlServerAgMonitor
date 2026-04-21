using System.Reactive.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SqlAgMonitor.Core.Services.Connection;

namespace SqlAgMonitor.Tests.Connection;

public sealed class ReconnectingConnectionWrapperTests : IAsyncLifetime
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger _logger;
    private ReconnectingConnectionWrapper? _wrapper;

    private const string TestServer = "TestServer01";
    private const string TestUsername = "sa";
    private const string TestCredKey = "cred-key-1";
    private const string TestAuthType = "SQL";

    public ReconnectingConnectionWrapperTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _logger = Substitute.For<ILogger>();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_wrapper != null)
            await _wrapper.DisposeAsync();
    }

    private ReconnectingConnectionWrapper CreateWrapper(
        bool encrypt = true, bool trustServerCert = false)
    {
        _wrapper = new ReconnectingConnectionWrapper(
            _connectionService, _logger, TestServer, TestUsername,
            TestCredKey, TestAuthType, encrypt, trustServerCert);
        return _wrapper;
    }

    [Fact]
    public void Constructor_SetsServerProperty()
    {
        var wrapper = CreateWrapper();

        Assert.Equal(TestServer, wrapper.Server);
    }

    [Fact]
    public void IsConnected_FalseInitially()
    {
        var wrapper = CreateWrapper();

        Assert.False(wrapper.IsConnected);
    }

    [Fact]
    public async Task AcquireAsync_CallsGetConnectionAsync_WithCorrectParameters()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("test failure"));

        var wrapper = CreateWrapper(encrypt: true, trustServerCert: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.AcquireAsync());

        await _connectionService.Received(1).GetConnectionAsync(
            TestServer, TestUsername, TestCredKey, TestAuthType, true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireAsync_WhenConnectionFails_EmitsDisconnectedStateChange()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("conn failed"));

        var wrapper = CreateWrapper();
        var stateChanges = new List<ConnectionStateChange>();
        using var sub = wrapper.StateChanges.Subscribe(sc => stateChanges.Add(sc));

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.AcquireAsync());

        Assert.Single(stateChanges);
        Assert.False(stateChanges[0].IsConnected);
        Assert.Equal(TestServer, stateChanges[0].Server);
        Assert.NotNull(stateChanges[0].ErrorMessage);
    }

    [Fact]
    public async Task TryAcquireAsync_ReturnsNull_WhenSemaphoreIsHeld()
    {
        var sqlConn = new SqlConnection();
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sqlConn));

        var wrapper = CreateWrapper();

        // Acquire a lease — this holds the semaphore
        var lease = await wrapper.AcquireAsync();

        // While the lease is held, TryAcquire should return null
        var secondLease = await wrapper.TryAcquireAsync();
        Assert.Null(secondLease);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task LeaseDisposeAsync_ReleasesLock_TryAcquireSucceedsAfter()
    {
        // Set up a connection that returns a new SqlConnection (will be Closed state)
        var sqlConn = new SqlConnection();
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sqlConn));

        var wrapper = CreateWrapper();

        // The connection won't be Open (no real server), so AcquireInternalAsync
        // will try GetConnectionAsync and succeed but the returned connection's State is Closed.
        // The wrapper stores it but won't think it's "already connected" on second call.
        // AcquireAsync should return a lease since GetConnectionAsync doesn't throw.
        var lease1 = await wrapper.AcquireAsync();
        Assert.NotNull(lease1);

        // While lease1 is held, TryAcquire should return null
        var lease2 = await wrapper.TryAcquireAsync();
        Assert.Null(lease2);

        // After disposing lease1, TryAcquire should succeed
        await lease1.DisposeAsync();

        var lease3 = await wrapper.TryAcquireAsync();
        Assert.NotNull(lease3);
        if (lease3 != null)
            await lease3.DisposeAsync();
    }

    [Fact]
    public async Task InvalidateConnectionInternal_EmitsDisconnectedState()
    {
        var sqlConn = new SqlConnection();
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(sqlConn));

        var wrapper = CreateWrapper();
        var stateChanges = new List<ConnectionStateChange>();
        using var sub = wrapper.StateChanges.Subscribe(sc => stateChanges.Add(sc));

        var lease = await wrapper.AcquireAsync();

        // First state change is "connected" from AcquireAsync success
        stateChanges.Clear();

        lease.Invalidate();

        Assert.Single(stateChanges);
        Assert.False(stateChanges[0].IsConnected);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesCleanly()
    {
        var wrapper = CreateWrapper();

        await wrapper.DisposeAsync();
        _wrapper = null; // Prevent double-dispose in DisposeAsync lifecycle

        // No exception means success
    }

    [Fact]
    public async Task MultipleRapidFailures_DoNotStartMultipleReconnectLoops()
    {
        var callCount = 0;
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>())
            .Returns<SqlConnection>(_ =>
            {
                Interlocked.Increment(ref callCount);
                throw new InvalidOperationException("still failing");
            });

        var wrapper = CreateWrapper();

        // Trigger multiple rapid acquire attempts that all fail
        for (var i = 0; i < 5; i++)
        {
            try { await wrapper.AcquireAsync(); }
            catch (InvalidOperationException) { }
        }

        // Wait a bit for any reconnect loops to progress
        await Task.Delay(500);

        // GetConnectionAsync should have been called a reasonable number of times
        // (5 for the explicit calls plus possibly 1-2 from reconnect).
        // The key test: there should be only ONE reconnect loop running, not five.
        // We verify by checking the call count doesn't explode.
        Assert.True(callCount <= 10,
            $"Expected reasonable call count but got {callCount} — suggests multiple reconnect loops.");
    }
}
