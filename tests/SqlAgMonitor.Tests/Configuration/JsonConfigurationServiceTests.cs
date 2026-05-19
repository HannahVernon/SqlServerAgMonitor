using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;

namespace SqlAgMonitor.Tests.Configuration;

public sealed class JsonConfigurationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly ILogger<JsonConfigurationService> _logger;

    public JsonConfigurationServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "SqlAgMonitorTests_" + Guid.NewGuid());
        _logger = Substitute.For<ILogger<JsonConfigurationService>>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private JsonConfigurationService CreateService(string? subDir = null)
    {
        var dir = subDir != null ? Path.Combine(_testDirectory, subDir) : _testDirectory;
        return new JsonConfigurationService(_logger, dir);
    }

    #region Load Defaults

    [Fact]
    public void Load_WhenNoFileExists_ReturnsDefaults()
    {
        var service = CreateService();

        var config = service.Load();

        Assert.NotNull(config);
        Assert.Equal(16, config.GlobalPollingIntervalSeconds);
        Assert.Equal("dark", config.Theme);
        Assert.False(config.Email.Enabled);
        Assert.False(config.Syslog.Enabled);
    }

    [Fact]
    public void Load_WhenNoFileExists_CreatesDefaultFile()
    {
        var service = CreateService();

        service.Load();

        Assert.True(File.Exists(service.ConfigFilePath));
    }

    #endregion

    #region Save and Roundtrip

    [Fact]
    public void Save_ThenLoad_RoundtripsCorrectly()
    {
        var service = CreateService();
        var config = new AppConfiguration
        {
            GlobalPollingIntervalSeconds = 30,
            Theme = "light"
        };

        service.Save(config);
        var loaded = service.Load();

        Assert.Equal(30, loaded.GlobalPollingIntervalSeconds);
        Assert.Equal("light", loaded.Theme);
    }

    [Fact]
    public void Load_PreservesAllSettings()
    {
        var service = CreateService();
        var config = new AppConfiguration
        {
            GlobalPollingIntervalSeconds = 42,
            Theme = "light",
            LogLevel = "Debug",
            Email = new EmailSettings
            {
                Enabled = true,
                SmtpServer = "smtp.example.com",
                SmtpPort = 465,
                UseTls = true,
                FromAddress = "alerts@example.com",
                ToAddresses = new List<string> { "admin@example.com" }
            },
            Syslog = new SyslogSettings
            {
                Enabled = true,
                Server = "syslog.example.com",
                Port = 1514,
                Protocol = "TCP",
                Facility = "local7"
            }
        };

        service.Save(config);

        // Reload via a fresh service instance to ensure file-based roundtrip
        var freshService = CreateService();
        var loaded = freshService.Load();

        Assert.Equal(42, loaded.GlobalPollingIntervalSeconds);
        Assert.Equal("light", loaded.Theme);
        Assert.Equal("Debug", loaded.LogLevel);
        Assert.True(loaded.Email.Enabled);
        Assert.Equal("smtp.example.com", loaded.Email.SmtpServer);
        Assert.Equal(465, loaded.Email.SmtpPort);
        Assert.Equal("alerts@example.com", loaded.Email.FromAddress);
        Assert.Single(loaded.Email.ToAddresses);
        Assert.True(loaded.Syslog.Enabled);
        Assert.Equal("syslog.example.com", loaded.Syslog.Server);
        Assert.Equal(1514, loaded.Syslog.Port);
        Assert.Equal("TCP", loaded.Syslog.Protocol);
        Assert.Equal("local7", loaded.Syslog.Facility);
    }

    #endregion

    #region Events

    [Fact]
    public void Save_RaisesConfigurationChangedEvent()
    {
        var service = CreateService();
        AppConfiguration? receivedConfig = null;
        service.ConfigurationChanged += cfg => receivedConfig = cfg;
        var config = new AppConfiguration { Theme = "high-contrast" };

        service.Save(config);

        Assert.NotNull(receivedConfig);
        Assert.Equal("high-contrast", receivedConfig!.Theme);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Load_WithCorruptJson_ReturnsDefaults()
    {
        var service = CreateService();
        // Write invalid JSON directly to the config file
        Directory.CreateDirectory(Path.GetDirectoryName(service.ConfigFilePath)!);
        File.WriteAllText(service.ConfigFilePath, "{ this is not valid json!!!");

        var config = service.Load();

        Assert.NotNull(config);
        Assert.Equal(16, config.GlobalPollingIntervalSeconds);
    }

    #endregion

    #region Directory and Path

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        var nestedDir = Path.Combine(_testDirectory, "nested", "deep");
        var service = new JsonConfigurationService(_logger, nestedDir);

        service.Save(new AppConfiguration());

        Assert.True(File.Exists(service.ConfigFilePath));
    }

    [Fact]
    public void ConfigFilePath_ReturnsExpectedPath()
    {
        var service = CreateService();

        Assert.Equal(Path.Combine(_testDirectory, "config.json"), service.ConfigFilePath);
    }

    #endregion
}
