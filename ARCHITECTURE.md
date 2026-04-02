# Architecture

## Overview

SQL Server AG Monitor is a .NET 9 desktop application built on Avalonia UI and ReactiveUI. It polls SQL Server DMVs on a timer, compares each snapshot to the previous one to detect state transitions, and publishes alerts through multiple channels.

```
┌──────────────────────────────────────────────────────────────────┐
│  Avalonia UI Layer (SqlAgMonitor)                                │
│  ┌──────────────┐  ┌──────────────┐  ┌────────────────────────┐  │
│  │ MainWindow   │  │ Topology     │  │ DataGrid (dynamic      │  │
│  │ (tabs, menu, │  │ Control      │  │ columns per replica)   │  │
│  │  status bar) │  │ (animated)   │  │                        │  │
│  └──────┬───────┘  └──────┬───────┘  └───────────┬────────────┘  │
│         │                 │                      │               │
│  ┌──────▼─────────────────▼──────────────────────▼────────────┐  │
│  │ MainWindowViewModel / MonitorTabViewModel                  │  │
│  │ (ReactiveUI, Observable subscriptions, Dispatcher.Post)    │  │
│  └──────┬─────────────────────────────────────────────────────┘  │
└─────────┼────────────────────────────────────────────────────────┘
          │  IObservable<MonitoredGroupSnapshot>
┌─────────▼───────────────────────────────────────────────────────┐
│  Core Layer (SqlAgMonitor.Core)                                 │
│                                                                 │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────────┐  │
│  │ AgMonitor   │  │ DagMonitor  │  │ AlertEngine             │  │
│  │ Service     │  │ Service     │  │ (snapshot diff →        │  │
│  │ (timer →    │  │ (timer →    │  │  cooldown → mute →      │  │
│  │  poll →     │  │  poll per   │  │  publish)               │  │
│  │  snapshot)  │  │  member)    │  │                         │  │
│  └──────┬──────┘  └──────┬──────┘  └──────┬──────────────────┘  │
│         │                │                │                     │
│  ┌──────▼────────────────▼──────┐  ┌──────▼──────────────────┐  │
│  │ ReconnectingConnection       │  │ Notification Channels   │  │
│  │ Wrapper (lease pattern,      │  │ ┌───────┐ ┌──────────┐  │  │
│  │ background reconnect,        │  │ │ SMTP  │ │ Syslog   │  │  │
│  │ exponential backoff)         │  │ │ Email │ │ RFC 5424 │  │  │
│  └──────┬───────────────────────┘  │ └───────┘ └──────────┘  │  │
│         │                          │ ┌──────────────────────┐│  │
│  ┌──────▼──────┐                   │ │ OS Notifications     ││  │
│  │ SqlConnection│                  │ └──────────────────────┘│  │
│  │ Service     │                   └─────────────────────────┘  │
│  └─────────────┘                                                │
│                                                                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐   │
│  │ DuckDB Event │  │ Credential   │  │ JSON Configuration   │   │
│  │ History      │  │ Store        │  │ Service              │   │
│  │ Service      │  │ (DPAPI/AES)  │  │ (%APPDATA%)          │   │
│  └──────────────┘  └──────────────┘  └──────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

## Dual-Mode Architecture

The desktop app supports two operating modes:

### Standalone Mode (default)
Direct SQL Server connections via `MonitoringCoordinator` → `AgMonitorService` / `DagMonitorService`. DuckDB stores event history locally. This is the original architecture shown in the diagram above.

### Service-Client Mode
When `Service.Enabled = true` in settings, the app connects to a remote `SqlAgMonitor.Service` instance via SignalR:

```
┌──────────────────────┐          SignalR           ┌──────────────────────────┐
│  Desktop App          │ ◄═══════════════════════► │  SqlAgMonitor.Service     │
│                       │   OnSnapshotReceived       │                          │
│  ServiceMonitoring    │   OnAlertFired             │  MonitoringWorker        │
│  Client               │                           │  (IHostedService)        │
│       │               │   GetSnapshotHistory       │       │                  │
│       ├─► MonitorTabs │   GetAlertHistory          │       ├─► AgMonitor      │
│       ├─► Alerts      │   GetSnapshotFilters       │       ├─► DagMonitor     │
│       └─► Statistics  │   ExportToExcel            │       ├─► AlertEngine    │
│                       │                           │       └─► DuckDB         │
│  HubSnapshotQuery ◄───┤                           │                          │
│  HubEventQuery    ◄───┤   JWT Auth                 │  Kestrel (port 58432)   │
└──────────────────────┘                            └──────────────────────────┘
```

Both modes share the same ViewModels — `IMonitoringCoordinator` abstracts the data source. `HubSnapshotQueryService` and `HubEventQueryService` implement the same `ISnapshotQueryService` / `IEventQueryService` interfaces, delegating to SignalR hub methods instead of local DuckDB.

### Service REST Endpoints

In addition to the SignalR hub, the service exposes REST endpoints:

| Method | Endpoint | Purpose |
|--------|----------|---------|
| POST | `/api/auth/login` | Authenticate and receive a JWT token |
| POST | `/api/auth/setup` | Create the initial admin account (first-run only) |
| GET | `/api/config/export` | Export the service's current configuration (credentials redacted) |
| POST | `/api/config/import` | Import configuration (additive merge — adds groups, alerts, email, syslog settings without removing existing ones) |

### TLS Certificate Pinning

When TLS is enabled, the `ServiceMonitoringClient` validates the server certificate chain. If the certificate is untrusted (e.g., self-signed), `LoginAsync` and `ConnectAsync` accept a `trustedCertThumbprint` parameter. The client compares the server certificate's SHA-256 thumbprint against the pinned value and allows the connection if they match. The SignalR hub connection also uses the pinned thumbprint for ongoing communication.

## Solution Structure

```
SqlAgMonitor.sln
├── src/SqlAgMonitor/                     # Avalonia desktop app (WinExe, .NET 9)
│   ├── Assets/                           # App icon (SVG, PNG, ICO)
│   ├── Controls/
│   │   ├── TopologyControl.cs            # Animated AG topology diagram
│   │   └── NotificationOverlay.cs        # In-app toast notification overlay
│   ├── Converters/                       # Avalonia value converters
│   ├── Services/
│   │   ├── FileLoggerProvider.cs         # Daily rotating file logger
│   │   ├── LayoutStateService.cs         # Window/grid layout persistence
│   │   ├── ThemeService.cs              # Light/dark/high-contrast themes
│   │   ├── ServiceMonitoringClient.cs   # SignalR client for service-client mode
│   │   ├── HubSnapshotQueryService.cs   # ISnapshotQueryService adapter → hub methods
│   │   └── HubEventQueryService.cs      # IEventQueryService adapter → hub methods
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs        # Root VM: tabs, polling, alert wiring
│   │   ├── MonitorTabViewModel.cs        # Per-group: snapshot → pivot rows
│   │   ├── IMonitoringCoordinator.cs    # Interface: standalone or service-client mode
│   │   ├── MonitoringCoordinator.cs     # Direct SQL monitoring (standalone)
│   │   ├── AlertHistoryViewModel.cs      # Alert history tab (event grid)
│   │   ├── StatisticsViewModel.cs        # Statistics window (charts, export)
│   │   ├── AddGroupViewModel.cs          # Discovery wizard
│   │   ├── DagMemberConnectionVm.cs      # Per-member connection state for Add Group wizard
│   │   └── SettingsViewModel.cs          # Settings dialog
│   ├── Views/
│   │   ├── MainWindow.axaml(.cs)         # Main window, tab control, dynamic DataGrid
│   │   ├── StatisticsWindow.axaml(.cs)   # Statistics & trends (View → Statistics…)
│   │   ├── AddGroupWindow.axaml(.cs)     # AG/DAG discovery wizard
│   │   ├── SettingsWindow.axaml(.cs)     # Settings dialog
│   │   ├── CertificateTrustDialog.axaml(.cs) # TLS cert trust/pin dialog for untrusted service certs
│   │   └── MigrationDialog.axaml(.cs)    # Config migration dialog (local → service push)
│   ├── Helpers/
│   │   └── DataGridAutoFitHelper.cs      # Double-click column separator to auto-fit
│   ├── App.axaml(.cs)                    # DI bootstrap, tray icon, theme
│   ├── Program.cs                        # Entry point, logging setup
│   └── ViewLocator.cs                    # Convention: *ViewModel → *View
│
├── src/SqlAgMonitor.Service/            # Windows Service + SignalR API (.NET 9)
│   ├── Program.cs                       # Kestrel, SignalR, JWT auth, UseWindowsService()
│   ├── MonitoringWorker.cs              # IHostedService — headless coordinator + hub push
│   ├── Hubs/
│   │   ├── MonitorHub.cs               # SignalR hub (6 query methods + push callbacks)
│   │   └── ExcelExporter.cs            # ClosedXML xlsx generation for hub export
│   ├── Auth/
│   │   ├── JwtTokenService.cs          # 512-bit signing key, auto-generated, persisted to AppData
│   │   └── UserStore.cs                # File-based JSON user store with bcrypt hashing
│   └── appsettings.json                # Port, auth config, logging levels
│
├── src/SqlAgMonitor.Installer/          # Graphical service installer (.NET 9, Windows only)
│   ├── Program.cs                       # Entry point, elevation check, /uninstall flag
│   ├── UninstallHandler.cs              # Silent uninstall for Add/Remove Programs
│   ├── ViewModels/
│   │   └── InstallerViewModel.cs        # 6-step wizard state machine + install pipeline
│   ├── Views/
│   │   └── InstallerWindow.axaml(.cs)   # Multi-step wizard UI
│   └── app.manifest                     # requireAdministrator elevation
│
├── src/SqlAgMonitor.Core/               # Business logic (classlib, .NET 9)
│   ├── Configuration/
│   │   ├── AppConfiguration.cs           # All config models
│   │   └── JsonConfigurationService.cs   # JSON file persistence
│   ├── Models/
│   │   ├── AvailabilityGroupInfo.cs      # AG snapshot with replicas
│   │   ├── ReplicaInfo.cs                # Replica-level state (role, health)
│   │   ├── DatabaseReplicaState.cs       # DB-level state (LSN, queues, sync)
│   │   ├── DistributedAgInfo.cs          # DAG with member AGs
│   │   ├── MonitoredGroupSnapshot.cs     # Point-in-time observation
│   │   ├── DatabasePivotRow.cs           # Pivoted grid row (one per DB)
│   │   ├── AlertEvent.cs                 # Alert with metadata
│   │   ├── HealthLevel.cs                # InSync/SlightlyBehind/Moderate/Danger
│   │   └── LsnHelper.cs                  # LSN parsing and comparison
│   ├── Services/
│   │   ├── Alerting/AlertEngine.cs       # Snapshot diff → alert generation
│   │   ├── Connection/
│   │   │   ├── ReconnectingConnectionWrapper.cs  # Lease pattern + reconnect
│   │   │   └── SqlConnectionService.cs   # Connection string builder
│   │   ├── Credentials/
│   │   │   ├── DpapiCredentialStore.cs   # Windows DPAPI encryption
│   │   │   ├── AesCredentialStore.cs     # AES-256-GCM + PBKDF2 fallback
│   │   │   └── PlatformCredentialStoreFactory.cs
│   │   ├── History/
│   │   │   ├── DuckDbConnectionManager.cs    # Shared DuckDB connection + schema migrations
│   │   │   ├── DuckDbEventStore.cs           # Alert event write/query (IEventRecorder, IEventQueryService)
│   │   │   ├── DuckDbSnapshotStore.cs        # Snapshot write/query (ISnapshotRecorder, ISnapshotQueryService)
│   │   │   └── MaintenanceScheduler.cs       # Periodic pruning + summarization timer
│   │   ├── Monitoring/
│   │   │   ├── AgMonitorService.cs       # AG polling (timer → DMV → snapshot)
│   │   │   ├── DagMonitorService.cs      # DAG polling (per-member connections)
│   │   │   └── AgDiscoveryService.cs     # AG/DAG auto-discovery
│   │   ├── Notifications/
│   │   │   ├── SmtpEmailNotificationService.cs
│   │   │   ├── SyslogService.cs          # RFC 5424 over UDP/TCP
│   │   │   └── OsNotificationService.cs  # Desktop toast notifications
│   │   └── Export/HtmlExportService.cs   # Scheduled HTML reports
│   └── ServiceCollectionExtensions.cs    # DI registration
│
└── tests/SqlAgMonitor.Tests/            # xUnit tests
```

## Key Dependencies

| Package | Version | Purpose |
|---|---|---|
| Avalonia | 11.3.9 | Cross-platform UI framework |
| Avalonia.ReactiveUI | 11.3.9 | MVVM integration |
| ReactiveUI | 20.1.1 | Reactive MVVM (commands, bindings, observables) |
| System.Reactive | 6.1.0 | Observable pipelines (Timer, SelectMany, Where) |
| Microsoft.Data.SqlClient | 7.0.0 | SQL Server connectivity |
| DuckDB.NET.Data.Full | 1.5.0 | Embedded event history and statistics database |
| LiveChartsCore.SkiaSharpView.Avalonia | — | Trend charts in Statistics window |
| ClosedXML | — | Excel export (.xlsx) from Statistics window |
| System.Security.Cryptography.ProtectedData | 10.0.5 | Windows DPAPI |
| Microsoft.AspNetCore.SignalR.Client | 9.0.6 | SignalR client for service-client mode |
| Microsoft.AspNetCore.Authentication.JwtBearer | 9.0.6 | JWT authentication for service API |
| BCrypt.Net-Next | 4.0.3 | Password hashing for service user store |
| Microsoft.Extensions.Hosting.WindowsServices | 9.0.6 | Windows Service host integration |

## Data Flow

### Poll Cycle

```
Observable.Timer(0, interval)
    │
    ▼
SelectMany → PollGroupAsync(blocking: false)
    │
    ├── TryAcquireAsync() → null if busy → filtered by .Where(!=null)
    │
    ├── Lease acquired
    │   ├── Query sys.dm_hadr_availability_replica_states
    │   ├── Query sys.dm_hadr_database_replica_states
    │   ├── Build MonitoredGroupSnapshot
    │   └── Dispose lease (release semaphore)
    │
    ▼
_snapshots.OnNext(snapshot)
    │
    ├──► MainWindowViewModel.OnSnapshotReceived()
    │       ├── Dispatcher.UIThread.Post()
    │       ├── MonitorTabViewModel.ApplySnapshot()
    │       │       ├── Update OverallHealth, IsConnected
    │       │       ├── Transform → DatabasePivotRow[]
    │       │       └── Notify ReplicaColumnsChanged
    │       └── AlertEngine.EvaluateSnapshot(current, previous)
    │               ├── Compare state transitions
    │               ├── Check enabled/muted/cooldown
    │               └── _alertSubject.OnNext(alert)
    │                       ├── Status bar update
    │                       ├── DuckDB event record
    │                       ├── SMTP email
    │                       └── Syslog
    │
    └──► TopologyControl.Render()
            ├── Node cards (role, health dot, mode)
            ├── Arrows (color = health, style = sync/async)
            └── Particle animation (33ms timer)
```

### DAG Polling

Distributed AGs require connections to multiple independent SQL Server instances. The `DagMonitorService` maintains a separate `ReconnectingConnectionWrapper` per member AG and polls them concurrently with `Task.WhenAll`:

```
DagMonitorService.PollGroupAsync()
    │
    ├── Query DAG topology from primary connection
    │   (sys.availability_groups WHERE is_distributed=1)
    │
    └── For each member AG (parallel):
            ├── TryAcquireAsync() on member's wrapper
            ├── Query local AG replicas via fn_hadr_distributed_ag_replica()
            ├── Query local AG database states
            └── Dispose lease
```

## Connection Management

### Lease Pattern

The `ReconnectingConnectionWrapper` ensures exclusive access to a `SqlConnection` through a disposable lease:

```
┌─────────────────────────────────────────────────────────┐
│ ReconnectingConnectionWrapper                           │
│                                                         │
│  SemaphoreSlim(1,1)  ◄── guards all access              │
│                                                         │
│  TryAcquireAsync()   → null if semaphore busy           │
│  AcquireAsync()      → waits for semaphore              │
│                                                         │
│  ConnectionLease : IAsyncDisposable                     │
│  ├── .Connection     → the SqlConnection                │
│  ├── .Invalidate()   → mark broken, start reconnect     │
│  └── .DisposeAsync() → release semaphore                │
│                                                         │
│  ReconnectLoopAsync() (background task)                 │
│  ├── Exponential backoff: 1s, 2s, 4s, 8s, 16s, 32s, 60s │
│  ├── Acquires semaphore with 2s timeout                 │
│  └── Emits ConnectionStateChange on success/failure     │
└─────────────────────────────────────────────────────────┘
```

**Timer polls** use `TryAcquireAsync` (non-blocking) — if the previous poll or a reconnect attempt is in progress, the poll is silently skipped and the Observable chain's `.Where(snapshot != null)` filter drops it. The UI keeps the last good data.

**Manual refresh** (F5) uses `AcquireAsync` (blocking) — the user expects to wait.

## Threading Model

| Work | Thread | Mechanism |
|---|---|---|
| Poll timer tick | Thread pool | `Observable.Timer` schedules on TP |
| SQL query execution | Thread pool | `ExecuteReaderAsync` is async I/O |
| Snapshot emission | Background (caller) | `_snapshots.OnNext()` on poll thread |
| UI binding update | UI thread | `Dispatcher.UIThread.Post()` |
| "Updated Xs ago" timer | UI thread | `Observable.Interval` + `ObserveOn(MainThreadScheduler)` |
| Alert evaluation | Background (caller) | Called from snapshot handler |
| DuckDB writes | Background (caller) | Fire-and-forget from alert handler |
| Config file I/O | Caller thread | Serialized by `lock` |
| Topology animation | UI thread | `DispatcherTimer` at 33ms (~30 fps) |

**Thread safety mechanisms:**
- `SemaphoreSlim(1,1)` — exclusive connection access via lease pattern
- `ConcurrentDictionary` — polling subscriptions and connection wrapper maps
- `lock` — config file I/O, credential store reads/writes
- `Dispatcher.UIThread.Post()` — marshals to Avalonia UI thread

## Credential Storage

Platform-aware factory selects the implementation:

```
PlatformCredentialStoreFactory.Create()
    │
    ├── Windows → DpapiCredentialStore
    │     Uses System.Security.Cryptography.ProtectedData
    │     DataProtectionScope.CurrentUser (machine + user bound)
    │     Storage: %APPDATA%\SqlAgMonitor\credentials\credentials.dat
    │
    └── Other → AesCredentialStore
          AES-256-GCM + PBKDF2 (600,000 iterations)
          Requires master password to unlock
          Storage: %APPDATA%\SqlAgMonitor\credentials\credentials.aes
```

## Configuration

All configuration is stored as JSON at `%APPDATA%\SqlAgMonitor\config.json` (Linux/macOS: `~/.config/SqlAgMonitor/config.json`).

Key sections of `AppConfiguration`:

| Section | Contents |
|---|---|
| `GlobalPollingIntervalSeconds` | Default poll interval (default: 16s) |
| `Theme` | "dark", "light", or "high-contrast" |
| `MonitoredGroups[]` | Per-group: name, type, connections, polling interval, alert overrides |
| `Email` | SMTP server, port, TLS, from/to addresses, credential key |
| `Syslog` | Server, port, protocol (UDP/TCP), facility code |
| `Alerts` | Master cooldown, per-type enable/disable/threshold overrides |
| `Export` | Enable, directory, interval |
| `History` | Auto-prune enabled, max retention days (default 90), max records |

### File Locations

All application data is stored under `%APPDATA%\SqlAgMonitor` (Windows) or `~/.config/SqlAgMonitor` (Linux/macOS):

| File | Purpose |
|---|---|
| `config.json` | Application configuration (connections, alerts, email, export, theme) |
| `layout.json` | Window positions, sizes, grid column widths, statistics filter state |
| `data\events.duckdb` | DuckDB database: alert history, error log, snapshot metrics (raw + hourly + daily rollups) |
| `credentials\credentials.dat` | Encrypted credential store (Windows DPAPI) |
| `credentials\credentials.aes` | Encrypted credential store (Linux/macOS AES) |
| `logs\` | Rolling application log files |

## Event History

DuckDB (embedded, serverless) stores structured events at `%APPDATA%\SqlAgMonitor\data\events.duckdb`:

| Table | Purpose |
|---|---|
| `events` | Alert history: timestamp, type, group, replica, database, message, severity, notification flags |
| `error_log` | Application errors with stack traces and context |
| `snapshots` | Raw per-poll metric snapshots (group, replica, database, queues, lag, LSN diff, sync state) |
| `snapshot_hourly` | Hourly rollup summaries (MIN/MAX/AVG aggregates, LAST() for state columns, BOOL_OR for booleans) |
| `snapshot_daily` | Daily rollup summaries (weighted averages computed from hourly data) |

Queryable by group name, time range, and count. Used by the HTML export service for scheduled reports.

### Statistics Data Pipeline

The statistics subsystem captures per-poll metrics and maintains a three-tier summarization pipeline for efficient long-term trend analysis.

**Snapshot capture:** `RecordSnapshotAsync` is called from `OnSnapshotReceived` every poll cycle. Each row in the `snapshots` table records the group, replica, database, and all key metrics (log send queue, redo queue, secondary lag, log block difference, sync state, suspended flag).

**Summarization:** An hourly timer invokes `SummarizeSnapshotsAsync`, which performs two rollup stages:

```
snapshots (raw)                    snapshot_hourly                 snapshot_daily
┌──────────────────────┐          ┌──────────────────────┐       ┌──────────────────────┐
│ Per-poll rows         │  ──►    │ GROUP BY             │  ──►  │ Weighted average     │
│ (full resolution)     │  hourly │ date_trunc('hour')   │ daily │ from hourly data     │
│                       │  rollup │                      │rollup │                      │
│ Retained: 48h         │         │ Aggregates:          │       │ Retained: 730d       │
│                       │         │  MIN/MAX/AVG (nums)  │       │                      │
│                       │         │  LAST() (states)     │       │                      │
│                       │         │  BOOL_OR (booleans)  │       │                      │
│                       │         │ Retained: 90d        │       │                      │
└──────────────────────┘          └──────────────────────┘       └──────────────────────┘
```

**Pruning** runs after each summarization pass:
- Raw snapshots older than **48 hours** are deleted
- Hourly summaries older than **90 days** are deleted
- Daily summaries older than **730 days** are deleted
- All thresholds are configurable via `SnapshotRetentionSettings` in the app configuration

**Query layer:** `GetSnapshotDataAsync` auto-selects the appropriate data tier based on the requested time range:
- ≤ 48 hours → `snapshots` (raw)
- ≤ 90 days → `snapshot_hourly`
- \> 90 days → `snapshot_daily`

**UI:** The Statistics window (`StatisticsWindow` / `StatisticsViewModel`) uses **LiveCharts2** (`LiveChartsCore.SkiaSharpView.Avalonia`) to render four line charts (Log Send Queue, Redo Queue, Secondary Lag, Log Block Difference). Excel export uses **ClosedXML** to generate `.xlsx` files.

## SQL Query Safety

All SQL queries follow strict parameterization rules to prevent SQL injection:

### SQL Server (AgControlService, AgMonitorService, DagMonitorService)

- **Read-only DMV queries** use static `const string` SQL with no parameters needed — they query only system catalog views (`sys.availability_groups`, `sys.dm_hadr_*`, etc.) with no user-supplied identifiers.
- **DDL statements** (`ALTER AVAILABILITY GROUP`, `ALTER DATABASE`) cannot use parameterized identifiers. These pass object names as `SqlParameter` values and use SQL Server's built-in `QUOTENAME()` function server-side to safely escape identifiers, with the result executed via `EXEC(@sql)`.

### DuckDB (DuckDbEventStore / DuckDbSnapshotStore)

- **INSERT/DELETE statements** use named DuckDB parameters (`$param_name` with `DuckDBParameter` objects) for all data values.
- **Dynamic WHERE clauses** are assembled from code-controlled static string fragments that contain parameter placeholders (e.g., `"group_name = $group_name"`). The fragments are joined with `" AND "` and prepended with `"WHERE "`. No user-supplied values are interpolated into the SQL structure — only the structural keywords (`WHERE`, `AND`) and pre-defined column/parameter names appear in the assembled string. This is a standard dynamic query-building pattern used when filter criteria are optional.
- **Table name selection** in `GetSnapshotDataAsync` chooses between `snapshots`, `snapshot_hourly`, and `snapshot_daily` based on the requested time range. These are hard-coded string literals selected by an internal tier enum, not user input.
- **INTERVAL literals** in pruning queries use `string.Format` with integer retention values from application configuration. DuckDB does not support parameterized `INTERVAL` syntax, and integers cannot contain SQL syntax.

## LSN Comparison

SQL Server stores LSNs as `numeric(25,0)` with the structure:

```
VLF_sequence (10 digits) × 10^15 + Block_offset (10 digits) × 10^5 + Slot (5 digits)
```

`LsnHelper.ComputeLogBlockDiff()` strips the slot (irrelevant at block level), then computes the absolute difference. The result maps to health levels:

| Difference | Health Level | Meaning |
|---|---|---|
| ≤ 1,000,000 | InSync | Within ~1 MB — fully caught up |
| ≤ 100,000,000 | SlightlyBehind | Within ~100 MB — minor transient lag |
| ≤ 10,000,000,000 | ModeratelyBehind | Within same VLF range — moderate lag |
| > 10,000,000,000 | DangerZone | VLF boundary crossed or massive lag |

Display format: `VVVVVVVV:BBBBBBBB` (hex VLF:Block), matching SQL Server's standard LSN display.

## Error Handling Strategy

Services use one of six error handling patterns, chosen based on how critical the operation is:

| Pattern | When to Use | Example |
|---|---|---|
| **Silent swallow** | Logging infrastructure — recursive failures must not crash the app | `FileErrorLogger.LogError()`, `FileLoggerProvider.Log()` |
| **Swallow + default** | Best-effort persistence where corrupted data has a safe fallback | `LayoutStateService.Load()` (returns `new()` on corrupt JSON) |
| **Log + return false** | Test/validation operations where the caller checks success | `SqlConnectionService.TestConnectionAsync()`, `SyslogService.TestConnectionAsync()` |
| **Log + continue** | Fire-and-forget side effects that must not block the primary workflow | `SmtpEmailNotificationService.SendAlertEmailAsync()`, `DuckDbEventStore.RecordEventAsync()` |
| **Log + re-throw** | Critical operations where the caller must know about failure | `SqlConnectionService.GetConnectionAsync()`, `ReconnectingConnectionWrapper.AcquireAsync()` |
| **Validate + throw** | Configuration/input validation errors requiring user correction | `SmtpEmailNotificationService.ValidateSettings()`, `AesCredentialStore.Unlock()` |

### Guidelines

- **Semaphore-guarded methods** always release in `finally` to prevent deadlocks.
- **`OperationCanceledException`** is caught separately and used to exit gracefully without logging (it's expected during shutdown).
- **Reconnection** uses exponential backoff (1s → 2s → 4s → 8s → 16s → 32s → 60s cap) with specific exception handling: `OperationCanceledException` exits, all other exceptions increment the attempt counter.
- All services log via `ILogger<T>`. The decision to swallow vs. throw is documented with an inline comment at each catch site explaining the rationale.
