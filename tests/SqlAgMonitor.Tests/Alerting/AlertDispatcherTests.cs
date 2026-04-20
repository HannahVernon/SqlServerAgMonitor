using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Alerting;
using SqlAgMonitor.Core.Services.History;
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.Tests.Alerting;

public sealed class AlertDispatcherTests
{
    private readonly IEventRecorder _eventRecorder;
    private readonly IEmailNotificationService _emailService;
    private readonly ISyslogService _syslogService;
    private readonly IConfigurationService _configService;
    private readonly ILogger<AlertDispatcher> _logger;
    private readonly AlertDispatcher _dispatcher;

    public AlertDispatcherTests()
    {
        _eventRecorder = Substitute.For<IEventRecorder>();
        _emailService = Substitute.For<IEmailNotificationService>();
        _syslogService = Substitute.For<ISyslogService>();
        _configService = Substitute.For<IConfigurationService>();
        _logger = Substitute.For<ILogger<AlertDispatcher>>();

        _configService.Load().Returns(new AppConfiguration());

        _dispatcher = new AlertDispatcher(
            _eventRecorder, _emailService, _syslogService, _configService, _logger);
    }

    private static AlertEvent CreateTestAlert() => new()
    {
        Id = 1,
        Timestamp = DateTimeOffset.UtcNow,
        AlertType = AlertType.HealthDegraded,
        GroupName = "TestAG",
        Message = "Test alert message",
        Severity = AlertSeverity.Warning
    };

    #region Event Recording

    [Fact]
    public async Task Dispatch_AlwaysRecordsEvent()
    {
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _eventRecorder.Received(1).RecordEventAsync(alert, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Email Dispatch

    [Fact]
    public async Task Dispatch_WhenEmailEnabled_SendsEmail()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Email = new EmailSettings { Enabled = true }
        });
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _emailService.Received(1).SendAlertEmailAsync(alert, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatch_WhenEmailDisabled_DoesNotSendEmail()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Email = new EmailSettings { Enabled = false }
        });
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _emailService.DidNotReceive().SendAlertEmailAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Syslog Dispatch

    [Fact]
    public async Task Dispatch_WhenSyslogEnabled_SendsSyslog()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Syslog = new SyslogSettings { Enabled = true }
        });
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _syslogService.Received(1).SendEventAsync(alert, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatch_WhenSyslogDisabled_DoesNotSendSyslog()
    {
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _syslogService.DidNotReceive().SendEventAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Both Channels

    [Fact]
    public async Task Dispatch_WhenBothEnabled_SendsBoth()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Email = new EmailSettings { Enabled = true },
            Syslog = new SyslogSettings { Enabled = true }
        });
        var alert = CreateTestAlert();

        _dispatcher.Dispatch(alert);
        await Task.Delay(100);

        await _emailService.Received(1).SendAlertEmailAsync(alert, Arg.Any<CancellationToken>());
        await _syslogService.Received(1).SendEventAsync(alert, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Error Handling (Fire-and-Forget)

    [Fact]
    public async Task Dispatch_WhenEmailThrows_DoesNotThrow()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Email = new EmailSettings { Enabled = true }
        });
        _emailService.SendAlertEmailAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP failure"));
        var alert = CreateTestAlert();

        var exception = Record.Exception(() => _dispatcher.Dispatch(alert));
        await Task.Delay(100);

        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispatch_WhenSyslogThrows_DoesNotThrow()
    {
        _configService.Load().Returns(new AppConfiguration
        {
            Syslog = new SyslogSettings { Enabled = true }
        });
        _syslogService.SendEventAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Syslog failure"));
        var alert = CreateTestAlert();

        var exception = Record.Exception(() => _dispatcher.Dispatch(alert));
        await Task.Delay(100);

        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispatch_WhenEventRecorderThrows_DoesNotThrow()
    {
        _eventRecorder.RecordEventAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB failure"));
        var alert = CreateTestAlert();

        var exception = Record.Exception(() => _dispatcher.Dispatch(alert));
        await Task.Delay(100);

        Assert.Null(exception);
    }

    [Fact]
    public void Dispatch_WhenConfigThrows_DoesNotThrow()
    {
        _configService.Load().Throws(new InvalidOperationException("Config corrupted"));
        var alert = CreateTestAlert();

        var exception = Record.Exception(() => _dispatcher.Dispatch(alert));

        Assert.Null(exception);
    }

    #endregion
}
