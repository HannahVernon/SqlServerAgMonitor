# Windows Service + SignalR API — Design Plan

> **Status:** Implemented — Phases 1–4 complete. See [Architecture](ARCHITECTURE.md) for technical details and scripts/ for deployment.

## Goal

Add a Windows Service (`SqlAgMonitor.Service`) that runs headless monitoring (polling, alerting, notifications, DuckDB persistence, HTML export) and exposes a **SignalR API** so the desktop app (and a future Android app) can connect remotely for real-time data and historical statistics.

## Architecture

```
SqlAgMonitor.sln
├── src/SqlAgMonitor/                  # Avalonia desktop app (dual-mode: standalone or service client)
├── src/SqlAgMonitor.Core/             # Shared business logic (headless)
├── src/SqlAgMonitor.Service/          # NEW — Windows Service + SignalR API
│   ├── Program.cs                     # Host builder, Kestrel, SignalR, UseWindowsService()
│   ├── MonitoringWorker.cs            # IHostedService — headless coordinator
│   ├── Hubs/
│   │   └── MonitorHub.cs             # SignalR hub — real-time push + request/response
│   ├── Auth/
│   │   ├── JwtTokenService.cs        # JWT generation/validation
│   │   ├── UserStore.cs              # Local user store (bcrypt hashed passwords)
│   │   └── WindowsAuthHandler.cs     # Negotiate/NTLM for domain environments
│   └── SqlAgMonitor.Service.csproj
└── tests/SqlAgMonitor.Tests/
```

All core services (`AgMonitorService`, `DagMonitorService`, `AlertEngine`, `AlertDispatcher`, `MaintenanceScheduler`, DuckDB stores, `HtmlExportService`, email, syslog) already live in `SqlAgMonitor.Core` and are fully headless. The only UI-coupled piece is `MonitoringCoordinator` (uses `Dispatcher.UIThread` and `MonitorTabViewModel`), which gets a headless equivalent in the service.

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **API protocol** | SignalR (WebSocket + fallback) | Real-time push for live snapshots/alerts + request/response for history. Android-friendly (Java client). |
| **Desktop app mode** | Dual-mode — standalone when no service, API client when service is configured | User configures service URL + port in Settings; without it, app monitors directly as today. |
| **Service discovery** | Manual toggle in Settings — specify service host:port | Service and app can be on different machines. |
| **Authentication** | JWT bearer tokens | Supports both Windows Auth (Negotiate) and local user/password (bcrypt). |
| **Default port** | 58432 (user-configurable) | Dynamic range, avoids common conflicts. |
| **Data ownership** | Service owns DuckDB exclusively when running | Desktop app queries all data through SignalR when connected to service. |

## SignalR Hub Design

**MonitorHub** — single hub for all communication:

### Server → Client (real-time push)

| Method | Payload | When |
|--------|---------|------|
| `OnSnapshotReceived` | `(groupName, snapshot)` | Every poll cycle |
| `OnAlertFired` | `(alertEvent)` | When an alert triggers |
| `OnConnectionStateChanged` | `(groupName, state)` | Connect/disconnect/reconnect events |

### Client → Server (request/response via hub invocation)

| Method | Returns | Purpose |
|--------|---------|---------|
| `GetMonitoredGroups()` | List of AG/DAG configurations | Populate tab list |
| `GetCurrentSnapshots()` | Latest snapshot per group | Initial state on connect |
| `GetSnapshotHistory(filters)` | Historical data points | Statistics/Trends window |
| `GetSnapshotFilters()` | Distinct group/replica/database values | Filter dropdowns |
| `GetAlertHistory(timeRange)` | Alert events | Alert History panel |
| `ExportToExcel(filters)` | `byte[]` | Excel file download |

## Desktop App Dual-Mode

### Standalone mode (no service configured)

Works exactly as today — direct SQL connections, local DuckDB, local alerting. `MonitoringCoordinator` drives everything.

### Service client mode (service URL configured in Settings)

- No direct SQL connections — all data comes through SignalR
- `ServiceMonitoringClient` replaces `MonitoringCoordinator`
- Subscribes to hub push events → updates ViewModels
- Statistics window calls hub methods instead of querying DuckDB directly
- Alerting/email/syslog handled by service — desktop only shows in-app notifications

## Authentication Flow

1. Desktop app connects to `ws://host:58432/monitor`
2. **Windows Auth path:** Negotiate handshake → JWT issued → used for subsequent requests
3. **Local user path:** Client calls `Authenticate(username, password)` → service validates against bcrypt-hashed store → returns JWT
4. JWT included in all subsequent SignalR messages via query string or headers
5. Token refresh handled automatically before expiry

## Implementation Phases

### Phase 1: Service Foundation

| Task | Description |
|------|-------------|
| Create project | Worker Service with Kestrel + SignalR + `Microsoft.Extensions.Hosting.WindowsServices` |
| MonitoringWorker | `IHostedService` subscribing to snapshot observables → AlertEngine → AlertDispatcher → DuckDB recording |
| Wire DI | `Program.cs` with `Host.CreateDefaultBuilder`, `AddSqlAgMonitorCore()`, Kestrel on port 58432, SignalR, file logging |

### Phase 2: SignalR API

| Task | Description |
|------|-------------|
| MonitorHub | SignalR hub with push methods + request/response invocations |
| JWT + Auth | JWT token service, local user store (bcrypt), Windows Auth handler (Negotiate/NTLM) |
| Snapshot push | Wire MonitoringWorker snapshots → `IHubContext<MonitorHub>` broadcast |
| History queries | Hub methods delegating to `IEventHistoryService` for statistics, filters, alerts, Excel |

### Phase 3: Desktop Client Mode

| Task | Description |
|------|-------------|
| Service settings | Settings UI for service host:port, connection mode toggle, persisted in `config.json` |
| ServiceMonitoringClient | SignalR client that connects to MonitorHub, subscribes to push events, drives ViewModels |
| Statistics adaptation | `StatisticsViewModel` calls hub methods when in service-client mode |
| Alert adaptation | In-app alert notifications from hub push events instead of local AlertEngine |

### Phase 4: Deployment & Documentation

| Task | Description |
|------|-------------|
| Install scripts | PowerShell / `sc.exe` for install/uninstall, `dotnet publish` for self-contained deployment |
| End-to-end test | Service monitors, desktop connects, statistics load, alerts flow, Excel export works |
| Documentation | Updated ARCHITECTURE.md, README, new SERVICE-INSTALL.md |

## Future: Android App

The SignalR hub is designed with cross-platform clients in mind. SignalR has official clients for:

- **.NET** (desktop app — `Microsoft.AspNetCore.SignalR.Client`)
- **Java** (Android — `com.microsoft.signalr`)
- **JavaScript** (web dashboard — `@microsoft/signalr`)

A future Android app would connect to the same `MonitorHub` and receive the same real-time push events.
