# SQL Server AG Monitor

A cross-platform desktop application for real-time monitoring of SQL Server Availability Groups (AGs) and Distributed Availability Groups (DAGs). Built with Avalonia UI for Windows, macOS, and Linux.

📖 [Feature Guide](FEATURE-GUIDE.md) · 🏗️ [Architecture](ARCHITECTURE.md) · 📦 [Install Guide](INSTALL-GUIDE.md) · 🔌 [Service Protocol](SERVICE-PROTOCOL.md) · 🔮 [Service Plan](SERVICE-PLAN.md) · 🤝 [Contributing](CONTRIBUTING.md)

## What It Monitors

The app polls SQL Server DMVs on a configurable interval and tracks the following for every database in every AG and DAG:

| Metric | Source DMV | Description |
|---|---|---|
| **Synchronization state** | `dm_hadr_database_replica_states` | SYNCHRONIZED, SYNCHRONIZING, NOT SYNCHRONIZING, etc. |
| **Last hardened LSN** | `dm_hadr_database_replica_states` | Last log block written to disk on each replica |
| **Log block difference** | Computed from LSN comparison | Byte-distance between primary and secondary LSN positions (slot-stripped) |
| **Secondary lag (seconds)** | `dm_hadr_database_replica_states` | Time-based lag reported by SQL Server (2016+ only) |
| **Log send queue (KB)** | `dm_hadr_database_replica_states` | Unsent transaction log queued on the primary |
| **Redo queue (KB)** | `dm_hadr_database_replica_states` | Transaction log waiting to be redone on the secondary |
| **Log send rate (KB/s)** | `dm_hadr_database_replica_states` | Current rate of log shipping to each secondary |
| **Redo rate (KB/s)** | `dm_hadr_database_replica_states` | Current rate of transaction log replay on each secondary |
| **Suspended / reason** | `dm_hadr_database_replica_states` | Whether replication is suspended and why |

At the replica level, the app also tracks:

| Metric | Source DMV | Description |
|---|---|---|
| **Replica role** | `dm_hadr_availability_replica_states` | PRIMARY or SECONDARY (failover detection) |
| **Connected state** | `dm_hadr_availability_replica_states` | CONNECTED or DISCONNECTED |
| **Operational state** | `dm_hadr_availability_replica_states` | ONLINE, OFFLINE, PENDING, FAILED, FAILED_NO_QUORUM |
| **Synchronization health** | `dm_hadr_availability_replica_states` | HEALTHY, PARTIALLY_HEALTHY, NOT_HEALTHY |
| **Recovery health** | `dm_hadr_availability_replica_states` | ONLINE or IN_PROGRESS |
| **Availability mode** | `sys.availability_replicas` | SYNCHRONOUS_COMMIT or ASYNCHRONOUS_COMMIT |
| **Failover mode** | `sys.availability_replicas` | AUTOMATIC or MANUAL |

For **Distributed Availability Groups**, the app uses `sys.fn_hadr_distributed_ag_replica()` to drill from the DAG through to each member AG's local replicas and databases, connecting to each member independently.

## Health Assessment

Log block difference is classified into four health levels, used throughout the UI for color coding:

| Health Level | Color | Threshold | Meaning |
|---|---|---|---|
| **InSync** | 🟢 Green | ≤ 1 MB offset | Secondary is fully caught up |
| **SlightlyBehind** | 🟡 Yellow | ≤ 100 MB offset | Minor lag, likely transient |
| **ModeratelyBehind** | 🟠 Orange | ≤ 10 GB offset | Moderate lag, within same VLF range |
| **DangerZone** | 🔴 Red | > 10 GB or disconnected | Major lag or VLF boundary crossed |

## Alerting

The alert engine evaluates every poll snapshot by comparing it to the previous snapshot and detects the following state transitions:

| Alert | Severity | Trigger |
|---|---|---|
| **ConnectionLost** | Critical | Monitoring connection to a server failed |
| **ConnectionRestored** | Information | Connection re-established after loss |
| **ReplicaDisconnected** | Critical | A replica's connected state changed to DISCONNECTED |
| **HealthDegraded** | Critical | Overall AG health dropped to NOT_HEALTHY or PARTIALLY_HEALTHY |
| **FailoverOccurred** | Warning | A replica's role changed (PRIMARY ↔ SECONDARY) |
| **SyncFellBehind** | Warning | Log block difference exceeded configurable threshold (default: 1 MB) |
| **SuspendDetected** | Warning | Database replication was suspended |
| **ResumeDetected** | Information | Database replication was resumed |
| **SyncModeChanged** | Warning | Availability mode changed (SYNC ↔ ASYNC) |

### Alert delivery channels

- **SMTP email** — HTML-formatted with color-coded severity headers. Configurable server, port, TLS, and recipients.
- **Syslog** — RFC 5424 structured data over UDP or TCP. Configurable facility and severity mapping.
- **OS notifications** — Desktop toast notifications via the platform's native notification system.

Each alert type can be individually enabled/disabled and configured with custom thresholds. A **master cooldown** (default 5 minutes) suppresses all alerts after any alert fires. Individual alerts can also be **muted** per-group, either for a duration or permanently.

## Features

- **Animated topology visualization** — Server cards connected by particle-animated arrows, color-coded by health level. Solid lines for synchronous commit, dashed for asynchronous. Click a replica card to filter the data grid.
- **Split view** — Topology diagram on top, sortable/resizable data grid on bottom.
- **Multi-tab monitoring** — One tab per AG/DAG, each with its own persistent SQL connection and configurable polling interval.
- **Auto-discovery wizard** — Connect to a server to automatically discover all AGs and DAGs, then select which to monitor.
- **Event history** — DuckDB-backed structured event storage with daily rotating log files. Automatic pruning by age (default 90 days) and/or record count.
- **Alert History tab** — Persistent first tab (View → Alert History) showing chronological alert events with sortable columns. Auto-refreshes when new alerts arrive.
- **Scheduled HTML export** — Periodic health reports saved to a configurable directory.
- **System tray** — Closing the window minimizes to the system tray; monitoring and alerting continue in the background. Use File → Exit for a full shutdown.
- **Themes** — Light, dark, and high-contrast.
- **Secure credentials** — DPAPI encryption on Windows; AES-256 encrypted fallback on other platforms. No plain-text passwords.
- **DataGrid features** — Right-click to copy cell, row, or all rows. Per-replica LSN columns displayed as hex `VLF:Block`. Color-coded sync state cells. Per-tab column layout persistence (widths, order, sort). Double-click column header edge to auto-fit width to content.
- **SQL Server 2014+ compatibility** — Automatically falls back to a legacy query when `secondary_lag_seconds` is unavailable (SQL error 207). The time-lag column shows 0 on SQL Server 2014; all other metrics remain fully functional.
- **Statistics & Trends** — Historical trend charts (View → Statistics…) with time range presets from 24 hours to 1 year plus custom date pickers. Three-tier data retention (raw snapshots → hourly → daily summaries) with automatic rollup and pruning. Summary grid, four line charts (Log Send Queue, Redo Queue, Secondary Lag, Log Block Difference), and one-click Excel export.
- **Keyboard shortcuts** — F5 to refresh the active tab, standard menu accelerators.
- **Windows Service mode** — Run monitoring as a headless Windows Service (`SqlAgMonitor.Service`) with real-time SignalR API. The desktop app connects remotely for live data, alerts, statistics, and Excel export. JWT bearer authentication with bcrypt-hashed local user store. Automatic reconnect with exponential backoff. Protocol versioning (`GET /api/version`) ensures client/service compatibility — see [SERVICE-PROTOCOL.md](SERVICE-PROTOCOL.md). Graphical installer (`SqlAgMonitor.Installer`) handles deployment, service registration, and initial admin setup with Add/Remove Programs compliance.

## Technology Stack

| Component | Technology |
|---|---|
| UI Framework | Avalonia UI 11.x |
| .NET Version | .NET 9 |
| MVVM Framework | ReactiveUI |
| SQL Client | Microsoft.Data.SqlClient |
| Event Storage | DuckDB |
| Charts | LiveCharts2 (SkiaSharp) |
| Excel Export | ClosedXML |
| Configuration | JSON (AppData) |
| SignalR | ASP.NET Core SignalR (server + client) |
| Authentication | JWT Bearer + bcrypt |
| Service Host | Kestrel + Windows Service |

## SQL Server Permissions

The monitoring account needs **read-only access** to Availability Group catalog views and DMVs. Grant on every SQL Server instance being monitored:

```sql
/* Minimum permissions for AG/DAG monitoring */
GRANT VIEW SERVER STATE TO [DOMAIN\ServiceAccount];
GRANT VIEW ANY DEFINITION TO [DOMAIN\ServiceAccount];
```

| Permission | Required For |
|---|---|
| `VIEW SERVER STATE` | DMV access — replica states, database sync status, LSN values, send/redo queues |
| `VIEW ANY DEFINITION` | Catalog view visibility — `sys.availability_groups`, `sys.availability_replicas` |
| `ALTER AVAILABILITY GROUP` | Failover operations (not required for monitoring) |
| `ALTER DATABASE` / `db_owner` | Suspend/resume replication (not required for monitoring) |

> **Why both?** `VIEW SERVER STATE` covers DMVs like `sys.dm_hadr_availability_replica_states`, but AG metadata lives in catalog views (`sys.availability_groups`, `sys.availability_replicas`) which are governed by separate metadata visibility rules. Without `VIEW ANY DEFINITION`, the catalog views return zero rows and monitoring silently fails.

> **Tip:** If using SQL Authentication, create a dedicated login with `VIEW SERVER STATE` and `VIEW ANY DEFINITION`. If using Windows Authentication (domain service account or gMSA), grant both permissions to that account.

## Building

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (pinned via `global.json`).

```bash
dotnet build
```

## Running

```bash
dotnet run --project src/SqlAgMonitor
```

## Project Structure

```
SqlAgMonitor.sln
├── src/SqlAgMonitor/              # Avalonia desktop app (standalone or service-client mode)
│   ├── Views/                     # AXAML views
│   ├── ViewModels/                # ReactiveUI ViewModels
│   ├── Services/                  # ServiceMonitoringClient, hub proxy adapters
│   ├── Controls/                  # Custom controls (topology diagram)
│   ├── Converters/                # Value converters
│   └── Helpers/                   # DataGrid auto-fit helper
├── src/SqlAgMonitor.Core/         # Shared business logic (headless)
│   ├── Models/                    # Domain models
│   ├── Services/                  # Service implementations
│   └── Configuration/             # Config model + persistence
├── src/SqlAgMonitor.Service/      # Windows Service + SignalR API
│   ├── Hubs/                      # MonitorHub (real-time push + queries)
│   └── Auth/                      # JWT token service, local user store
├── src/SqlAgMonitor.Installer/    # Graphical service installer (Windows only)
├── scripts/                       # Service publish/install/uninstall scripts
└── tests/SqlAgMonitor.Tests/      # Unit tests
```

## License

[MIT](LICENSE) — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for dependency licenses.
