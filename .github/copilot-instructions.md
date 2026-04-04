# Copilot Instructions — SQL Server AG Monitor

## Documentation Sync Rule

Whenever you make a code change that affects any of the following, you **must** update the corresponding documentation files in the same commit:

| What changed | Update these files |
|---|---|
| New/renamed/removed files or folders | `ARCHITECTURE.md` (file tree + relevant sections) |
| New/changed features, menu items, UI behavior | `FEATURE-GUIDE.md` |
| New/changed NuGet dependencies | `licence.md` (package name, version, copyright, license) |
| Build requirements, project structure, quick-start steps | `README.md` |
| Contribution workflow, coding standards | `CONTRIBUTING.md` |

**Do not defer documentation updates to a follow-up commit.** Treat docs as part of the definition of done for every change.

## Win32 Interop Safety Rules

- All Win32 handles (`HANDLE`) must be closed in `finally` blocks
- `IntPtr` buffers must be freed in `finally` blocks
- Passwords must never be logged — redact as `***REDACTED***`

## NuGet Vulnerability Scanning

Whenever a NuGet package is added, updated, or its version changes, run `dotnet list package --vulnerable` from the solution root and verify zero vulnerabilities before committing. If a vulnerability is found, check for a patched version or flag it to the user for a decision.
