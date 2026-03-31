using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Models;
using SqlAgMonitor.Core.Services.Credentials;

namespace SqlAgMonitor.Core.Services.Notifications;

public class SmtpEmailNotificationService : IEmailNotificationService
{
    private readonly IConfigurationService _configService;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<SmtpEmailNotificationService> _logger;

    public SmtpEmailNotificationService(
        IConfigurationService configService,
        ICredentialStore credentialStore,
        ILogger<SmtpEmailNotificationService> logger)
    {
        _configService = configService;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task SendAlertEmailAsync(AlertEvent alert, CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configService.Load().Email;
            ValidateSettings(settings);

            using var client = await CreateSmtpClientAsync(settings, cancellationToken);
            using var message = BuildMessage(alert, settings);

            _logger.LogInformation(
                "Sending alert email for {AlertType} on '{GroupName}' to {RecipientCount} recipient(s).",
                alert.AlertType, alert.GroupName, settings.ToAddresses.Count);

            await client.SendMailAsync(message, cancellationToken);

            _logger.LogInformation("Alert email sent successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to send alert email for {AlertType} on '{GroupName}'.",
                alert.AlertType, alert.GroupName);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = _configService.Load().Email;
            ValidateSettings(settings);

            using var client = await CreateSmtpClientAsync(settings, cancellationToken);

            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress),
                Subject = "SqlAgMonitor — Test Email",
                Body = "<p>This is a test email from <strong>SqlAgMonitor</strong>. SMTP connectivity is working.</p>",
                IsBodyHtml = true
            };

            foreach (var to in settings.ToAddresses)
                message.To.Add(to);

            await client.SendMailAsync(message, cancellationToken);

            _logger.LogInformation("SMTP test email sent successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP connection test failed.");
            return false;
        }
    }

    private async Task<SmtpClient> CreateSmtpClientAsync(EmailSettings settings, CancellationToken cancellationToken)
    {
        var client = new SmtpClient(settings.SmtpServer, settings.SmtpPort)
        {
            EnableSsl = settings.UseTls,
            Timeout = 30_000 // 30 seconds — generous for TLS handshake + auth over WAN
        };

        if (!string.IsNullOrEmpty(settings.CredentialKey))
        {
            var password = await _credentialStore.GetPasswordAsync(settings.CredentialKey, cancellationToken);
            if (!string.IsNullOrEmpty(password))
            {
                client.Credentials = new NetworkCredential(settings.Username, password);
            }
        }

        return client;
    }

    private static MailMessage BuildMessage(AlertEvent alert, EmailSettings settings)
    {
        var severityLabel = alert.Severity.ToString().ToUpperInvariant();
        var subject = $"[{severityLabel}] SqlAgMonitor — {alert.AlertType} on {alert.GroupName}";

        var severityColor = alert.Severity switch
        {
            AlertSeverity.Critical => "#dc3545",
            AlertSeverity.Warning => "#ffc107",
            _ => "#17a2b8"
        };

        var body = $"""
            <html>
            <body style="font-family: Segoe UI, Arial, sans-serif; color: #333;">
                <h2 style="color: {severityColor};">{severityLabel}: {alert.AlertType}</h2>
                <table style="border-collapse: collapse; width: 100%; max-width: 600px;">
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Availability Group</td>
                        <td style="padding: 6px 12px;">{WebUtility.HtmlEncode(alert.GroupName)}</td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Replica</td>
                        <td style="padding: 6px 12px;">{WebUtility.HtmlEncode(alert.ReplicaName ?? "N/A")}</td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Database</td>
                        <td style="padding: 6px 12px;">{WebUtility.HtmlEncode(alert.DatabaseName ?? "N/A")}</td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Timestamp</td>
                        <td style="padding: 6px 12px;">{alert.Timestamp:yyyy-MM-dd HH:mm:ss zzz}</td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Message</td>
                        <td style="padding: 6px 12px;">{WebUtility.HtmlEncode(alert.Message)}</td>
                    </tr>
                </table>
                <hr style="margin-top: 20px;" />
                <p style="font-size: 0.85em; color: #888;">Sent by SqlAgMonitor</p>
            </body>
            </html>
            """;

        var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        foreach (var to in settings.ToAddresses)
            message.To.Add(to);

        return message;
    }

    private static void ValidateSettings(EmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpServer))
            throw new InvalidOperationException("SMTP server is not configured.");

        if (string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new InvalidOperationException("Email FromAddress is not configured.");

        if (settings.ToAddresses == null || settings.ToAddresses.Count == 0)
            throw new InvalidOperationException("No email recipients configured.");
    }
}
