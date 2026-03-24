using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Core.Services.Notifications;

public class SyslogService : ISyslogService
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<SyslogService> _logger;

    private static readonly Dictionary<string, int> FacilityCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kern"]     = 0,
        ["user"]     = 1,
        ["mail"]     = 2,
        ["daemon"]   = 3,
        ["auth"]     = 4,
        ["syslog"]   = 5,
        ["lpr"]      = 6,
        ["news"]     = 7,
        ["uucp"]     = 8,
        ["cron"]     = 9,
        ["local0"]   = 16,
        ["local1"]   = 17,
        ["local2"]   = 18,
        ["local3"]   = 19,
        ["local4"]   = 20,
        ["local5"]   = 21,
        ["local6"]   = 22,
        ["local7"]   = 23
    };

    private const string AppName = "SqlAgMonitor";
    private const string NilValue = "-";

    public SyslogService(
        IConfigurationService configService,
        ILogger<SyslogService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task SendEventAsync(AlertEvent alert, CancellationToken cancellationToken = default)
    {
        var settings = _configService.Load().Syslog;
        ValidateSettings(settings);

        var message = FormatRfc5424Message(alert, settings);
        await SendAsync(message, settings, cancellationToken);

        _logger.LogInformation(
            "Syslog event sent for {AlertType} on '{GroupName}'.",
            alert.AlertType, alert.GroupName);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configService.Load().Syslog;
            ValidateSettings(settings);

            var testAlert = new AlertEvent
            {
                Id = 0,
                Timestamp = DateTimeOffset.UtcNow,
                AlertType = AlertType.Unknown,
                GroupName = "test",
                Message = "SqlAgMonitor syslog connectivity test.",
                Severity = AlertSeverity.Information
            };

            var message = FormatRfc5424Message(testAlert, settings);
            await SendAsync(message, settings, cancellationToken);

            _logger.LogInformation("Syslog connection test succeeded.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Syslog connection test failed.");
            return false;
        }
    }

    private static string FormatRfc5424Message(AlertEvent alert, SyslogSettings settings)
    {
        var facility = GetFacilityCode(settings.Facility);
        var severity = MapSeverity(alert.Severity);
        var priority = facility * 8 + severity;

        var timestamp = alert.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);
        var hostname = Environment.MachineName;
        var procId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var msgId = alert.AlertType.ToString();

        var msg = new StringBuilder();
        msg.Append($"AG={alert.GroupName}");
        if (!string.IsNullOrEmpty(alert.ReplicaName))
            msg.Append($" Replica={alert.ReplicaName}");
        if (!string.IsNullOrEmpty(alert.DatabaseName))
            msg.Append($" Database={alert.DatabaseName}");
        msg.Append($" {alert.Message}");

        // RFC 5424: <priority>VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP STRUCTURED-DATA SP MSG
        return $"<{priority}>1 {timestamp} {hostname} {AppName} {procId} {msgId} {NilValue} {msg}";
    }

    private async Task SendAsync(string message, SyslogSettings settings, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(message);

        if (string.Equals(settings.Protocol, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            await SendTcpAsync(data, settings.Server, settings.Port, cancellationToken);
        }
        else
        {
            await SendUdpAsync(data, settings.Server, settings.Port, cancellationToken);
        }
    }

    private static async Task SendUdpAsync(byte[] data, string server, int port, CancellationToken cancellationToken)
    {
        using var client = new UdpClient();
        client.Connect(server, port);
        await client.SendAsync(data, cancellationToken);
    }

    private static async Task SendTcpAsync(byte[] data, string server, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(server, port, cancellationToken);
        var stream = client.GetStream();
        // RFC 5425 octet-counting framing: length SP message
        var frame = Encoding.UTF8.GetBytes($"{data.Length} ");
        await stream.WriteAsync(frame, cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static int GetFacilityCode(string facility)
    {
        if (FacilityCodes.TryGetValue(facility, out var code))
            return code;

        return 16; // default to local0
    }

    private static int MapSeverity(AlertSeverity severity) =>
        severity switch
        {
            AlertSeverity.Critical    => 2, // RFC 5424 Critical
            AlertSeverity.Warning     => 4, // RFC 5424 Warning
            AlertSeverity.Information => 6, // RFC 5424 Informational
            _                         => 6
        };

    private static void ValidateSettings(SyslogSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Server))
            throw new InvalidOperationException("Syslog server is not configured.");
    }
}
