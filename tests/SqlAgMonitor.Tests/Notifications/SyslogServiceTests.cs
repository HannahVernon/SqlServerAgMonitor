using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.Tests.Notifications;

public sealed class SyslogServiceTests
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<SyslogService> _logger;

    public SyslogServiceTests()
    {
        _configService = Substitute.For<IConfigurationService>();
        _logger = Substitute.For<ILogger<SyslogService>>();
    }

    private SyslogService CreateService() => new(_configService, _logger);

    private void ConfigureSyslog(string server, int port, string protocol = "UDP", string facility = "local0")
    {
        var config = new AppConfiguration
        {
            Syslog = new SyslogSettings
            {
                Enabled = true,
                Server = server,
                Port = port,
                Protocol = protocol,
                Facility = facility
            }
        };
        _configService.Load().Returns(config);
    }

    private static AlertEvent CreateAlert(
        AlertSeverity severity = AlertSeverity.Critical,
        AlertType alertType = AlertType.ConnectionLost,
        string groupName = "AG-Prod",
        string message = "Connection lost to primary replica.",
        string? replicaName = null,
        string? databaseName = null) =>
        new()
        {
            Id = 1,
            Timestamp = new DateTimeOffset(2025, 7, 15, 12, 0, 0, TimeSpan.Zero),
            AlertType = alertType,
            GroupName = groupName,
            Message = message,
            Severity = severity,
            ReplicaName = replicaName,
            DatabaseName = databaseName
        };

    private static async Task<string> ReceiveUdpMessage(UdpClient listener, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var result = await listener.ReceiveAsync(cts.Token);
        return Encoding.UTF8.GetString(result.Buffer);
    }

    [Fact]
    public async Task SendEventAsync_WithValidUdpConfig_SendsDataToListener()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert());

        var received = await ReceiveUdpMessage(listener);
        Assert.NotEmpty(received);
    }

    [Fact]
    public async Task SendEventAsync_MessageStartsWithRfc5424PriorityHeader()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert());

        var received = await ReceiveUdpMessage(listener);
        // local0 (16) * 8 + Critical (2) = 130
        Assert.StartsWith("<130>1 ", received);
    }

    [Theory]
    [InlineData("local0", AlertSeverity.Critical, 130)]   // 16*8+2
    [InlineData("local0", AlertSeverity.Warning, 132)]     // 16*8+4
    [InlineData("local0", AlertSeverity.Information, 134)] // 16*8+6
    [InlineData("local7", AlertSeverity.Critical, 186)]    // 23*8+2
    [InlineData("kern", AlertSeverity.Critical, 2)]         // 0*8+2
    [InlineData("user", AlertSeverity.Warning, 12)]         // 1*8+4
    public async Task PriorityCalculation_CorrectForFacilityAndSeverity(
        string facility, AlertSeverity severity, int expectedPriority)
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port, facility: facility);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert(severity: severity));

        var received = await ReceiveUdpMessage(listener);
        Assert.StartsWith($"<{expectedPriority}>1 ", received);
    }

    [Fact]
    public async Task SendEventAsync_MessageContainsGroupNameAndAlertInfo()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert(groupName: "MyAG", message: "Something broke"));

        var received = await ReceiveUdpMessage(listener);
        Assert.Contains("AG=MyAG", received);
        Assert.Contains("ConnectionLost", received);
        Assert.Contains("Something broke", received);
    }

    [Fact]
    public async Task SendEventAsync_MessageContainsReplicaName_WhenProvided()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert(replicaName: "SQL-NODE1"));

        var received = await ReceiveUdpMessage(listener);
        Assert.Contains("Replica=SQL-NODE1", received);
    }

    [Fact]
    public async Task SendEventAsync_MessageContainsDatabaseName_WhenProvided()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        await service.SendEventAsync(CreateAlert(databaseName: "OrdersDB"));

        var received = await ReceiveUdpMessage(listener);
        Assert.Contains("Database=OrdersDB", received);
    }

    [Fact]
    public async Task ValidateSettings_ThrowsWhenServerIsEmpty()
    {
        ConfigureSyslog("", 514);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendEventAsync(CreateAlert()));
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsTrue_OnSuccess()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port);
        var service = CreateService();

        var result = await service.TestConnectionAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_ReturnsFalse_WhenServerUnreachable()
    {
        // Empty server triggers validation failure → returns false
        ConfigureSyslog("", 514);
        var service = CreateService();

        var result = await service.TestConnectionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task DefaultFacility_UnknownString_MapsToLocal0()
    {
        using var listener = new UdpClient(0);
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        ConfigureSyslog("127.0.0.1", port, facility: "unknown_facility");
        var service = CreateService();

        // local0 (16) * 8 + Information (6) = 134
        await service.SendEventAsync(CreateAlert(severity: AlertSeverity.Information));

        var received = await ReceiveUdpMessage(listener);
        Assert.StartsWith("<134>1 ", received);
    }
}
