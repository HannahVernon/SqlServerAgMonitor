using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.Tests.Monitoring;

public sealed class AgControlServiceTests
{
    private readonly ISqlConnectionService _connectionService;
    private readonly ILogger<AgControlService> _logger;
    private readonly AgControlService _sut;

    public AgControlServiceTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _logger = NullLoggerFactory.Instance.CreateLogger<AgControlService>();
        _sut = new AgControlService(_connectionService, _logger);
    }

    [Fact]
    public async Task FailoverAsync_PassesTargetReplica_AsServer()
    {
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync("TargetServer", null, null, "windows", true, false, Arg.Any<CancellationToken>())
            .Returns(conn);

        await _sut.FailoverAsync("AG1", "TargetServer");

        await _connectionService.Received(1).GetConnectionAsync(
            "TargetServer", null, null, "windows", true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceFailoverAsync_PassesTargetReplica_AsServer()
    {
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync("TargetServer", null, null, "windows", true, false, Arg.Any<CancellationToken>())
            .Returns(conn);

        await _sut.ForceFailoverAsync("AG1", "TargetServer");

        await _connectionService.Received(1).GetConnectionAsync(
            "TargetServer", null, null, "windows", true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SuspendDatabaseAsync_PassesDot_AsServer()
    {
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync(".", null, null, "windows", true, false, Arg.Any<CancellationToken>())
            .Returns(conn);

        await _sut.SuspendDatabaseAsync("AG1", "DB1");

        await _connectionService.Received(1).GetConnectionAsync(
            ".", null, null, "windows", true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeDatabaseAsync_PassesDot_AsServer()
    {
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync(".", null, null, "windows", true, false, Arg.Any<CancellationToken>())
            .Returns(conn);

        await _sut.ResumeDatabaseAsync("AG1", "DB1");

        await _connectionService.Received(1).GetConnectionAsync(
            ".", null, null, "windows", true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailoverAsync_ReturnsFalse_OnConnectionFailure()
    {
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var result = await _sut.FailoverAsync("AG1", "Server1");

        Assert.False(result);
    }

    [Fact]
    public async Task SetAvailabilityMode_WithUnknown_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _sut.SetAvailabilityModeAsync("AG1", "Server1", AvailabilityMode.Unknown));
    }

    [Fact]
    public async Task ReturnConnection_CalledAfterExecution()
    {
        // Real SqlConnection on a fake server — ExecuteNonQueryAsync will fail,
        // but ReturnConnection should still be called in the finally block.
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(conn);

        // Will return false because ExecuteNonQueryAsync fails on a non-open connection
        await _sut.FailoverAsync("AG1", "Server1");

        _connectionService.Received(1).ReturnConnection("Server1", conn);
    }

    [Fact]
    public async Task ReturnConnection_CalledEvenOnCommandFailure()
    {
        var conn = new SqlConnection("Data Source=fake;");
        _connectionService.GetConnectionAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(conn);

        // SuspendDatabase uses "." as the server
        var result = await _sut.SuspendDatabaseAsync("AG1", "DB1");

        // Should be false (command fails) but ReturnConnection still called
        Assert.False(result);
        _connectionService.Received(1).ReturnConnection(".", conn);
    }
}
