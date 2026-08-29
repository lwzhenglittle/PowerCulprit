# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

**PowerCulprit** — a Windows battery-drain analyzer. It samples per-process CPU/GPU/IO/memory and system battery + hardware sensors, persists everything to SQLite, then ranks the processes that most correlate with the discharge curve. It is a *correlation-and-attribution* tool, not a per-process wattmeter; UI and reports must say so. The full design contract lives in [init.md](init.md) — treat that as the spec when extending features.

Fixed decisions (do not relitigate): C# / .NET 10 / Windows 10+ / WinUI 3 (Windows App SDK) / LiveCharts2 / SQLite / CommunityToolkit.Mvvm / Intel CPU + Intel iGPU/Arc only. **Do not add NVIDIA, AMD, NVML, nvidia-smi, ADLX, or SMU code paths.**

The .NET SDK is pinned to `10.0.301` via [global.json](global.json).

## Common commands

Run from the repo root.

```powershell
dotnet build                                                              # build whole solution
dotnet test                                                               # all xUnit tests
dotnet test --filter "FullyQualifiedName~PowerCulpritAnalyzerTests"       # one test class
dotnet test --filter "DisplayName~CpuPercent"                             # tests matching a name
dotnet run --project src\PowerCulprit.Cli                                 # CLI default → launches Desktop GUI
dotnet run --project src\PowerCulprit.Cli -- --diagnose                   # print data-source / permission status
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 30s    # sample without GUI
dotnet run --project src\PowerCulprit.Cli -- --headless --duration 30m --interval 2 --db C:\temp\pc.db
dotnet run --project src\PowerCulprit.Desktop                             # launch WinUI 3 GUI directly
```

`--duration` accepts suffixes `ms` / `s` / `m` / `h` (bare number = seconds). `--interval` is clamped to 1–5 seconds. Default DB: `%LocalAppData%\PowerCulprit\powerculprit.db`. Default logs: `%LocalAppData%\PowerCulprit\logs`.

The solution file is [PowerCulprit.slnx](PowerCulprit.slnx) (the new XML solution format) — `dotnet build` picks it up automatically.

## Architecture

Six projects, three layers:

- **Core** ([src/PowerCulprit.Core/](src/PowerCulprit.Core/)) — pure domain. Models (`SystemPowerSample`, `ProcessSample`, `GpuProcessSample`, `HardwareSensorSample`, `SourceStatus`, `CulpritReportItem`), `IMonitoringService` + `MonitoringSnapshot`, and `PowerCulpritAnalyzer` / `CorrelationHelper`. `net10.0`, no Windows targeting — keeps the scorer testable without the WinRT/PDH stack.
- **Storage** ([src/PowerCulprit.Storage/](src/PowerCulprit.Storage/)) — `DatabaseManager`: SQLite schema creation, batched-transaction writes, time-window queries, 7-day retention cleanup. Tables: `system_power_samples`, `process_samples`, `gpu_process_samples`, `hardware_sensor_samples`, `analysis_reports`, `source_status`. Also `net10.0`.
- **Collectors** ([src/PowerCulprit.Collectors/](src/PowerCulprit.Collectors/)) — Windows-only (`net10.0-windows10.0.26100.0`). One collector per data source: `BatteryPowerCollector` (WinRT `AggregateBattery`), `ProcessResourceCollector` (process IO + foreground via `NativeMethods`), `WindowsGpuEngineCollector` (PDH `GPU Engine`), `LibreHardwareMonitorCollector`, `IntelCpuPowerCollector`, `IntelGpuPowerCollector` (the Intel collectors *derive* from LHM samples + GPU Engine — they don't talk to MSR/RAPL/Level Zero directly in the MVP). `MonitoringService` orchestrates them and is the single consumer of `DatabaseManager`.
- **Desktop** ([src/PowerCulprit.Desktop/](src/PowerCulprit.Desktop/)) — WinUI 3 `WinExe` (unpackaged, `WindowsAppSDKSelfContained=true`). `App.xaml.cs` wires DI, `MainWindow` + `MainPage` host the dashboard, `MainViewModel` binds to `MonitoringService`, and `TrayManager` provides the system tray via Win32 interop. Closing the window hides it; only the tray `Exit` shuts down.
- **Cli** ([src/PowerCulprit.Cli/](src/PowerCulprit.Cli/)) — plain `Exe`. Dispatches to one of three modes (`RunGui` / `RunHeadlessAsync` / `RunDiagnoseAsync`). For GUI mode it locates `PowerCulprit.Desktop.exe` (or falls back to `dotnet run --project`). For headless mode it builds its own DI container — there is **no shared composition root**; if you add a collector, register it in *both* [App.xaml.cs](src/PowerCulprit.Desktop/App.xaml.cs) and [Program.cs](src/PowerCulprit.Cli/Program.cs).
- **Tests** ([tests/PowerCulprit.Tests/](tests/PowerCulprit.Tests/)) — xUnit. Targets the WinUI TFM because it references `Collectors`. Focus is Core + Storage + fallback behavior; no UI tests.

### Sampling loop

`MonitoringService.RunLoopAsync` is the single timing authority: every `_intervalSeconds` it calls each collector, computes derived values, refreshes `SourceStatus` every 30s, fans out `Database*Async` writes via `Task.WhenAll`, then publishes a `MonitoringSnapshot` under `_lock`. **A single collector throwing must not stop the loop** — every collector call is wrapped in try/catch + `ILogger.LogError`, and the loop continues. Preserve that contract when adding collectors.

### Fallback discipline

The whole product hinges on degrading gracefully:

- Missing values are `null`, never fabricated. UI renders them as `--`.
- Each collector exposes `GetStatus()` returning `SourceStatus { IsAvailable, Status, RequiresAdmin, Details }`. `--diagnose` and the status panel read these.
- `IntelCpuPowerCollector` / `IntelGpuPowerCollector` take the already-collected `HardwareSensorSample` list and *filter* it — they are derivations, not independent data sources. Direct hardware power (W) and GPU Engine utilization (%) must remain separate; utilization is never returned or displayed as watts. RAPL package/platform/core/memory domains overlap and must not be summed.
- `PowerCulpritAnalyzer.Reason` strings must explain *why* a process ranked where it did and call out missing inputs (e.g. "no battery discharge correlation available").

### Time

All timestamps are UTC. Persist either as ISO-8601 strings or Unix milliseconds, but stay consistent with `DatabaseManager`.

## Working in this repo

- The XML solution file lists project paths verbatim — when you add a project, edit [PowerCulprit.slnx](PowerCulprit.slnx) by hand.
- WinUI 3 / Windows App SDK templates may not be installable via `dotnet new` here; project files are hand-maintained. If you need a new WinUI project, copy [PowerCulprit.Desktop.csproj](src/PowerCulprit.Desktop/PowerCulprit.Desktop.csproj) — do not swap the framework to WPF/Avalonia.
- Tray must stay WinUI-native (Win32 `Shell_NotifyIcon` / WinForms `NotifyIcon` interop). Don't pull in the WPF tray libraries.
- If you change anything that affects what `--diagnose` reports (new data source, new permission), update both the collector's `GetStatus()` and the diagnose output in [Program.cs](src/PowerCulprit.Cli/Program.cs).

## Out of scope (don't add)

- NVIDIA / AMD / discrete-GPU collectors of any kind.
- A real per-process wattmeter. The product explicitly does not promise this — see §1 of [init.md](init.md).
- A second GUI framework. WinUI 3 stays.
