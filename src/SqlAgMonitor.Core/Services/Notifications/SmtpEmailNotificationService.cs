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

    public async Task<string?> TestConnectionAsync(CancellationToken cancellationToken = default)
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
                Body = $"<p>This is a test email from <strong>SqlAgMonitor</strong> on <strong>{WebUtility.HtmlEncode(Environment.MachineName)}</strong>. SMTP connectivity is working.</p>",
                IsBodyHtml = true
            };

            foreach (var to in settings.ToAddresses)
                message.To.Add(to);

            await client.SendMailAsync(message, cancellationToken);

            _logger.LogInformation("SMTP test email sent successfully.");
            return null;
        }
        catch (SmtpFailedRecipientsException ex)
        {
            _logger.LogError(ex, "SMTP connection test failed.");
            return TranslateSmtpError(ex);
        }
        catch (SmtpFailedRecipientException ex)
        {
            _logger.LogError(ex, "SMTP connection test failed.");
            return TranslateSmtpError(ex);
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "SMTP connection test failed.");
            return TranslateSmtpError(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP connection test failed.");
            return ex.InnerException?.Message ?? ex.Message;
        }
    }

    private static string TranslateSmtpError(SmtpException ex)
    {
        var serverResponse = ex.Message;

        // Extract the inner exception's message for wrapped errors
        if (ex is SmtpFailedRecipientsException multi && multi.InnerExceptions.Length > 0)
            serverResponse = multi.InnerExceptions[0].Message;

        var responseLower = serverResponse.ToLowerInvariant();

        if (responseLower.Contains("authenticate") || responseLower.Contains("authentication")
            || responseLower.Contains("incorrect authentication")
            || ex.StatusCode == SmtpStatusCode.ClientNotPermitted)
        {
            return "Authentication failed — check your SMTP username and password.";
        }

        if (responseLower.Contains("relay") && responseLower.Contains("denied"))
            return "Relay denied — the server requires authentication. Check your username and password.";

        return ex.StatusCode switch
        {
            SmtpStatusCode.MustIssueStartTlsFirst =>
                "The server requires TLS. Enable the 'Use TLS' option and try again.",
            SmtpStatusCode.MailboxUnavailable =>
                $"Mailbox unavailable — {serverResponse}",
            SmtpStatusCode.MailboxBusy =>
                "The server is busy. Please try again later.",
            SmtpStatusCode.InsufficientStorage =>
                "The server reported insufficient storage.",
            SmtpStatusCode.ServiceNotAvailable =>
                "The SMTP service is not available. Check the server address and port.",
            _ => serverResponse
        };
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
                if (!settings.UseTls)
                {
                    _logger.LogWarning(
                        "SMTP credentials are configured but TLS is disabled — credentials will be sent in plain text. "
                        + "Enable UseTls in email settings to protect credentials in transit.");
                }

                _logger.LogDebug(
                    "SMTP authenticating (TLS={UseTls}, port={Port}).",
                    settings.UseTls, settings.SmtpPort);
                client.UseDefaultCredentials = false;
                client.Credentials = new NetworkCredential(settings.Username, password);
            }
            else
            {
                _logger.LogWarning(
                    "SMTP credential key '{CredentialKey}' exists but no password was found in the credential store.",
                    settings.CredentialKey);
            }
        }
        else
        {
            _logger.LogWarning("SMTP sending without authentication (no credential key configured).");
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
                    <tr>
                        <td style="padding: 6px 12px; font-weight: bold;">Monitor Host</td>
                        <td style="padding: 6px 12px;">{WebUtility.HtmlEncode(Environment.MachineName)}</td>
                    </tr>
                </table>
                <hr style="margin-top: 20px;" />
                <p style="font-size: 0.85em; color: #888;">Sent by SqlAgMonitor on {WebUtility.HtmlEncode(Environment.MachineName)}</p>
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
