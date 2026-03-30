# SQL Server AG Monitor

A cross-platform desktop application for real-time monitoring of SQL Server Availability Groups (AGs) and Distributed Availability Groups (DAGs).

## Features

- **Animated topology visualization** — Server cards connected by particle-animated data flow arrows, color-coded by LSN lag (green/yellow/orange/red), solid for synchronous and dashed for asynchronous commit modes
- **Split view** — Topology diagram on top, sortable/resizable data grid on bottom with click-to-filter by replica
- **Multi-tab monitoring** — One tab per AG/DAG, each with independent persistent connections and configurable polling intervals
- **Auto-discovery** — Connect to a server and automatically discover all AGs/DAGs via a guided wizard
- **Alerting** — SMTP email, syslog (RFC 3164), and OS notifications with per-alert thresholds, master cooldown, and mute support
- **Event history** — DuckDB-backed structured event storage with daily rotating log files
- **Scheduled HTML export** — Periodic health reports to a configurable directory
- **Themes** — Light, dark, and high-contrast
- **Secure credentials** — DPAPI encryption on Windows with AES-256 encrypted fallback for other platforms
- **DataGrid features** — Right-click copy (cell/row/all), color-coded sync state, send/redo queue and rate columns, per-tab column layout persistence
- **SQL Server 2014+ compatibility** — Graceful fallback for older instances missing `secondary_lag_seconds`
- **Keyboard shortcuts** — F5 to refresh, standard menu accelerators

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
