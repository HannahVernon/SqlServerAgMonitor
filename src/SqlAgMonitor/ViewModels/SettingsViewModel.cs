using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using SqlAgMonitor.Core.Configuration;

namespace SqlAgMonitor.ViewModels;

public class SettingsViewModel : ViewModelBase
{
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
    private int _exportIntervalHours = 6;

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
    public int ExportIntervalHours { get => _exportIntervalHours; set => this.RaiseAndSetIfChanged(ref _exportIntervalHours, value); }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> TestEmailCommand { get; }

    /// <summary>Raised when the dialog should close. True = saved, False = cancelled.</summary>
    public event Action<bool>? CloseRequested;

    public SettingsViewModel()
    {
        SaveCommand = ReactiveCommand.Create(OnSave);
        CancelCommand = ReactiveCommand.Create(OnCancel);
        TestEmailCommand = ReactiveCommand.Create(OnTestEmail);
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
        ExportIntervalHours = config.Export.ScheduleIntervalHours;
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
        config.Export.ScheduleIntervalHours = ExportIntervalHours;
    }

    private void OnSave()
    {
        CloseRequested?.Invoke(true);
    }

    private void OnCancel()
    {
        CloseRequested?.Invoke(false);
    }

    private void OnTestEmail()
    {
        // TODO: Test SMTP connection
    }
}
