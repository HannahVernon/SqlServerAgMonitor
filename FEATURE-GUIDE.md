# SQL Server AG Monitor — Feature Guide

## Quick Start

1. **Grant SQL permissions** — The monitoring account needs `VIEW SERVER STATE` on each SQL Server instance (see [SQL Server Permissions](#sql-server-permissions) below)
2. **Launch** — `dotnet run --project src/SqlAgMonitor` (or run the compiled executable)
3. **Add a group** — File → Add AG/DAG… (walks you through a 4-step wizard)
4. **Monitor** — The app polls automatically on the interval you chose

---

## Menu Reference

### File Menu

| Item  | Description |
|---|---|
| Add AG/DAG… | Opens the connection wizard to discover and add Availability Groups |
| Settings… | Opens the settings dialog (General, Email, Syslog, Alerts, Export, History) |
| Exit | Shuts down the application completely (disposes connections, stops polling) |

### View Menu

| Item | Description |
|---|---|
| Light Theme | Switch to light theme |
| Dark Theme | Switch to dark theme |
| High Contrast Theme | Switch to high-contrast theme |
| Alert History | Toggle the Alert History tab (always-visible first tab) |
| Statistics… | Open the Statistics & Trends window with historical charts and data export |

### Monitoring Menu

| Item | Description |
|---|---|
| Refresh | Force an immediate poll of the selected tab (same as F5) |
| Pause All | Pause polling on all tabs. Status bar shows "PAUSED" and the timer displays "Paused · last update Xs ago" |
| Resume All | Resume polling on all paused tabs |

### Help Menu

| Item | Description |
|---|---|
| Open Log Directory | Opens the log file directory in your OS file manager |
| About | Shows the About dialog with version information |

---

## Keyboard Shortcuts

| Key | Action |
|---|---|
| F5 | Refresh the active tab immediately (blocking acquire — waits for any in-progress poll to finish) |
| Alt+F | Open File menu |
| Alt+V | Open View menu |
| Alt+M | Open Monitoring menu |
| Alt+H | Open Help menu |

---

## Add AG/DAG Wizard

**Step 1 — Connect:** Enter a SQL Server hostname/instance. Choose Windows Authentication or SQL Authentication. Credentials are encrypted (DPAPI on Windows, AES-256 elsewhere) — never stored in plain text.

**Step 2 — Select Groups:** The app runs discovery queries against `sys.availability_groups`, `sys.availability_replicas`, and `sys.dm_hadr_availability_replica_states` to list all AGs and DAGs. Check the ones you want to monitor.

**Step 3 — Configure:** Set the polling interval (seconds) for each selected group.

**Step 4 — DAG Members:** For Distributed AGs, the wizard discovers member AGs via `sys.fn_hadr_distributed_ag_replica()` and prompts for connection details to each member server (since DAGs span independent SQL Server instances that may require separate credentials).

---

## System Tray

- **Closing the window** (X button) saves layout and hides the window to the system tray. Monitoring and alerting continue running.
- **Clicking the tray icon** or selecting **Show** from the tray context menu restores the window.
- **Exit** from the tray context menu (or File → Exit) performs a full shutdown.

---

## Topology Visualization (Upper Panel)

The topology diagram renders a live, animated view of each AG/DAG:

### Replica Cards
- **Server name** — Truncated hostname displayed prominently
- **Role badge** — GREEN "PRIMARY" or BLUE "SECONDARY"
- **Database count** — "N databases"
- **Availability mode** — "SYNC" or "ASYNC"
- **Connection state** — RED "DISCONNECTED" shown only when the replica is unreachable
- **Health dot** — Solid circle in the bottom-left corner colored by the worst database health on that replica

### Arrows (Primary → Secondary)
- **Color** — Matches the worst health level of databases on the destination replica:
  - 🟢 Green = InSync (≤ 1 MB lag)
  - 🟡 Yellow = SlightlyBehind (≤ 100 MB lag)
  - 🟠 Orange = ModeratelyBehind (≤ 10 GB lag)
  - 🔴 Red = DangerZone (> 10 GB or disconnected)
- **Line style** — Solid = synchronous commit, Dashed = asynchronous commit
- **Animated particles** — 3 dots per arrow flowing from primary to secondary, colored by health

### DAG Group Containers
- Member AGs are grouped inside dashed-border containers
- Container border color: dark green for the primary member, dark blue for secondary members
- Container label shows the member AG name

### Interaction
- **Click a replica card** to filter the data grid to show only that replica's databases
- **Click the background** to clear the filter

---

## Data Grid (Lower Panel)

The data grid shows one row per database, with columns for every monitored metric:

### Static Columns

| Column | Description | Color Coding |
|---|---|---|
| Database | Database name | — |
| Health | Visual indicator dot | Green/Yellow/Orange/Red per health level |
| Worst Sync | Worst synchronization state across all secondaries | Green=Synchronized, Yellow=Synchronizing, Orange=Reverting/Initializing, Red=NotSynchronizing |
| Suspended | Whether any secondary is suspended | Red if true |
| Suspend Reason | Why replication is suspended | Red text |
| Max Log Block Diff | Largest LSN offset from primary across all secondaries | Colored per health level |
| Lag (seconds) | Worst secondary lag in seconds (SQL Server 2016+ only; shows 0 on 2014) | — |
| Send Queue (KB) | Largest unsent log queue across secondaries | — |
| Redo Queue (KB) | Largest unredo log queue across secondaries | — |
| Send Rate (KB/s) | Log send rate | — |
| Redo Rate (KB/s) | Redo rate | — |

### Dynamic Replica LSN Columns

For each replica in the AG, a column is auto-generated:
- **Header:** `ServerName (P)` for primary, `ServerName (S)` for secondary
- **Value:** Last hardened LSN formatted as `VVVVVVVV:BBBBBBBB` (hex VLF:Block)
- **Cell background:** Color-coded by that replica's synchronization state for the database

### Grid Interactions
- **Right-click** → Copy Cell, Copy Row (tab-delimited), Copy All (tab-delimited with headers)
- **Click column headers** to sort
- **Drag column headers** to reorder
- **Drag column edges** to resize (proportionally sized by default)
- **Double-click column header edge** to auto-fit column width to content
- **Layout is persisted** per tab — column widths, order, and sort are saved and restored

---

## Alert History Tab

The Alert History tab is the always-visible first tab in the main window, showing a chronological DataGrid of all alert events stored in DuckDB. Events load automatically on startup and refresh when new alerts arrive.

### Columns

| Column | Description |
|---|---|
| Time | Alert timestamp (yyyy-MM-dd HH:mm:ss) |
| Severity | Critical, Warning, or Information |
| Type | Alert type (e.g., SyncFellBehind, ReplicaDisconnected) |
| Group | AG or DAG name |
| Replica | Replica server name (if applicable) |
| Database | Database name (if applicable) |
| Message | Descriptive alert message |

### Behavior
- Loads the most recent 1,000 events on startup
- **Auto-refreshes** when a new alert fires
- **Refresh button** to manually reload
- **Sortable columns** — click any column header to sort
- **Double-click column header edge** to auto-fit column width to content

---

## Statistics & Trends

The Statistics window provides historical trend analysis of AG health metrics over time. Data is captured every polling cycle and automatically summarized into hourly and daily rollups for efficient long-term storage.

### Opening

**View → Statistics…** opens the Statistics window.

### Time Range

Preset buttons select common ranges: **24h**, **7d**, **30d**, **90d**, **180d**, **365d**. A custom date range picker is also available for arbitrary start/end dates.

The app auto-selects the appropriate data tier based on the requested range:
- **≤ 48 hours** — raw per-poll snapshots (full resolution)
- **≤ 90 days** — hourly summaries (MIN/MAX/AVG aggregates)
- **> 90 days** — daily summaries (weighted averages from hourly data)

### Filters

Dropdown filters narrow the data:
- **Group** — select a specific AG/DAG
- **Replica** — filter to a specific secondary replica
- **Database** — filter to a specific database

### Summary Grid

A DataGrid at the top displays key metrics for the selected time range and filters, including send queue, redo queue, secondary lag, and log block difference values.

### Charts

Four line charts visualize trends over the selected period:

| Chart | Metric |
|---|---|
| Log Send Queue | Unsent transaction log (KB) over time |
| Redo Queue | Transaction log awaiting replay (KB) over time |
| Secondary Lag | Time-based lag in seconds over time |
| Log Block Difference | LSN offset between primary and secondary over time |

### Excel Export

Click the **📊 Export to Excel** button to save the currently displayed data (full dataset, not just visible rows) to an `.xlsx` file.

### Data Retention

Data is automatically summarized and pruned on a schedule:
- **Raw snapshots** — retained for 48 hours (default)
- **Hourly summaries** — retained for 90 days (default)
- **Daily summaries** — retained for 730 days (default)

Retention periods are configurable via `SnapshotRetentionSettings`.

---

## Alert System

### Alert Types

| Alert | Severity | What Triggers It |
|---|---|---|
| ConnectionLost | Critical | The app can no longer reach the monitored SQL Server |
| ConnectionRestored | Information | A previously lost connection was re-established |
| ReplicaDisconnected | Critical | A replica's connected_state changed to DISCONNECTED |
| HealthDegraded | Critical | Overall AG synchronization_health dropped below HEALTHY |
| FailoverOccurred | Warning | A replica's role changed (e.g., PRIMARY became SECONDARY) |
| SyncFellBehind | Warning | Log block difference exceeded the configurable threshold (default 1 MB) |
| SuspendDetected | Warning | A database's is_suspended changed from false to true |
| ResumeDetected | Information | A database's is_suspended changed from true to false |
| SyncModeChanged | Warning | A replica's availability_mode changed (SYNC ↔ ASYNC) |

### Delivery Channels

**SMTP Email** — HTML-formatted emails with color-coded severity headers (red=Critical, yellow=Warning, blue=Information). Configure via Settings → Email Notifications.

**Syslog** — RFC 5424 structured syslog over UDP or TCP with configurable facility codes. Severity maps to syslog levels (Critical→2, Warning→4, Information→6). Configure via Settings → Syslog.

**OS Notifications** — Desktop toast notifications via the platform's native notification system.

### Cooldown and Muting

- **Master cooldown** (default 5 minutes) — After any alert fires, all alerts are suppressed for the cooldown duration to prevent notification storms.
- **Per-alert mute** — Mute a specific alert type for a specific group, either for a fixed duration or permanently. Unmute at any time.

---

## Settings Dialog

### General Tab
- Polling interval defaults
- Log level configuration

### Email Notifications Tab
- SMTP server, port, TLS toggle
- From address and recipient list
- Test button to verify connectivity

### Syslog Tab
- Server address and port
- Protocol (UDP/TCP)
- Facility code selection
- Test button

### Alerts Tab
- Enable/disable individual alert types
- Configure custom thresholds (e.g., SyncFellBehind log block diff threshold)
- Per-alert channel toggles (email, syslog, OS notification)
- Master cooldown duration

### Export Tab
- Enable/disable scheduled HTML export
- Export directory path
- Export interval

### History Tab
- **Enable automatic pruning** — toggle on/off (default: on)
- **Maximum age (days)** — alerts older than this are deleted automatically (default: 90, 0 = unlimited)
- **Maximum number of records** — only the most recent N records are kept (default: 0 = unlimited)
- Pruning runs automatically 10 seconds after startup and then every 24 hours

### Service Tab

| Setting | Default | Description |
|---------|---------|-------------|
| Enable Service Client Mode | Off | When enabled, the app connects to a remote SqlAgMonitor Service instead of monitoring SQL Server directly |
| Service Host | localhost | Hostname or IP address of the service |
| Service Port | 58432 | Port the service listens on |
| Username | — | Username for service authentication |
| Password | — | Password for service authentication (stored securely via AesCredentialStore; never saved in plain text) |
| Require TLS | Off | When enabled, uses HTTPS for the SignalR connection |
| Test Connection | — | Button that probes the service, handles TLS certificate trust, and verifies authentication credentials |

When service mode is enabled, the app does not make direct SQL Server connections. All monitoring data, alerts, and statistics come from the remote service.

---

## Service Client Connection

When service mode is enabled (Settings → Service tab), the desktop app connects to a remote SqlAgMonitor Service instead of monitoring SQL Server directly. This section describes the connection flow and related features.

### Test Connection

The **Test Connection** button in the Service tab performs a full connection probe:

1. Attempts to reach the service at the configured host and port
2. If TLS is enabled and the server presents an untrusted certificate, opens the **Certificate Trust Dialog**
3. Authenticates with the provided username and password
4. Reports success or a specific error message

### Certificate Trust Dialog

When the service presents a TLS certificate that is not trusted by the OS certificate store (e.g., self-signed or issued by an internal CA), a dialog appears showing:

- Certificate subject and issuer
- Validity period
- SHA-256 thumbprint

The user can choose to **trust and pin** the certificate thumbprint. Once pinned, the app accepts that specific certificate for all future connections without prompting again. Both `LoginAsync` and `ConnectAsync` in `ServiceMonitoringClient` use the pinned thumbprint, including the ongoing SignalR hub connection.

### Auto-Login on Startup

When service mode is enabled and credentials (username + password) are stored, the app automatically attempts to log in and establish the SignalR connection on startup. If the service's TLS certificate is untrusted and no thumbprint has been pinned, the Certificate Trust Dialog is shown before completing the connection.

### Connection Status Indicator

In service mode, the main window status bar displays a live connection indicator:

- **● Connected** — SignalR connection is active and receiving data
- **○ Disconnected** — SignalR connection is down or not yet established

The indicator updates in real time as the SignalR connection state changes.

### Config Migration

When service mode is **newly enabled** and the app has locally configured monitored groups, a **Migration Dialog** offers to push the local configuration to the remote service. This is a one-time convenience to avoid re-entering groups, alert settings, email, and syslog configuration on the service.

The migration uses `POST /api/config/import` to perform an **additive merge** — existing service configuration is preserved, and the local settings are added on top. The dialog warns that **SQL authentication passwords are not transferred** (they must be re-entered on the service side).

The corresponding `GET /api/config/export` endpoint allows retrieving the service's current configuration with credentials redacted.

---

## Credential Security

- **Windows:** DPAPI (Data Protection API) encrypts credentials using the current user's Windows login. Credentials are machine- and user-bound.
- **Other platforms:** AES-256 encryption with a key derived from a random salt stored alongside the encrypted data.
- **Never plain text** — credentials are encrypted at rest in the AppData directory.

---

## SQL Server Permissions

### Monitoring (read-only)

The monitoring account requires two server-level permissions on each SQL Server instance:

```sql
/* Grant to a SQL login */
GRANT VIEW SERVER STATE TO [SqlAgMonitorLogin];
GRANT VIEW ANY DEFINITION TO [SqlAgMonitorLogin];

/* Or grant to a Windows/domain account */
GRANT VIEW SERVER STATE TO [DOMAIN\ServiceAccount];
GRANT VIEW ANY DEFINITION TO [DOMAIN\ServiceAccount];
```

These permissions cover all catalog views and DMVs used by the app:

| DMV / System View | Permission | Purpose |
|---|---|---|
| `sys.availability_groups` | `VIEW ANY DEFINITION` | Enumerate AGs and DAGs |
| `sys.availability_replicas` | `VIEW ANY DEFINITION` | Replica names, availability modes, failover modes |
| `sys.dm_hadr_availability_replica_states` | `VIEW SERVER STATE` | Replica roles, connected state, sync health |
| `sys.dm_hadr_database_replica_states` | `VIEW SERVER STATE` | Database sync state, LSN values, queues, rates, lag |
| `sys.databases` | (public) | Map database IDs to names |
| `sys.fn_hadr_distributed_ag_replica()` | `VIEW ANY DEFINITION` | Drill from DAG to member AG replicas (SQL 2016+) |

> **Why both permissions?** `VIEW SERVER STATE` covers the `sys.dm_hadr_*` DMVs, but AG metadata lives in catalog views (`sys.availability_groups`, `sys.availability_replicas`) governed by separate [metadata visibility rules](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration). Without `VIEW ANY DEFINITION`, the catalog views return zero rows and monitoring silently fails.

### Control operations (optional)

These permissions are only needed if you use failover or suspend/resume features:

| Permission | Operation |
|---|---|
| `ALTER AVAILABILITY GROUP` | Manual or forced failover |
| `ALTER DATABASE` / `db_owner` | Suspend or resume database replication |

### Authentication modes

| Deployment | Recommended Auth |
|---|---|
| **Desktop app (standalone)** | Windows Authentication (runs as the logged-in user) |
| **Service (domain account)** | Windows Authentication (grant `VIEW SERVER STATE` + `VIEW ANY DEFINITION` to the service account) |
| **Service (LOCAL SERVICE)** | SQL Authentication (create a dedicated SQL login with `VIEW SERVER STATE` + `VIEW ANY DEFINITION`) |

---

## SQL Server 2014 Compatibility

The app targets SQL Server 2014 and later. On SQL Server 2014, the `secondary_lag_seconds` column does not exist in `sys.dm_hadr_database_replica_states`.

**Automatic fallback:** The app tries the standard query first. If it gets SQL error 207 ("Invalid column name"), it permanently switches to a legacy query that returns NULL for that column. This is sticky for the session (no repeated error attempts).

**Impact on 2014:**
- The "Lag (seconds)" grid column shows 0
- All other metrics (LSN comparison, queues, rates, sync state, health) work identically
- Topology visualization is unaffected (uses log block difference, not time lag)
- Alert thresholds based on log block difference work normally

---

## Event History

All state changes and alerts are recorded in a DuckDB database (AppData directory). Events are queryable and used for the scheduled HTML export feature.

## Log Files

Daily rotating log files are stored in `%APPDATA%\SqlAgMonitor\logs\` (Windows) or `~/.config/SqlAgMonitor/logs/` (Linux/macOS). Open the log directory from Help → Open Log Directory.

## Themes

Three themes available from the View menu:
- **Dark** (default) — Dark backgrounds optimized for low-light monitoring
- **Light** — Light backgrounds for bright environments
- **High Contrast** — Maximum contrast for accessibility

---

## Windows Service Mode

The SqlAgMonitor Windows Service runs headless monitoring and exposes a SignalR API for remote clients.

### Deployment

#### Graphical Installer (recommended)

Run `SqlAgMonitor.Installer.exe` (requires administrator). The wizard walks through:

1. **Install path** — where to publish the service (default: `C:\Program Files\SqlAgMonitor`)
2. **Service account** — LOCAL SERVICE (default) or a domain account for Windows-authenticated SQL connections
3. **Port** — service listening port (default: 58432)
4. **TLS** — optional HTTPS with certificate
5. **Admin credentials** — initial username and password for the service API
6. **Install** — publishes the service, creates the Windows Service, starts it, creates the admin user, and registers in Add/Remove Programs

To uninstall, use Windows Settings → Apps → SQL Server AG Monitor Service, or run `SqlAgMonitor.Installer.exe /uninstall`.

#### PowerShell Scripts (advanced)

1. **Publish:** `.\scripts\Publish-Service.ps1` — builds a self-contained single-file executable
2. **Install:** `.\scripts\Install-Service.ps1` — registers as a Windows Service with delayed auto-start
3. **Initial setup:** `POST http://host:58432/api/auth/setup` with `{"username":"admin","password":"YourPassword"}` to create the first user account
4. **Start:** `Start-Service SqlAgMonitorService`

### Authentication

The service uses JWT bearer tokens. Clients authenticate via `POST /api/auth/login` and include the token in subsequent SignalR connections. Passwords are hashed with bcrypt (work factor 12). The JWT signing key is auto-generated per deployment and stored in `%APPDATA%\SqlAgMonitor\service\jwt-signing-key.bin`.

### Uninstall

Use Windows Settings → Apps → SQL Server AG Monitor Service for GUI uninstall, or run `SqlAgMonitor.Installer.exe /uninstall` for silent removal. Alternatively, `.\scripts\Uninstall-Service.ps1` stops and removes the Windows Service (published files are left in place for manual cleanup).
