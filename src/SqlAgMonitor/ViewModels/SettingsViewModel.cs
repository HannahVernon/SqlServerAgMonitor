using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Reactive;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SqlAgMonitor.Core.Configuration;
using SqlAgMonitor.Core.Services.Credentials;
using SqlAgMonitor.Core.Services.Notifications;
using SqlAgMonitor.Services;

namespace SqlAgMonitor.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;
    private readonly IEmailNotificationService _emailService;
    private readonly ICredentialStore _credentialStore;

    private int _globalPollingIntervalSeconds;
    private string _theme = "dark";
    private string _logLevel = "Information";

    // Email
    private bool _emailEnabled;
    private string _smtpServer = string.Empty;
    private int _smtpPort = 587;
    private bool _useTls = true;
    private string _fromAddress = string.Empty;
    private string _toAddresses = string.Empty;
    private string? _emailUsername;
    private string _emailPassword = string.Empty;
    private bool _hasStoredSmtpPassword;

    // Syslog
    private bool _syslogEnabled;
    private string _syslogServer = string.Empty;
    private int _syslogPort = 514;
    private string _syslogProtocol = "UDP";

    // Alerts
    private int _masterCooldownMinutes = 5;

    // Export
    private bool _exportEnabled;
    private string _exportPath = string.Empty;
    private int _exportIntervalMinutes = 60;

    // History
    private bool _autoPruneEnabled = true;
    private int _maxRetentionDays = 90;
    private int _maxRecords;

    // Service
    private bool _serviceEnabled;
    private bool _serviceWasPreviouslyEnabled;
    private string _serviceHost = "localhost";
    private int _servicePort = 58432;
    private string? _serviceUsername;
    private string _servicePassword = string.Empty;
    private bool _serviceUseTls;
    private string? _originalServiceHost;
    private int _originalServicePort;

    // UI feedback
    private string? _testEmailStatus;
    private string? _testConnectionStatus;

    public List<string> ThemeOptions { get; } = new() { "Light", "Dark", "High Contrast" };
    public List<string> SyslogProtocolOptions { get; } = new() { "UDP", "TCP" };
    public List<string> LogLevelOptions { get; } = new() { "Debug", "Information", "Warning", "Error" };

    public int GlobalPollingIntervalSeconds
    {
        get => _globalPollingIntervalSeconds;
        set => this.RaiseAndSetIfChanged(ref _globalPollingIntervalSeconds, value);
    }

    public string Theme
    {
        get => _theme;
        set => this.RaiseAndSetIfChanged(ref _theme, value);
    }

    public string LogLevel
    {
        get => _logLevel;
        set => this.RaiseAndSetIfChanged(ref _logLevel, value);
    }

    // Email settings
    public bool EmailEnabled { get => _emailEnabled; set => this.RaiseAndSetIfChanged(ref _emailEnabled, value); }
    public string SmtpServer { get => _smtpServer; set => this.RaiseAndSetIfChanged(ref _smtpServer, value); }
    public int SmtpPort { get => _smtpPort; set => this.RaiseAndSetIfChanged(ref _smtpPort, value); }
    public bool UseTls { get => _useTls; set => this.RaiseAndSetIfChanged(ref _useTls, value); }
    public string FromAddress { get => _fromAddress; set => this.RaiseAndSetIfChanged(ref _fromAddress, value); }
    public string ToAddresses { get => _toAddresses; set => this.RaiseAndSetIfChanged(ref _toAddresses, value); }
    public string? EmailUsername { get => _emailUsername; set => this.RaiseAndSetIfChanged(ref _emailUsername, value); }
    public string EmailPassword { get => _emailPassword; set => this.RaiseAndSetIfChanged(ref _emailPassword, value); }
    public bool HasStoredSmtpPassword { get => _hasStoredSmtpPassword; set => this.RaiseAndSetIfChanged(ref _hasStoredSmtpPassword, value); }

    // Syslog settings
    public bool SyslogEnabled { get => _syslogEnabled; set => this.RaiseAndSetIfChanged(ref _syslogEnabled, value); }
    public string SyslogServer { get => _syslogServer; set => this.RaiseAndSetIfChanged(ref _syslogServer, value); }
    public int SyslogPort { get => _syslogPort; set => this.RaiseAndSetIfChanged(ref _syslogPort, value); }
    public string SyslogProtocol { get => _syslogProtocol; set => this.RaiseAndSetIfChanged(ref _syslogProtocol, value); }

    // Alert settings
    public int MasterCooldownMinutes { get => _masterCooldownMinutes; set => this.RaiseAndSetIfChanged(ref _masterCooldownMinutes, value); }

    // Export settings
    public bool ExportEnabled { get => _exportEnabled; set => this.RaiseAndSetIfChanged(ref _exportEnabled, value); }
    public string ExportPath { get => _exportPath; set => this.RaiseAndSetIfChanged(ref _exportPath, value); }
    public int ExportIntervalMinutes { get => _exportIntervalMinutes; set => this.RaiseAndSetIfChanged(ref _exportIntervalMinutes, value); }

    // History settings
    public bool AutoPruneEnabled { get => _autoPruneEnabled; set => this.RaiseAndSetIfChanged(ref _autoPruneEnabled, value); }
    public int MaxRetentionDays { get => _maxRetentionDays; set => this.RaiseAndSetIfChanged(ref _maxRetentionDays, value); }
    public int MaxRecords { get => _maxRecords; set => this.RaiseAndSetIfChanged(ref _maxRecords, value); }

    // Service settings
    public bool ServiceEnabled { get => _serviceEnabled; set => this.RaiseAndSetIfChanged(ref _serviceEnabled, value); }
    public string ServiceHost
    {
        get => _serviceHost;
        set
        {
            this.RaiseAndSetIfChanged(ref _serviceHost, value);
            if (!string.Equals(value, _originalServiceHost, StringComparison.OrdinalIgnoreCase))
                AcceptedCertThumbprint = null;
        }
    }
    public int ServicePort
    {
        get => _servicePort;
        set
        {
            this.RaiseAndSetIfChanged(ref _servicePort, value);
            if (value != _originalServicePort)
                AcceptedCertThumbprint = null;
        }
    }
    public string? ServiceUsername { get => _serviceUsername; set => this.RaiseAndSetIfChanged(ref _serviceUsername, value); }
    public string ServicePassword { get => _servicePassword; set => this.RaiseAndSetIfChanged(ref _servicePassword, value); }
    public bool ServiceUseTls { get => _serviceUseTls; set => this.RaiseAndSetIfChanged(ref _serviceUseTls, value); }

    public string? TestEmailStatus{ get => _testEmailStatus; set => this.RaiseAndSetIfChanged(ref _testEmailStatus, value); }
    public string? TestConnectionStatus { get => _testConnectionStatus; set => this.RaiseAndSetIfChanged(ref _testConnectionStatus, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestEmailCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }

    /// <summary>Raised when the dialog should close. True = saved, False = cancelled.</summary>
    public Func<bool, Task>? CloseRequested;

    public SettingsViewModel(IConfigurationService configService, IEmailNotificationService emailService, ICredentialStore credentialStore)
    {
        _configService = configService;
        _emailService = emailService;
        _credentialStore = credentialStore;
        SaveCommand = ReactiveCommand.CreateFromTask(OnSaveAsync);
        CancelCommand = ReactiveCommand.CreateFromTask(OnCancelAsync);
        TestEmailCommand = ReactiveCommand.CreateFromTask(OnTestEmailAsync);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(OnTestConnectionAsync);

        // Prevent ReactiveCommand from faulting (permanently disabling)
        // when an unhandled exception escapes — log it and surface in UI.
        TestEmailCommand.ThrownExceptions.Subscribe(ex =>
            TestEmailStatus = $"✗ {ex.Message}");
        TestConnectionCommand.ThrownExceptions.Subscribe(ex =>
            TestConnectionStatus = $"✗ {ex.Message}");
        SaveCommand.ThrownExceptions.Subscribe(_ => { });
        CancelCommand.ThrownExceptions.Subscribe(_ => { });
    }

    private static readonly Dictionary<string, string> ThemeToDisplay = new(StringComparer.OrdinalIgnoreCase)
    {
        { "light", "Light" },
        { "dark", "Dark" },
        { "highContrast", "High Contrast" },
    };

    private static readonly Dictionary<string, string> DisplayToTheme = new()
    {
        { "Light", "light" },
        { "Dark", "dark" },
        { "High Contrast", "highContrast" },
    };

    public void LoadFrom(AppConfiguration config)
    {
        GlobalPollingIntervalSeconds = config.GlobalPollingIntervalSeconds;
        Theme = ThemeToDisplay.GetValueOrDefault(config.Theme, "Dark");
        LogLevel = LogLevelOptions.Contains(config.LogLevel) ? config.LogLevel : "Information";
        EmailEnabled = config.Email.Enabled;
        SmtpServer = config.Email.SmtpServer;
        SmtpPort = config.Email.SmtpPort;
        UseTls = config.Email.UseTls;
        FromAddress = config.Email.FromAddress;
        ToAddresses = string.Join("; ", config.Email.ToAddresses);
        EmailUsername = config.Email.Username;
        HasStoredSmtpPassword = !string.IsNullOrEmpty(config.Email.CredentialKey);
        SyslogEnabled = config.Syslog.Enabled;
        SyslogServer = config.Syslog.Server;
        SyslogPort = config.Syslog.Port;
        SyslogProtocol = config.Syslog.Protocol;
        MasterCooldownMinutes = config.Alerts.MasterCooldownMinutes;
        ExportEnabled = config.Export.Enabled;
        ExportPath = config.Export.ExportPath;
        ExportIntervalMinutes = config.Export.ScheduleIntervalMinutes;
        AutoPruneEnabled = config.History.AutoPruneEnabled;
        MaxRetentionDays = config.History.MaxRetentionDays ?? 0;
        MaxRecords = config.History.MaxRecords ?? 0;
        ServiceEnabled = config.Service.Enabled;
        _serviceWasPreviouslyEnabled = config.Service.Enabled;
        ServiceHost = config.Service.Host;
        ServicePort = config.Service.Port;
        _originalServiceHost = config.Service.Host;
        _originalServicePort = config.Service.Port;
        ServiceUsername = config.Service.Username;
        ServiceUseTls = config.Service.UseTls;
        AcceptedCertThumbprint = config.Service.TrustedCertThumbprint;
    }

    public void ApplyTo(AppConfiguration config)
    {
        config.GlobalPollingIntervalSeconds = GlobalPollingIntervalSeconds;
        config.Theme = DisplayToTheme.GetValueOrDefault(Theme, "dark");
        config.LogLevel = LogLevel;
        config.Email.Enabled = EmailEnabled;
        config.Email.SmtpServer = SmtpServer;
        config.Email.SmtpPort = SmtpPort;
        config.Email.UseTls = UseTls;
        config.Email.FromAddress = FromAddress;
        config.Email.ToAddresses = ToAddresses
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        config.Email.Username = EmailUsername;
        config.Syslog.Enabled = SyslogEnabled;
        config.Syslog.Server = SyslogServer;
        config.Syslog.Port = SyslogPort;
        config.Syslog.Protocol = SyslogProtocol;
        config.Alerts.MasterCooldownMinutes = MasterCooldownMinutes;
        config.Export.Enabled = ExportEnabled;
        config.Export.ExportPath = ExportPath;
        config.Export.ScheduleIntervalMinutes = ExportIntervalMinutes;
        config.History.AutoPruneEnabled = AutoPruneEnabled;
        config.History.MaxRetentionDays = MaxRetentionDays > 0 ? MaxRetentionDays : null;
        config.History.MaxRecords = MaxRecords > 0 ? MaxRecords : null;
        config.Service.Enabled = ServiceEnabled;
        config.Service.Host = ServiceHost;
        config.Service.Port = ServicePort;
        config.Service.Username = ServiceUsername;
        config.Service.UseTls = ServiceUseTls;
        config.Service.TrustedCertThumbprint = AcceptedCertThumbprint;
    }

    private async Task OnSaveAsync(CancellationToken cancellationToken)
    {
        if (CloseRequested != null)
            await CloseRequested(true);
    }

    private async Task OnCancelAsync(CancellationToken cancellationToken)
    {
        if (CloseRequested != null)
            await CloseRequested(false);
    }

    private async Task OnTestEmailAsync(CancellationToken cancellationToken)
    {
        TestEmailStatus = "Sending test email...";
        try
        {
            // Save current email settings first so the service reads them
            var config = _configService.Load();
            ApplyTo(config);

            // Store SMTP password in credential store before testing
            if (!string.IsNullOrEmpty(EmailPassword))
            {
                const string smtpCredentialKey = "smtp-password";
                config.Email.CredentialKey = smtpCredentialKey;
                await _credentialStore.StorePasswordAsync(smtpCredentialKey, EmailPassword, cancellationToken);
            }

            _configService.Save(config);

            var error = await _emailService.TestConnectionAsync(cancellationToken);
            TestEmailStatus = error == null ? "✓ Test email sent successfully." : $"✗ {error}";
        }
        catch (Exception ex)
        {
            TestEmailStatus = $"✗ {ex.Message}";
        }
    }

    /// <summary>
    /// Callback wired by the window to show a certificate trust dialog.
    /// Receives the untrusted X509Certificate2, returns true if the user accepts it.
    /// </summary>
    public Func<X509Certificate2, Task<bool>>? ConfirmUntrustedCertificate { get; set; }

    /// <summary>
    /// Callback wired by the window to offer config migration to the service.
    /// Receives the list of group names to migrate, returns true if the user confirms.
    /// </summary>
    public Func<List<string>, Task<bool>>? ConfirmMigration { get; set; }

    /// <summary>
    /// True when service mode was newly enabled (wasn't previously enabled) and local groups exist.
    /// </summary>
    public bool ShouldOfferMigration =>
        ServiceEnabled && (!_serviceWasPreviouslyEnabled || ServiceConnectionChanged);

    private bool ServiceConnectionChanged =>
        _serviceWasPreviouslyEnabled &&
        (!string.Equals(ServiceHost, _originalServiceHost, StringComparison.OrdinalIgnoreCase)
         || ServicePort != _originalServicePort);

    /// <summary>
    /// If the user accepted an untrusted cert during Test Connection, its thumbprint is stored
    /// here so it can be persisted to config on Save.
    /// </summary>
    public string? AcceptedCertThumbprint { get; private set; }

    private async Task OnTestConnectionAsync(CancellationToken cancellationToken)
    {
        TestConnectionStatus = "Connecting...";

        var scheme = ServiceUseTls ? "https" : "http";
        var port = Math.Clamp(ServicePort, 1, 65535);
        var baseUrl = $"{scheme}://{ServiceHost}:{port}";

        try
        {
            // Phase 1: if TLS, probe for cert issues
            string? pinnedThumbprint = AcceptedCertThumbprint;

            if (ServiceUseTls)
            {
                X509Certificate2? capturedCert = null;
                bool systemTrusted = false;

                using var probeHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    {
                        if (errors == SslPolicyErrors.None)
                        {
                            systemTrusted = true;
                            return true;
                        }
                        if (cert != null)
                            capturedCert = new X509Certificate2(cert);
                        return false;
                    }
                };

                using var probeClient = new HttpClient(probeHandler)
                {
                    BaseAddress = new Uri(baseUrl),
                    Timeout = TimeSpan.FromSeconds(10)
                };

                try
                {
                    await probeClient.GetAsync("/api/version", cancellationToken);
                }
                catch (HttpRequestException) when (capturedCert != null)
                {
                    // Certificate is not system-trusted — check if the pinned thumbprint still matches
                    if (pinnedThumbprint != null && string.Equals(
                            capturedCert.Thumbprint, pinnedThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        // Pinned thumbprint matches — proceed without prompting
                    }
                    else if (ConfirmUntrustedCertificate != null)
                    {
                        // Thumbprint changed or no pin — show the trust dialog
                        var accepted = await ConfirmUntrustedCertificate(capturedCert);
                        if (!accepted)
                        {
                            TestConnectionStatus = "✗ Certificate not trusted.";
                            return;
                        }
                        pinnedThumbprint = capturedCert.Thumbprint;
                        AcceptedCertThumbprint = pinnedThumbprint;
                    }
                    else
                    {
                        TestConnectionStatus = "✗ Server certificate is not trusted.";
                        return;
                    }
                }
                catch (HttpRequestException ex) when (capturedCert == null)
                {
                    TestConnectionStatus = $"✗ TLS handshake failed: {ex.InnerException?.Message ?? ex.Message}";
                    return;
                }

                if (systemTrusted)
                    pinnedThumbprint = null; // System-trusted cert — no need to pin
            }

            // Phase 2: check protocol version
            var svcSettings = new ServiceSettings
            {
                Host = ServiceHost,
                Port = port,
                UseTls = ServiceUseTls
            };
            var versionError = await ServiceMonitoringClient.CheckVersionAsync(
                svcSettings, pinnedThumbprint, cancellationToken);
            if (versionError != null)
            {
                TestConnectionStatus = $"✗ {versionError}";
                return;
            }

            // Phase 3: attempt login
            using var handler = new HttpClientHandler();
            if (pinnedThumbprint != null)
            {
                var pinned = pinnedThumbprint;
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(cert.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            if (string.IsNullOrWhiteSpace(ServiceUsername) || string.IsNullOrWhiteSpace(ServicePassword))
            {
                // No credentials — just check reachability
                var healthResponse = await client.GetAsync("/api/auth/login", cancellationToken);
                TestConnectionStatus = "✓ Service is reachable. Enter credentials to test authentication.";
                return;
            }

            var payload = JsonSerializer.Serialize(new { username = ServiceUsername, password = ServicePassword });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/auth/login", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                TestConnectionStatus = "✓ Connected and authenticated successfully.";
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                TestConnectionStatus = "✗ Authentication failed — check username and password.";
            }
            else
            {
                TestConnectionStatus = $"✗ Service returned {(int)response.StatusCode} {response.StatusCode}.";
            }
        }
        catch (TaskCanceledException)
        {
            TestConnectionStatus = "✗ Connection timed out.";
        }
        catch (HttpRequestException ex)
        {
            TestConnectionStatus = $"✗ {ex.InnerException?.Message ?? ex.Message}";
        }
        catch (Exception ex)
        {
            TestConnectionStatus = $"✗ {ex.Message}";
        }
    }

    /// <summary>
    /// Pushes local config (groups, alerts, email, syslog) to the remote service.
    /// Returns a human-readable result message.
    /// </summary>
    public Task<string> MigrateConfigToServiceAsync()
    {
        var config = _configService.Load();
        return MigrateSelectedGroupsAsync(config.MonitoredGroups.Select(g => g.Name).ToList());
    }

    /// <summary>
    /// Fetches the list of monitored group names from the remote service.
    /// Returns an empty list if the service is unreachable or has no groups.
    /// </summary>
    public async Task<List<string>> FetchServiceGroupNamesAsync()
    {
        var config = _configService.Load();
        var svc = config.Service;
        var scheme = svc.UseTls ? "https" : "http";
        var port = Math.Clamp(svc.Port, 1, 65535);
        var baseUrl = $"{scheme}://{svc.Host}:{port}";
        var thumbprint = AcceptedCertThumbprint ?? svc.TrustedCertThumbprint;

        try
        {
            var token = await ServiceMonitoringClient.LoginAsync(svc, ServiceUsername ?? "", ServicePassword, thumbprint);
            if (token == null) return new List<string>();

            using var handler = new HttpClientHandler();
            if (thumbprint != null)
            {
                var pinned = thumbprint;
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(cert.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync("/api/config/export");
            if (!response.IsSuccessStatusCode) return new List<string>();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("monitoredGroups", out var groupsArray))
            {
                return groupsArray.EnumerateArray()
                    .Select(g => g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList();
            }
        }
        catch { /* best effort */ }

        return new List<string>();
    }

    /// <summary>
    /// Pushes only the selected local groups (plus alerts, email, syslog) to the remote service.
    /// Returns a human-readable result message.
    /// </summary>
    public async Task<string> MigrateSelectedGroupsAsync(List<string> selectedGroupNames)
    {
        var config = _configService.Load();
        var svc = config.Service;
        var scheme = svc.UseTls ? "https" : "http";
        var port = Math.Clamp(svc.Port, 1, 65535);
        var baseUrl = $"{scheme}://{svc.Host}:{port}";

        try
        {
            var thumbprint = AcceptedCertThumbprint ?? svc.TrustedCertThumbprint;

            // Step 1: Login to get JWT
            var token = await ServiceMonitoringClient.LoginAsync(svc, ServiceUsername ?? "", ServicePassword, thumbprint);
            if (token == null)
                return "Migration failed — could not authenticate with the service.";

            // Step 2: Push config via import API
            using var handler = new HttpClientHandler();
            if (thumbprint != null)
            {
                var pinned = thumbprint;
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert != null && string.Equals(cert.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var selectedGroups = config.MonitoredGroups
                .Where(g => selectedGroupNames.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var importPayload = new
            {
                monitoredGroups = selectedGroups,
                alerts = config.Alerts,
                email = config.Email,
                syslog = config.Syslog
            };

            var json = JsonSerializer.Serialize(importPayload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/config/import", content);

            if (!response.IsSuccessStatusCode)
                return $"Migration failed — service returned {(int)response.StatusCode} {response.StatusCode}.";

            var resultJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(resultJson);

            var imported = doc.RootElement.GetProperty("imported");
            var groups = imported.GetProperty("groups").GetInt32();
            var alerts = imported.GetProperty("alerts").GetBoolean();
            var email = imported.GetProperty("email").GetBoolean();
            var syslog = imported.GetProperty("syslog").GetBoolean();

            var parts = new List<string>();
            if (groups > 0) parts.Add($"{groups} group(s)");
            if (alerts) parts.Add("alert settings");
            if (email) parts.Add("email settings");
            if (syslog) parts.Add("syslog settings");

            var sqlAuthGroups = selectedGroups
                .Where(g => g.Connections.Any(c =>
                    string.Equals(c.AuthType, "sql", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(c.CredentialKey)))
                .Select(g => g.Name)
                .ToList();

            var result = $"✓ Migrated: {string.Join(", ", parts)}.";

            if (sqlAuthGroups.Count > 0)
            {
                result += $"\n\n⚠ The following groups use SQL Server authentication and their passwords were NOT transferred: {string.Join(", ", sqlAuthGroups)}. You will need to re-enter these passwords on the service side.";
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Migration failed — {ex.Message}";
        }
    }
}
