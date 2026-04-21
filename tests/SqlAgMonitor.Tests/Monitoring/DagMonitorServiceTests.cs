using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.Tests.Monitoring;

public sealed class DagMonitorServiceTests : IAsyncDisposable
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<DagMonitorService> _logger;
    private readonly DagMonitorService _service;

    public DagMonitorServiceTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _configService = Substitute.For<IConfigurationService>();
        _logger = NullLoggerFactory.Instance.CreateLogger<DagMonitorService>();
        _service = new DagMonitorService(_connectionService, _configService, _logger);
    }

    public async ValueTask DisposeAsync() => await _service.DisposeAsync();

    [Fact]
    public void Snapshots_IsNotNull()
    {
        Assert.NotNull(_service.Snapshots);
    }

    [Fact]
    public async Task StopMonitoringAsync_WhenNotStarted_DoesNotThrow()
    {
        var ex = await Record.ExceptionAsync(() => _service.StopMonitoringAsync("NonExistent"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartMonitoringAsync_NoConfig_DoesNotThrow()
    {
        _configService.Load().Returns(new AppConfiguration());

        var ex = await Record.ExceptionAsync(() => _service.StartMonitoringAsync("MissingDAG"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartMonitoringAsync_TwiceForSameGroup_DoesNotThrow()
    {
        var config = CreateDagConfig("TestDAG", "Server1", "Server2");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("TestDAG");
        var ex = await Record.ExceptionAsync(() => _service.StartMonitoringAsync("TestDAG"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task PollOnceAsync_NoConfig_ThrowsInvalidOperation()
    {
        _configService.Load().Returns(new AppConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PollOnceAsync("NoSuchDAG"));
    }

    [Fact]
    public async Task StopMonitoringAsync_AfterStart_DoesNotThrow()
    {
        var config = CreateDagConfig("TestDAG", "Server1", "Server2");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("TestDAG");
        var ex = await Record.ExceptionAsync(() => _service.StopMonitoringAsync("TestDAG"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartMonitoringAsync_UsesConfiguredGroupName()
    {
        var config = CreateDagConfig("MyDAG", "PrimaryServer", "SecondaryServer");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("MyDAG");

        _configService.Received().Load();
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_DoesNotThrow()
    {
        var config = CreateDagConfig("TestDAG", "Server1", "Server2");
        _configService.Load().Returns(config);
        await _service.StartMonitoringAsync("TestDAG");

        var ex = await Record.ExceptionAsync(async () => await _service.DisposeAsync());
        Assert.Null(ex);
    }

    private static AppConfiguration CreateDagConfig(string groupName, params string[] servers)
    {
        var connections = servers.Select(s => new ConnectionConfig
        {
            Server = s,
            AuthType = "windows"
        }).ToList();

        return new AppConfiguration
        {
            MonitoredGroups = new List<MonitoredGroupConfig>
            {
                new MonitoredGroupConfig
                {
                    Name = groupName,
                    GroupType = "DistributedAvailabilityGroup",
                    PollingIntervalSeconds = 5,
                    Connections = connections
                }
            }
        };
    }
}
