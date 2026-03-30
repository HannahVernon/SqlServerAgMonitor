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

## Solution Structure

```
SqlAgMonitor.sln
├── src/SqlAgMonitor/                     # Avalonia desktop app (WinExe, .NET 9)
│   ├── Assets/                           # App icon (SVG, PNG, ICO)
│   ├── Controls/TopologyControl.cs       # Animated AG topology diagram
│   ├── Converters/                       # Avalonia value converters
│   ├── Services/
│   │   ├── FileLoggerProvider.cs         # Daily rotating file logger
│   │   ├── LayoutStateService.cs         # Window/grid layout persistence
│   │   └── ThemeService.cs              # Light/dark/high-contrast themes
│   ├── ViewModels/
│   │   ├── MainWindowViewModel.cs        # Root VM: tabs, polling, alert wiring
│   │   ├── MonitorTabViewModel.cs        # Per-group: snapshot → pivot rows
│   │   ├── AddGroupViewModel.cs          # Discovery wizard
│   │   └── SettingsViewModel.cs          # Settings dialog
│   ├── Views/
│   │   ├── MainWindow.axaml(.cs)         # Main window, dynamic DataGrid
│   │   ├── AddGroupWindow.axaml(.cs)     # AG/DAG discovery wizard
│   │   └── SettingsWindow.axaml(.cs)     # Settings dialog
│   ├── App.axaml(.cs)                    # DI bootstrap, tray icon, theme
│   ├── Program.cs                        # Entry point, logging setup
│   └── ViewLocator.cs                    # Convention: *ViewModel → *View
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
│   │   │   └── DuckDbEventHistoryService.cs  # Event persistence
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
| DuckDB.NET.Data | 1.5.0 | Embedded event history database |
| System.Security.Cryptography.ProtectedData | 10.0.5 | Windows DPAPI |

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

## Event History

DuckDB (embedded, serverless) stores structured events at `%APPDATA%\SqlAgMonitor\data\events.duckdb`:

| Table | Purpose |
|---|---|
| `events` | Alert history: timestamp, type, group, replica, database, message, severity, notification flags |
| `error_log` | Application errors with stack traces and context |

Queryable by group name, time range, and count. Used by the HTML export service for scheduled reports.

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
