using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Connection;
using SqlAgMonitor.Core.Services.Monitoring;

namespace SqlAgMonitor.Tests.Monitoring;

public sealed class AgMonitorServiceTests : IAsyncDisposable
{
    private readonly ISqlConnectionService _connectionService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<AgMonitorService> _logger;
    private readonly AgMonitorService _service;

    public AgMonitorServiceTests()
    {
        _connectionService = Substitute.For<ISqlConnectionService>();
        _configService = Substitute.For<IConfigurationService>();
        _logger = NullLoggerFactory.Instance.CreateLogger<AgMonitorService>();
        _service = new AgMonitorService(_connectionService, _configService, _logger);
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

        var ex = await Record.ExceptionAsync(() => _service.StartMonitoringAsync("MissingGroup"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartMonitoringAsync_TwiceForSameGroup_DoesNotThrow()
    {
        var config = CreateConfig("TestAG", "TestServer");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("TestAG");
        var ex = await Record.ExceptionAsync(() => _service.StartMonitoringAsync("TestAG"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task PollOnceAsync_NoConfig_ThrowsInvalidOperation()
    {
        _configService.Load().Returns(new AppConfiguration());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PollOnceAsync("NoSuchGroup"));
    }

    [Fact]
    public async Task StopMonitoringAsync_AfterStart_DoesNotThrow()
    {
        var config = CreateConfig("TestAG", "TestServer");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("TestAG");
        var ex = await Record.ExceptionAsync(() => _service.StopMonitoringAsync("TestAG"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task StartMonitoringAsync_UsesConfiguredGroupName()
    {
        var config = CreateConfig("MyAG", "Server1");
        _configService.Load().Returns(config);

        await _service.StartMonitoringAsync("MyAG");

        _configService.Received().Load();
    }

    [Fact]
    public async Task DisposeAsync_AfterStart_DoesNotThrow()
    {
        var config = CreateConfig("TestAG", "TestServer");
        _configService.Load().Returns(config);
        await _service.StartMonitoringAsync("TestAG");

        var ex = await Record.ExceptionAsync(async () => await _service.DisposeAsync());
        Assert.Null(ex);
    }

    private static AppConfiguration CreateConfig(string groupName, string server)
    {
        return new AppConfiguration
        {
            MonitoredGroups = new List<MonitoredGroupConfig>
            {
                new MonitoredGroupConfig
                {
                    Name = groupName,
                    GroupType = "AvailabilityGroup",
                    PollingIntervalSeconds = 5,
                    Connections = new List<ConnectionConfig>
                    {
                        new ConnectionConfig
                        {
                            Server = server,
                            AuthType = "windows"
                        }
                    }
                }
            }
        };
    }
}
