using System.Reactive.Linq;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.ViewModels;

namespace SqlAgMonitor.Tests.ViewModels;

public sealed class SettingsViewModelSmtpTests
{
    private readonly IConfigurationService _configService;
    private readonly IEmailNotificationService _emailService;
    private readonly ICredentialStore _credentialStore;

    public SettingsViewModelSmtpTests()
    {
        _configService = Substitute.For<IConfigurationService>();
        _emailService = Substitute.For<IEmailNotificationService>();
        _credentialStore = Substitute.For<ICredentialStore>();

        _configService.Load().Returns(new AppConfiguration());
    }

    private SettingsViewModel CreateVm() => new(_configService, _emailService, _credentialStore);

    [Fact]
    public void LoadFrom_SetsHasStoredSmtpPassword_WhenCredentialKeyExists()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings { CredentialKey = "smtp-password" }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.True(vm.HasStoredSmtpPassword);
    }

    [Fact]
    public void LoadFrom_ClearsHasStoredSmtpPassword_WhenNoCredentialKey()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings { CredentialKey = null }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.False(vm.HasStoredSmtpPassword);
    }

    [Fact]
    public void LoadFrom_SetsEmailUsername()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings { Username = "smtpuser" }
        };

        var vm = CreateVm();
        vm.LoadFrom(config);

        Assert.Equal("smtpuser", vm.EmailUsername);
    }

    [Fact]
    public void ApplyTo_SetsEmailSettingsOnConfig()
    {
        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.SmtpPort = 465;
        vm.UseTls = true;
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "a@test.com; b@test.com";
        vm.EmailUsername = "user1";

        var config = new AppConfiguration();
        vm.ApplyTo(config);

        Assert.Equal("smtp.test.com", config.Email.SmtpServer);
        Assert.Equal(465, config.Email.SmtpPort);
        Assert.True(config.Email.UseTls);
        Assert.Equal("from@test.com", config.Email.FromAddress);
        Assert.Equal(new List<string> { "a@test.com", "b@test.com" }, config.Email.ToAddresses);
        Assert.Equal("user1", config.Email.Username);
    }

    [Fact]
    public async Task TestEmail_StoresPasswordInCredentialStore_BeforeTesting()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";
        vm.EmailUsername = "user";
        vm.EmailPassword = "secret";

        await vm.TestEmailCommand.Execute();

        /* Password should be stored before the test email is sent */
        Received.InOrder(() =>
        {
            _credentialStore.StorePasswordAsync("smtp-password", "secret", Arg.Any<CancellationToken>());
            _emailService.TestConnectionAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task TestEmail_SetsCredentialKeyOnConfig_WhenPasswordProvided()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";
        vm.EmailPassword = "secret";

        await vm.TestEmailCommand.Execute();

        /* Verify the config was saved with CredentialKey set */
        _configService.Received().Save(Arg.Is<AppConfiguration>(c =>
            c.Email.CredentialKey == "smtp-password"));
    }

    [Fact]
    public async Task TestEmail_SkipsCredentialStore_WhenNoPassword()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";
        vm.EmailPassword = "";

        await vm.TestEmailCommand.Execute();

        await _credentialStore.DidNotReceive()
            .StorePasswordAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestEmail_SetsSuccessStatus_OnSuccess()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";

        await vm.TestEmailCommand.Execute();

        Assert.Contains("✓", vm.TestEmailStatus);
    }

    [Fact]
    public async Task TestEmail_SetsFailureStatus_OnFailure()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";

        await vm.TestEmailCommand.Execute();

        Assert.Contains("✗", vm.TestEmailStatus);
    }

    [Fact]
    public async Task TestEmail_SetsErrorStatus_OnException()
    {
        _emailService.TestConnectionAsync(Arg.Any<CancellationToken>())
            .Returns<bool>(x => throw new InvalidOperationException("SMTP server is not configured."));

        var vm = CreateVm();
        vm.SmtpServer = "smtp.test.com";
        vm.FromAddress = "from@test.com";
        vm.ToAddresses = "to@test.com";

        await vm.TestEmailCommand.Execute();

        Assert.Contains("✗", vm.TestEmailStatus);
        Assert.Contains("SMTP server is not configured", vm.TestEmailStatus);
    }
}
