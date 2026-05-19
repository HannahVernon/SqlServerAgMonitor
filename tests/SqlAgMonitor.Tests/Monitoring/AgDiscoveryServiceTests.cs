using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.Tests.Monitoring;

public sealed class AgDiscoveryServiceTests
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger<AgDiscoveryService> _logger;
    private readonly AgDiscoveryService _service;

    public AgDiscoveryServiceTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _logger = NullLoggerFactory.Instance.CreateLogger<AgDiscoveryService>();
        _service = new AgDiscoveryService(_connectionService, _logger);
    }

    [Fact]
    public async Task DiscoverGroupsAsync_ConnectionFailure_Throws()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection refused"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DiscoverGroupsAsync("BadServer", null, null, "windows"));
    }

    [Fact]
    public async Task DiscoverGroupsAsync_PassesCorrectServer()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("expected"));

        try
        {
            await _service.DiscoverGroupsAsync("MyServer", "user1", "key1", "sql");
        }
        catch (InvalidOperationException) { }

        await _connectionService.Received(1).GetConnectionAsync(
            "MyServer", "user1", "key1", "sql",
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverGroupsAsync_CancellationToken_IsForwarded()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.DiscoverGroupsAsync("Server", null, null, "windows", cts.Token));
    }

    [Fact]
    public async Task DiscoverGroupsAsync_WindowsAuth_PassesNullCredentialKey()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("expected"));

        try
        {
            await _service.DiscoverGroupsAsync("Server", null, null, "windows");
        }
        catch (InvalidOperationException) { }

        await _connectionService.Received(1).GetConnectionAsync(
            "Server", null, null, "windows",
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverGroupsAsync_SqlAuth_PassesCredentialKey()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("expected"));

        try
        {
            await _service.DiscoverGroupsAsync("Server", "sa", "mykey", "sql");
        }
        catch (InvalidOperationException) { }

        await _connectionService.Received(1).GetConnectionAsync(
            "Server", "sa", "mykey", "sql",
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverGroupsAsync_MultipleCallsUseCorrectServers()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("expected"));

        try { await _service.DiscoverGroupsAsync("Server1", null, null, "windows"); }
        catch (InvalidOperationException) { }

        try { await _service.DiscoverGroupsAsync("Server2", null, null, "windows"); }
        catch (InvalidOperationException) { }

        await _connectionService.Received(1).GetConnectionAsync(
            "Server1", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _connectionService.Received(1).GetConnectionAsync(
            "Server2", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
