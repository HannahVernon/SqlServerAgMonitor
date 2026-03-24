# SQL Server AG Monitor

A cross-platform desktop application for real-time monitoring and management of SQL Server Availability Groups (AGs) and Distributed Availability Groups (DAGs).

## Features

- **Animated topology visualization** — Node cards connected by particle-animated data flow arrows, color-coded by LSN lag (green/yellow/orange/red), solid for synchronous and dashed for asynchronous connections
- **Split view** — Topology diagram on top, sortable data grid on bottom with click-to-filter
- **Full control** — Failover, force failover, sync/async mode toggle, suspend/resume with confirmation dialogs
- **Multi-tab monitoring** — One tab per AG/DAG, each with independent persistent connections and configurable polling intervals
- **Auto-discovery** — Connect to a server and automatically discover all AGs/DAGs
- **Alerting** — In-app, OS-level, SMTP email, and syslog notifications with per-alert thresholds, master cooldown, and mute support
- **Event history** — DuckDB-backed structured event storage with plain text error logs
- **Scheduled HTML export** — Periodic health reports to a configurable path
- **System tray** — Minimize to tray with background monitoring and tray icon health indicator
- **Themes** — Light, dark, and high-contrast
- **Secure credentials** — OS credential store (DPAPI/Keychain/libsecret) with AES-256 encrypted fallback

## Technology Stack

| Component | Technology |
|---|---|
| UI Framework | Avalonia UI 11.x |
| .NET Version | .NET 9 |
| MVVM Framework | ReactiveUI |
| SQL Client | Microsoft.Data.SqlClient |
| Event Storage | DuckDB |
| Configuration | JSON (AppData) |

## Building

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
├── src/SqlAgMonitor/              # Avalonia desktop app
│   ├── Views/                     # AXAML views
│   ├── ViewModels/                # ReactiveUI ViewModels
│   ├── Controls/                  # Custom controls (topology diagram)
│   └── Converters/                # Value converters
├── src/SqlAgMonitor.Core/         # Shared business logic
│   ├── Models/                    # Domain models
│   ├── Services/                  # Service implementations
│   └── Configuration/             # Config model + persistence
└── tests/SqlAgMonitor.Tests/      # Unit tests
```

## License

MIT
