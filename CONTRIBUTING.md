# Contributing to SQL Server AG Monitor

Thank you for your interest in contributing! This document explains how to get started, what we expect from contributions, and how we work together.

## Code of Conduct

This project adopts the [Contributor Covenant Code of Conduct v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/). By participating, you agree to uphold its standards.

**In short:** Be respectful, be constructive, be welcoming. Harassment, trolling, and personal attacks are not tolerated.

Instances of unacceptable behavior may be reported by:

- Opening a [GitHub Issue](https://github.com/HannahVernon/SqlServerAgMonitor/issues)
- Contacting the maintainer via [GitHub (@HannahVernon)](https://github.com/HannahVernon)
- Emailing [hannah@mvct.com](mailto:hannah@mvct.com)

All reports will be reviewed promptly and handled with discretion.

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (pinned via `global.json`)
- **For service development:** ASP.NET Core runtime (included in .NET 9 SDK)
- A SQL Server instance with at least one Availability Group (for integration testing)
- Windows, macOS, or Linux

### Building

```bash
git clone https://github.com/HannahVernon/SqlServerAgMonitor.git
cd SqlServerAgMonitor
dotnet restore
dotnet build
```

### Running

```bash
dotnet run --project src/SqlAgMonitor
```

### Project Structure

| Path | Description |
|---|---|
| `src/SqlAgMonitor/` | Avalonia UI desktop app (views, view models, services) |
| `src/SqlAgMonitor.Core/` | Core library (monitoring, alerting, DuckDB history, configuration) |
| `src/SqlAgMonitor.Service/` | Windows Service + SignalR API |
| `tests/` | Unit and integration tests |

The solution contains three projects: `SqlAgMonitor` (desktop app), `SqlAgMonitor.Core` (shared logic), and `SqlAgMonitor.Service` (Windows Service). All build with `dotnet build`.
| [ARCHITECTURE.md](ARCHITECTURE.md) | Detailed architecture overview |
| [FEATURE-GUIDE.md](FEATURE-GUIDE.md) | Feature documentation |
| [SERVICE-PLAN.md](SERVICE-PLAN.md) | Planned Windows Service + SignalR API |

## How to Contribute

### Reporting Bugs

- Search [existing issues](https://github.com/HannahVernon/SqlServerAgMonitor/issues) first to avoid duplicates
- Include the app log from `%APPDATA%\SqlAgMonitor\logs\` (or `~/.local/share/SqlAgMonitor/logs/` on Linux/macOS)
- If the app crashed without an exception in the log, check the Windows Event Log (`Application` source) — SkiaSharp native crashes won't appear in the app log
- Include your OS, .NET version (`dotnet --version`), and SQL Server version

### Suggesting Features

Open an issue with the `enhancement` label. Describe the problem you're trying to solve, not just the solution you envision — there may be a better approach.

### Submitting Pull Requests

1. **Fork and branch** from `dev` (not `main`). Use descriptive branch names: `fix/timestamp-binding`, `feature/export-csv`, etc.
2. **Keep changes focused.** One logical change per PR. Don't mix refactoring with feature work.
3. **Build cleanly.** `dotnet build` must produce 0 errors and 0 warnings.
4. **Test your changes.** Run `dotnet test` and verify manually where applicable.
5. **Update documentation** if your change affects user-visible behavior, configuration, or architecture.
6. **Write a clear commit message.** First line is a concise summary; body explains *why*, not just *what*.

### NuGet Dependencies

All direct NuGet dependencies must be **MIT or Apache-2.0 licensed**. When adding a new package:

1. Verify the license in the `.nuspec` file at `%USERPROFILE%\.nuget\packages\{package}\{version}\`
2. Add the package to `LICENSE` in the repo root with name, version, copyright holder, and license type
3. If the license is more restrictive, discuss in the PR before merging

## Technical Guidelines

### Code Style

- Target **.NET 9** (pinned in `global.json`)
- Follow standard C# conventions; the codebase uses file-scoped namespaces, nullable reference types, and expression-bodied members where readable
- **No commented-out code** in commits
- Comments should explain *why*, not *what*

### Avalonia-Specific Patterns

These patterns were learned through hard-won debugging — please follow them:

- **DataGrid column widths:** Never use `DataGridLengthUnitType.Star`. Use `DataGridLength.Auto` with `MinWidth` for initial sizing, save/restore as raw pixel values. See `copilot-instructions.md` for the full rationale.
- **LiveCharts dispose safety:** Always set `DataContext = null` before disposing a ViewModel that owns chart series. Clear all series/axes arrays in `Dispose()`. The LiveCharts render loop runs on the composition thread and will access disposed SkiaSharp objects otherwise, causing a native crash with no managed exception.
- **DuckDB parameter names:** Avoid DuckDB reserved keywords (e.g., `timestamp`, `select`, `table`) as parameter names. DuckDB.NET silently skips parameters whose names conflict with keywords — no exception, just default/null values.

### Branching

- `dev` — active development branch; PRs target here
- `main` — stable releases only
- Feature branches are deleted after merge

### Service Protocol Versioning

The service and desktop client negotiate compatibility via `GET /api/version`, which returns a `protocolVersion` integer. The shared constant lives in `SqlAgMonitor.Core/ServiceProtocol.cs`.

**You must increment `ServiceProtocol.Current` when making any of these changes:**

- Adding, removing, or renaming a REST endpoint
- Changing the request or response shape of an existing endpoint
- Adding, removing, or renaming a SignalR hub method or push event
- Changing the parameter types or return types of a hub method
- Any change that would cause an older client to fail against the new service (or vice versa)

**You do NOT need to bump the version for:**

- Adding optional fields to an existing response (additive, non-breaking)
- Bug fixes that don't change the API contract
- Internal refactoring

The client checks the protocol version at three points: before initial login, during Test Connection in Settings, and after every SignalR reconnect. A mismatch shows a clear upgrade prompt to the user. See [SERVICE-PROTOCOL.md](SERVICE-PROTOCOL.md) for the full API reference.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE) that covers this project.
