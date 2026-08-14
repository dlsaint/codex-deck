# Contributing

Thanks for contributing to Codex Project Center.

## Requirements

- Windows 10 or Windows 11
- .NET Framework 4.x compiler included with Windows
- PowerShell
- Codex Desktop for integration testing

Regenerating the project-owned icon additionally requires Python and Pillow:

```powershell
python -m pip install Pillow
python .\tools\generate_icon.py
```

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## Test

```powershell
.\dist\CodexProjectCenter.exe --self-test .\dist\self-test.json
.\dist\CodexProjectCenter.exe --cache-merge-test .\dist\cache-merge-test.json
.\dist\CodexProjectCenter.exe --title-sync-test .\dist\title-sync-test.json
.\dist\CodexProjectCenter.exe --navigation-event-test .\dist\navigation-event-test.json
```

Diagnostic output belongs in `dist/` or `artifacts/` and must not be committed.

## Pull requests

- Keep task discovery and status processing local by default.
- Do not add telemetry or transmit task content without an explicit design discussion.
- Preserve local, remote and side-conversation behavior.
- Add or update a headless diagnostic for status, cache or navigation changes.
- Do not include personal paths, hostnames, task IDs, session logs or credentials.
- Explain reliance on undocumented Codex behavior when a change uses desktop logs, IPC or UI Automation.
