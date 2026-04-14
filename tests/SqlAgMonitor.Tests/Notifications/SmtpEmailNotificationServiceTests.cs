using Microsoft.Extensions.Logging;
using NSubstitute;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.Tests.Notifications;

public sealed class SmtpEmailNotificationServiceTests
{
    private readonly IConfigurationService _configService;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationServiceTests()
    {
        _configService = Substitute.For<IConfigurationService>();
        _credentialStore = Substitute.For<ICredentialStore>();
        _logger = Substitute.For<ILogger<SmtpEmailNotificationService>>();
    }

    private SmtpEmailNotificationService CreateService() =>
        new(_configService, _credentialStore, _logger);

    private static EmailSettings ValidSettings() => new()
    {
        Enabled = true,
        SmtpServer = "smtp.example.com",
        SmtpPort = 587,
        UseTls = true,
        FromAddress = "monitor@example.com",
        ToAddresses = new List<string> { "admin@example.com" },
        Username = "smtpuser",
        CredentialKey = "smtp-password"
    };

    [Fact]
    public async Task TestConnectionAsync_MissingSmtpServer_ReturnsFalse()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings
            {
                SmtpServer = "",
                FromAddress = "test@example.com",
                ToAddresses = new List<string> { "admin@example.com" }
            }
        };
        _configService.Load().Returns(config);
        var service = CreateService();

        var result = await service.TestConnectionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_MissingFromAddress_ReturnsFalse()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings
            {
                SmtpServer = "smtp.example.com",
                FromAddress = "",
                ToAddresses = new List<string> { "admin@example.com" }
            }
        };
        _configService.Load().Returns(config);
        var service = CreateService();

        var result = await service.TestConnectionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_NoRecipients_ReturnsFalse()
    {
        var config = new AppConfiguration
        {
            Email = new EmailSettings
            {
                SmtpServer = "smtp.example.com",
                FromAddress = "test@example.com",
                ToAddresses = new List<string>()
            }
        };
        _configService.Load().Returns(config);
        var service = CreateService();

        var result = await service.TestConnectionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_WithCredentialKey_RetrievesPassword()
    {
        var settings = ValidSettings();
        var config = new AppConfiguration { Email = settings };
        _configService.Load().Returns(config);
        _credentialStore.GetPasswordAsync("smtp-password", Arg.Any<CancellationToken>())
            .Returns("secret123");

        var service = CreateService();

        /* The actual SMTP send will fail (no real server), but we can verify
           the credential store was consulted before the connection attempt. */
        await service.TestConnectionAsync();

        await _credentialStore.Received(1)
            .GetPasswordAsync("smtp-password", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnectionAsync_NoCredentialKey_SkipsCredentialLookup()
    {
        var settings = ValidSettings();
        settings.CredentialKey = null;
        var config = new AppConfiguration { Email = settings };
        _configService.Load().Returns(config);

        var service = CreateService();
        await service.TestConnectionAsync();

        await _credentialStore.DidNotReceive()
            .GetPasswordAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestConnectionAsync_EmptyCredentialKey_SkipsCredentialLookup()
    {
        var settings = ValidSettings();
        settings.CredentialKey = "";
        var config = new AppConfiguration { Email = settings };
        _configService.Load().Returns(config);

        var service = CreateService();
        await service.TestConnectionAsync();

        await _credentialStore.DidNotReceive()
            .GetPasswordAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
