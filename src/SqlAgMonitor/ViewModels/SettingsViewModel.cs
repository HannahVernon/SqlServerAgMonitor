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
using SqlAgMonitor.Core.Services.Notifications;

namespace SqlAgMonitor.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;
    private readonly IEmailNotificationService _emailService;

    private int _globalPollingIntervalSeconds;
    private string _theme = "dark";

    // Email
    private bool _emailEnabled;
    private string _smtpServer = string.Empty;
    private int _smtpPort = 587;
    private bool _useTls = true;
    private string _fromAddress = string.Empty;
    private string _toAddresses = string.Empty;
    private string? _emailUsername;

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
    private string _serviceHost = "localhost";
    private int _servicePort = 58432;
    private string? _serviceUsername;
    private string _servicePassword = string.Empty;
    private bool _serviceUseTls;

    // UI feedback
    private string? _testEmailStatus;
    private string? _testConnectionStatus;

    public List<string> ThemeOptions { get; } = new() { "Light", "Dark", "High Contrast" };
    public List<string> SyslogProtocolOptions { get; } = new() { "UDP", "TCP" };

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

    // Email settings
    public bool EmailEnabled { get => _emailEnabled; set => this.RaiseAndSetIfChanged(ref _emailEnabled, value); }
    public string SmtpServer { get => _smtpServer; set => this.RaiseAndSetIfChanged(ref _smtpServer, value); }
    public int SmtpPort { get => _smtpPort; set => this.RaiseAndSetIfChanged(ref _smtpPort, value); }
    public bool UseTls { get => _useTls; set => this.RaiseAndSetIfChanged(ref _useTls, value); }
    public string FromAddress { get => _fromAddress; set => this.RaiseAndSetIfChanged(ref _fromAddress, value); }
    public string ToAddresses { get => _toAddresses; set => this.RaiseAndSetIfChanged(ref _toAddresses, value); }
    public string? EmailUsername { get => _emailUsername; set => this.RaiseAndSetIfChanged(ref _emailUsername, value); }

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
    public string ServiceHost { get => _serviceHost; set => this.RaiseAndSetIfChanged(ref _serviceHost, value); }
    public int ServicePort { get => _servicePort; set => this.RaiseAndSetIfChanged(ref _servicePort, value); }
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

    public SettingsViewModel(IConfigurationService configService, IEmailNotificationService emailService)
    {
        _configService = configService;
        _emailService = emailService;
        SaveCommand = ReactiveCommand.CreateFromTask(OnSaveAsync);
        CancelCommand = ReactiveCommand.CreateFromTask(OnCancelAsync);
        TestEmailCommand = ReactiveCommand.CreateFromTask(OnTestEmailAsync);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(OnTestConnectionAsync);
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
        EmailEnabled = config.Email.Enabled;
        SmtpServer = config.Email.SmtpServer;
        SmtpPort = config.Email.SmtpPort;
        UseTls = config.Email.UseTls;
        FromAddress = config.Email.FromAddress;
        ToAddresses = string.Join("; ", config.Email.ToAddresses);
        EmailUsername = config.Email.Username;
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
        ServiceHost = config.Service.Host;
        ServicePort = config.Service.Port;
        ServiceUsername = config.Service.Username;
        ServiceUseTls = config.Service.UseTls;
        AcceptedCertThumbprint = config.Service.TrustedCertThumbprint;
    }

    public void ApplyTo(AppConfiguration config)
    {
        config.GlobalPollingIntervalSeconds = GlobalPollingIntervalSeconds;
        config.Theme = DisplayToTheme.GetValueOrDefault(Theme, "dark");
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
        if (AcceptedCertThumbprint != null)
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
            _configService.Save(config);

            var success = await _emailService.TestConnectionAsync(cancellationToken);
            TestEmailStatus = success ? "✓ Test email sent successfully." : "✗ Test email failed.";
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
            // Phase 1: if TLS, probe for cert issues first
            string? pinnedThumbprint = AcceptedCertThumbprint;

            if (ServiceUseTls && pinnedThumbprint == null)
            {
                X509Certificate2? capturedCert = null;

                using var probeHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                    {
                        if (errors == SslPolicyErrors.None)
                            return true;
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
                    await probeClient.GetAsync("/api/auth/login", cancellationToken);
                }
                catch (HttpRequestException) when (capturedCert != null)
                {
                    if (ConfirmUntrustedCertificate != null)
                    {
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
            }

            // Phase 2: attempt login
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
}
