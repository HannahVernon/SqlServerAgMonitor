# SQL Server AG Monitor — Feature Guide

## Quick Start

1. **Launch** — `dotnet run --project src/SqlAgMonitor` (or run the compiled executable)
2. **Add a group** — File → Add AG/DAG… (walks you through a 4-step wizard)
3. **Monitor** — The app polls automatically on the interval you chose

---

## Menu Reference

### File Menu

| Item  | Description |
|---|---|
| Add AG/DAG… | Opens the connection wizard to discover and add Availability Groups |
| Settings… | Opens the settings dialog (General, Email, Syslog, Alerts, Export) |
| Exit | Shuts down the application completely (disposes connections, stops polling) |

### View Menu

| Item | Description |
|---|---|
| Light Theme | Switch to light theme |
| Dark Theme | Switch to dark theme |
| High Contrast Theme | Switch to high-contrast theme |

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
- **Layout is persisted** per tab — column widths, order, and sort are saved and restored

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

---

## Credential Security

- **Windows:** DPAPI (Data Protection API) encrypts credentials using the current user's Windows login. Credentials are machine- and user-bound.
- **Other platforms:** AES-256 encryption with a key derived from a random salt stored alongside the encrypted data.
- **Never plain text** — credentials are encrypted at rest in the AppData directory.

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
