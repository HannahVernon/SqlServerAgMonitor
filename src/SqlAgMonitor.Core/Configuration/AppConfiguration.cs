using System.Text.Json.Serialization;

namespace SqlAgMonitor.Core.Configuration;

public class AppConfiguration
{
    /// <summary>Default polling interval in seconds for AG/DAG monitoring loops. 16s balances
    /// responsiveness against SQL Server DMV query load. Per-group overrides via
    /// <see cref="MonitoredGroupConfig.PollingIntervalSeconds"/>. Clamped to ≥ 5s at runtime.</summary>
    public int GlobalPollingIntervalSeconds { get; set; } = 16;
    public string Theme { get; set; } = "dark";
    public EmailSettings Email { get; set; } = new();
    public SyslogSettings Syslog { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public ServiceSettings Service { get; set; } = new();
    public List<MonitoredGroupConfig> MonitoredGroups { get; set; } = new();
}

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string SmtpServer { get; set; } = string.Empty;
    /// <summary>Standard SMTP submission port with STARTTLS (RFC 6409).</summary>
    public int SmtpPort { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string FromAddress { get; set; } = string.Empty;
    public List<string> ToAddresses { get; set; } = new();
    public string? Username { get; set; }
    /// <summary>Reference to credential store, never stored as plain text.</summary>
    public string? CredentialKey { get; set; }
}

public class SyslogSettings
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = string.Empty;
    /// <summary>Standard syslog port (RFC 5424).</summary>
    public int Port { get; set; } = 514;
    public string Protocol { get; set; } = "UDP";
    public string Facility { get; set; } = "local0";
}

public class AlertSettings
{
    /// <summary>Minimum minutes between any two published alerts. Prevents alert storms during
    /// cascading failures. Clamped to ≥ 0 at runtime. Set to 0 to disable cooldown.</summary>
    public int MasterCooldownMinutes { get; set; } = 5;
    public Dictionary<string, AlertTypeConfig> AlertTypeOverrides { get; set; } = new();
}

public class AlertTypeConfig
{
    public bool Enabled { get; set; } = true;
    public int? ThresholdValue { get; set; }
    public int? ThresholdDurationSeconds { get; set; }
    public bool SendEmail { get; set; } = true;
    public bool SendSyslog { get; set; } = true;
    public bool SendOsNotification { get; set; } = true;
}

public class ExportSettings
{
    public bool Enabled { get; set; }
    public string ExportPath { get; set; } = string.Empty;
    /// <summary>Minutes between HTML export runs. Clamped to ≥ 1 at runtime.</summary>
    public int ScheduleIntervalMinutes { get; set; } = 60;
}

public class MonitoredGroupConfig
{
    public string Name { get; set; } = string.Empty;
    public string GroupType { get; set; } = "AvailabilityGroup";
    public int? PollingIntervalSeconds { get; set; }
    public List<ConnectionConfig> Connections { get; set; } = new();
    public Dictionary<string, AlertTypeConfig>? AlertOverrides { get; set; }
    public List<MutedAlert> MutedAlerts { get; set; } = new();
}

public class ConnectionConfig
{
    public string Server { get; set; } = string.Empty;
    public string AuthType { get; set; } = "windows";
    public string? Username { get; set; }
    /// <summary>Reference key into credential store. Never plain text.</summary>
    public string? CredentialKey { get; set; }
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
}

public class MutedAlert
{
    public string AlertType { get; set; } = string.Empty;
    public DateTimeOffset? MutedUntil { get; set; }
    public bool IsPermanent { get; set; }
}

public class HistorySettings
{
    /// <summary>Maximum days to retain alert events before auto-pruning. Clamped to ≥ 1 at runtime.</summary>
    public int? MaxRetentionDays { get; set; } = 90;
    public int? MaxRecords { get; set; }
    public bool AutoPruneEnabled { get; set; } = true;
    public SnapshotRetentionSettings SnapshotRetention { get; set; } = new();
}

public class ServiceSettings
{
    /// <summary>When true, the desktop app connects to a remote SqlAgMonitor Windows Service
    /// via SignalR instead of monitoring SQL Server directly.</summary>
    public bool Enabled { get; set; }
    /// <summary>Hostname or IP of the SqlAgMonitor Service. Default: localhost.</summary>
    public string Host { get; set; } = "localhost";
    /// <summary>Port the SqlAgMonitor Service listens on. Clamped to 1–65535 at runtime.</summary>
    public int Port { get; set; } = 58432;
    /// <summary>Username for authenticating to the service API.</summary>
    public string? Username { get; set; }
    /// <summary>Reference key into credential store for the service password. Never plain text.</summary>
    public string? CredentialKey { get; set; }
    /// <summary>When true, require TLS for the SignalR connection.</summary>
    public bool UseTls { get; set; }
    /// <summary>Pinned certificate thumbprint for self-signed/untrusted TLS certs.
    /// Set when the user accepts an untrusted cert — subsequent connections skip the trust prompt.</summary>
    public string? TrustedCertThumbprint { get; set; }
}

public class SnapshotRetentionSettings
{
    /// <summary>Hours to keep raw per-poll snapshots before summarizing to hourly. Default: 48.</summary>
    public int RawRetentionHours { get; set; } = 48;
    /// <summary>Days to keep hourly summaries before summarizing to daily. Default: 90.</summary>
    public int HourlyRetentionDays { get; set; } = 90;
    /// <summary>Days to keep daily summaries. Default: 730 (2 years).</summary>
    public int DailyRetentionDays { get; set; } = 730;
}
