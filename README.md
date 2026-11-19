# Agent Local Monitor

Lightweight Windows dashboard for monitoring local Claude, Codex, and AGY agents, with live process status, activity history, and a compact UI.

## Windows

The `release/windows/` folder contains the tested .NET 9 WPF application and its local Python monitoring helper. Keep the files and `monitor/` subdirectory together. Run `AgentLocalMonitor.exe` on Windows with .NET 9 Desktop Runtime and Python available. Optional: `--data-dir "C:\path\to\monitor-profile"` for a dedicated application profile.

## Build

`dotnet build src/AgentLocalMonitor/AgentLocalMonitor.csproj -c Release`

## Scope and limitations

This application observes local agent processes and existing monitor data; it does not start AI jobs or require AI API calls to generate test activity. The current release is a Windows candidate with focused GUI smoke testing, not a claim of complete acceptance. Do not commit local monitor databases, logs, profiles or credentials.
