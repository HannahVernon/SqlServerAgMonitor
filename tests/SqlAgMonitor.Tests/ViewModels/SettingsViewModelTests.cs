using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    private readonly IConfigurationService _configService;
    private readonly IEmailNotificationService _emailService;
    private readonly ICredentialStore _credentialStore;

    public SettingsViewModelTests()
    {
        _configService = Substitute.For<IConfigurationService>();
        _emailService = Substitute.For<IEmailNotificationService>();
        _credentialStore = Substitute.For<ICredentialStore>();

        _configService.Load().Returns(new AppConfiguration());
    }

    private SettingsViewModel CreateVm() => new(_configService, _emailService, _credentialStore);

    [Fact]
    public void LoadFrom_PopulatesGlobalPollingIntervalSeconds()
    {
        var config = new AppConfiguration { GlobalPollingIntervalSeconds = 30 };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.Equal(30, vm.GlobalPollingIntervalSeconds);
    }

    [Fact]
    public void LoadFrom_PopulatesTheme()
    {
        var config = new AppConfiguration { Theme = "light" };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.Equal("Light", vm.Theme);
    }

    [Fact]
    public void SaveSettings_WritesCorrectPollingInterval()
    {
        var vm = CreateVm();
        vm.GlobalPollingIntervalSeconds = 45;

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.Equal(45, config.GlobalPollingIntervalSeconds);
    }

    [Fact]
    public void SaveSettings_WritesSyslogSettings()
    {
        var vm = CreateVm();
        vm.SyslogEnabled = true;
        vm.SyslogServer = "syslog.example.com";
        vm.SyslogPort = 1514;
        vm.SyslogProtocol = "TCP";

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.True(config.Syslog.Enabled);
        Assert.Equal("syslog.example.com", config.Syslog.Server);
        Assert.Equal(1514, config.Syslog.Port);
        Assert.Equal("TCP", config.Syslog.Protocol);
    }

    [Fact]
    public void SaveSettings_WritesExportSettings()
    {
        var vm = CreateVm();
        vm.ExportEnabled = true;
        vm.ExportPath = @"C:\Reports";
        vm.ExportIntervalMinutes = 120;

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.True(config.Export.Enabled);
        Assert.Equal(@"C:\Reports", config.Export.ExportPath);
        Assert.Equal(120, config.Export.ScheduleIntervalMinutes);
    }

    [Fact]
    public void SaveSettings_WritesHistorySettings()
    {
        var vm = CreateVm();
        vm.AutoPruneEnabled = true;
        vm.MaxRetentionDays = 60;
        vm.MaxRecords = 5000;

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.True(config.History.AutoPruneEnabled);
        Assert.Equal(60, config.History.MaxRetentionDays);
        Assert.Equal(5000, config.History.MaxRecords);
    }

    [Fact]
    public void LoadFrom_PopulatesSyslogSettings()
    {
        var config = new AppConfiguration
        {
            Syslog = new SyslogSettings
            {
                Enabled = true,
                Server = "log.example.com",
                Port = 1514,
                Protocol = "TCP"
            }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.True(vm.SyslogEnabled);
        Assert.Equal("log.example.com", vm.SyslogServer);
        Assert.Equal(1514, vm.SyslogPort);
        Assert.Equal("TCP", vm.SyslogProtocol);
    }

    [Fact]
    public void LoadFrom_PopulatesExportSettings()
    {
        var config = new AppConfiguration
        {
            Export = new ExportSettings
            {
                Enabled = true,
                ExportPath = @"C:\Export",
                ScheduleIntervalMinutes = 30
            }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.True(vm.ExportEnabled);
        Assert.Equal(@"C:\Export", vm.ExportPath);
        Assert.Equal(30, vm.ExportIntervalMinutes);
    }

    [Fact]
    public void LoadFrom_PopulatesServiceSettings()
    {
        var config = new AppConfiguration
        {
            Service = new ServiceSettings
            {
                Enabled = true,
                Host = "remote.example.com",
                Port = 9090
            }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.True(vm.ServiceEnabled);
        Assert.Equal("remote.example.com", vm.ServiceHost);
        Assert.Equal(9090, vm.ServicePort);
    }

    [Fact]
    public void DefaultValues_WhenConfigHasNoOverrides()
    {
        var config = new AppConfiguration();

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.Equal(16, vm.GlobalPollingIntervalSeconds);
        Assert.Equal("Dark", vm.Theme);
        Assert.False(vm.SyslogEnabled);
        Assert.False(vm.ExportEnabled);
        Assert.False(vm.ServiceEnabled);
        Assert.Equal(5, vm.MasterCooldownMinutes);
    }

    [Fact]
    public void MasterCooldownMinutes_PersistsThroughSaveLoad()
    {
        var vm = CreateVm();
        vm.MasterCooldownMinutes = 15;

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.Equal(15, config.Alerts.MasterCooldownMinutes);

        var vm2 = CreateVm();
        vm2.LoadFrom(config);

        Assert.Equal(15, vm2.MasterCooldownMinutes);
    }

    [Fact]
    public void LogLevel_PersistsThroughSaveLoad()
    {
        var vm = CreateVm();
        vm.LogLevel = "Warning";

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.Equal("Warning", config.LogLevel);

        var vm2 = CreateVm();
        vm2.LoadFrom(config);

        Assert.Equal("Warning", vm2.LogLevel);
    }
}
