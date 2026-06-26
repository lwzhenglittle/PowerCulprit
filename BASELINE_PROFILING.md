# PowerCulprit Baseline Profiling Report

Date: 2026-06-25  
Scope: current pre-ETW runtime baseline for `PowerCulprit.Cli --headless`  
Configuration: Release, Windows x64, .NET SDK 10.0.301

> This report is a baseline before implementing ETW collection. The current repository contains [ETW.md](ETW.md) as a design document, but no runtime ETW collector is wired into `MonitoringService` yet.

## Executive summary

The default headless configuration remains lightweight:

- **Default mode** (`--interval 2`, GPU Engine sampling off):
  - Process sampling: **2.70% of one core avg**, **9.26% p95**, **29.28% max**.
  - `dotnet-counters`: **4.28% of one core avg**, **14.06% max**.
  - Average system CPU on a 16 logical-processor machine: **0.17–0.27%**.
  - Working set stable around **169–172 MB**.
  - Allocation rate around **868 KB/s**.
- **1-second interval** roughly increases CPU/allocation as expected:
  - `dotnet-counters`: **6.66% of one core avg**, **1.56 MB/s allocation avg**.
- **GPU Engine sampling enabled** is substantially more expensive:
  - Process sampling: **13.39% of one core avg**, **72.52% p95**, **92.28% max**.
  - `dotnet-counters`: **22.69% of one core avg**, **73.44% max**.
  - Allocation rate jumps to **18.82 MB/s avg**.
  - Trace points at `System.Diagnostics.PerformanceCounter` / `PerformanceMonitor.GetData` and `WindowsGpuEngineCollector` reads.

Recommendation: use **default interval 2s + GPU off** as the ETW A/B baseline. ETW should only remain default-on if it adds no sustained CPU load and keeps average overhead close to the current default baseline.

## Repository and environment

| Item | Value |
|---|---|
| Branch | `feature/powerculprit-implementation` |
| Commit | `17730a3` |
| Working tree | Existing changes in [App.xaml.cs](src/PowerCulprit.Desktop/App.xaml.cs), [PowerCulprit.Desktop.csproj](src/PowerCulprit.Desktop/PowerCulprit.Desktop.csproj), plus [ETW.md](ETW.md) |
| OS | Windows 11 Home, build `10.0.29613` |
| Logical processors | 16 |
| .NET SDK | `10.0.301` |
| .NET host/runtime | `10.0.9` |
| Profiling tools | `dotnet-counters` / `dotnet-trace` `9.0.661903` |
| Build command | `dotnet build -c Release src/PowerCulprit.Cli/PowerCulprit.Cli.csproj` |
| Build result | Success, 0 warnings, 0 errors |

Collected raw artifacts are under:

```text
C:\Users\92469\AppData\Local\Temp\powerculprit-baseline-profile-20260625
```

Important artifact files:

```text
process-sampling-summary.json
trace-counter-manifest.json
default_interval2_gpuoff.runtime-counters.csv
default_interval2_gpuoff.cpu.nettrace
default_interval2_gpuoff.cpu-topn.txt
fast_interval1_gpuoff.runtime-counters.csv
gpu_interval2_gpuon.runtime-counters.csv
gpu_interval2_gpuon.cpu.nettrace
gpu_interval2_gpuon.cpu-topn.txt
```

## Methodology

Three scenarios were measured:

| Scenario | Command shape | Purpose |
|---|---|---|
| `default_interval2_gpuoff` | `--headless --duration 110s/115s --interval 2` | Product default headless baseline; GPU Engine sampling disabled. |
| `fast_interval1_gpuoff` | `--headless --duration 80s/110s --interval 1` | Stress the polling loop at minimum interval. |
| `gpu_interval2_gpuon` | `--headless --duration 110s/115s --interval 2 --enable-gpu` | Quantify optional Windows GPU Engine sampling overhead. |

Measurements used two complementary methods:

1. **Process-level sampling** via `Get-Process.TotalProcessorTime` every 1 second after an 8-second warmup.
   - CPU is reported as **percent of one logical core**.
   - System CPU is computed as `one-core percent / 16`.
2. **Runtime counters** via:

   ```powershell
   dotnet-counters collect -p <pid> --providers System.Runtime \
     --refresh-interval 1 --duration 00:00:25 --format csv
   ```

3. **CPU sample traces** for default and GPU-enabled scenarios via:

   ```powershell
   dotnet-trace collect -p <pid> --profile dotnet-sampled-thread-time \
     --duration 00:00:30
   dotnet-trace report <trace.nettrace> topN -n 40
   ```

Caveat: `dotnet-sampled-thread-time` topN includes idle/waiting managed threads. The first entries are wait functions and should not be interpreted as active CPU work.

## Scenario results

### Process-level CPU and memory sampling

| Scenario | Avg CPU, one core | P95 CPU, one core | Max CPU, one core | Avg system CPU | P95 system CPU | Avg WS MB | WS min-max MB | Avg private MB | Avg threads | Max threads | Avg handles | Max handles |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Default, interval 2, GPU off | 2.70% | 9.26% | 29.28% | 0.169% | 0.579% | 168.8 | 161.4–171.6 | 121.1 | 16.0 | 17 | 496 | 501 |
| Interval 1, GPU off | 4.06% | 7.70% | 41.45% | 0.254% | 0.481% | 170.5 | 167.2–176.4 | 122.7 | 16.3 | 18 | 500 | 512 |
| Interval 2, GPU on | 13.39% | 72.52% | 92.28% | 0.837% | 4.532% | 184.1 | 179.8–186.9 | 133.0 | 18.0 | 19 | 674 | 678 |

Interpretation:

- Default mode shows the expected sampling pulse pattern: brief work burst, then near-idle until the next 2-second cycle.
- Reducing the interval to 1s increases average CPU, but still stays below 0.5% system CPU in this run.
- Enabling GPU Engine sampling is the only scenario with large CPU bursts and materially higher memory/handle count.

### `dotnet-counters` runtime metrics

| Scenario | Avg CPU, one core | Max CPU, one core | Avg system CPU | Avg alloc KB/s | Max alloc KB/s | Avg WS MB | Avg GC committed MB | GC pause total | Gen0 / Gen1 / Gen2 | Exceptions | Lock contentions |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Default, interval 2, GPU off | 4.28% | 14.06% | 0.267% | 867.6 | 1817.8 | 172.0 | 11.0 | 5.695 ms | 1 / 1 / 0 | 0 | 0 |
| Interval 1, GPU off | 6.66% | 37.50% | 0.416% | 1557.7 | 3329.9 | 172.1 | 11.0 | 5.725 ms | 3 / 1 / 0 | 0 | 0 |
| Interval 2, GPU on | 22.69% | 73.44% | 1.418% | 19269.5 | 72866.0 | 186.6 | 14.7 | 56.914 ms | 46 / 1 / 1 | 0 | 0 |

Interpretation:

- Default allocation rate is under 1 MB/s and GC pause time is negligible.
- 1s interval roughly doubles allocation rate and modestly increases CPU.
- GPU sampling produces a much larger allocation stream and more Gen0 collections; this aligns with PerformanceCounter instance enumeration/read overhead.

### SQLite write volume during profiling

Counts from the profiling databases:

| Scenario DB | System power rows | Process rows | GPU process rows | Hardware sensor rows | Source status rows |
|---|---:|---:|---:|---:|---:|
| `default_interval2_gpuoff.counters.db` | 57 | 23,041 | 0 | 2,337 | 24 |
| `fast_interval1_gpuoff.counters.db` | 79 | 31,666 | 0 | 3,239 | 18 |
| `gpu_interval2_gpuon.counters.db` | 57 | 23,351 | 13,780 | 2,337 | 24 |

Status highlights:

- `BatteryAPI`: Available.
- `ProcessResource`: Available.
- `WindowsGpuEngine`: Disabled in GPU-off scenarios; Available in GPU-on scenario.
- `LibreHardwareMonitor`: Partial; details indicate some sensors may require administrator privileges.
- `CPU_Package_Power`: Partial/unavailable without admin sensor access.
- `Intel_iGPU_Power`: Requires admin or falls back to GPU Engine utilization when GPU sampling is enabled.

## CPU trace findings

### Default: interval 2, GPU off

Top exclusive frames from `default_interval2_gpuoff.cpu-topn.txt`:

| Rank | Function | Exclusive |
|---:|---|---:|
| 1 | `WaitHandle.WaitOneNoCheck(...)` | 37.50% |
| 2 | `LowLevelLifoSemaphore.WaitForSignal(...)` | 37.28% |
| 3 | `Monitor.Wait(...)` | 25.00% |
| 4 | `SqliteDataReader.NextResult()` | 0.07% |
| 5 | `ThreadAffinity.Set(...)` | 0.05% |
| 6 | `ManagementObjectCollection...MoveNext()` | 0.03% |

Interpretation:

- The top three entries are idle/wait states, not active work.
- Actual active managed hotspots are tiny in this trace.
- SQLite writes are visible but not dominant.
- LHM-related thread affinity and WMI enumeration appear, but at very low exclusive percentages in this run.
- `Marshal.PtrToStructure`, native process enumeration, and CPU delta computation are not meaningful hotspots.

### Interval 2, GPU Engine sampling enabled

Top exclusive frames from `gpu_interval2_gpuon.cpu-topn.txt`:

| Rank | Function | Exclusive / Inclusive |
|---:|---|---:|
| 1 | `WaitHandle.WaitOneNoCheck(...)` | 37.49% exclusive |
| 2 | `LowLevelLifoSemaphore.WaitForSignal(...)` | 34.50% exclusive |
| 3 | `Monitor.Wait(...)` | 25.00% exclusive |
| 4 | `PerformanceMonitor.GetData(...)` | 2.08% exclusive |
| 7 | `WindowsGpuEngineCollector.GpuCounterState.Read(...)` | 2.74% inclusive |
| 25 | `PerformanceCounter.NextSample()` | 2.34% inclusive |

Interpretation:

- Idle/wait frames still dominate the sample report, but GPU-specific work is now clearly visible.
- The expensive path is Windows PerformanceCounter/GPU Engine sampling, not the general process sampler.
- This explains the high CPU bursts, handle count increase, and high allocation rate when `--enable-gpu` is used.

## Comparison to previously recorded baseline

The existing memory baseline from 2026-06-24 recorded:

- Avg CPU: **4.24% of one core**.
- Peak CPU: **14% of one core**.
- Working set: **171–174 MB**.
- Allocation rate: **~926 KB/s avg**.

The new default-mode counter run is effectively the same:

- Avg CPU: **4.28% of one core**.
- Peak CPU: **14.06% of one core**.
- Working set: **169.6–173.1 MB**.
- Allocation rate: **867.6 KB/s avg**.

So the current default baseline is stable versus the previous measurement.

## Findings

### 1. Default mode is low-overhead enough for the product goal

Default headless sampling uses less than 0.3% average system CPU by runtime counters on this 16-logical-processor machine. The process-level sampler measured even lower average CPU.

This supports the product's positioning as a low-overhead correlation and attribution tool.

### 2. The main loop scales with interval as expected

Switching from 2s to 1s interval increases CPU and allocation but does not create runaway behavior:

- CPU avg by counters: **4.28% → 6.66% of one core**.
- Allocation avg: **867.6 KB/s → 1557.7 KB/s**.
- Working set remains stable.
- No exceptions or lock contentions were observed.

### 3. GPU Engine sampling is expensive and should remain opt-in

GPU sampling generated:

- Large CPU bursts, up to **73–92% of one core** depending on measurement method.
- Average CPU around **13–23% of one core**.
- Allocation rate around **18.8 MB/s avg**.
- Additional `gpu_process_samples` writes: **13,780 rows** in the profiling DB.

This is acceptable as an explicit diagnostic mode, but not as a default battery-monitoring path.

### 4. Database writing is not currently a top bottleneck

SQLite methods appear in traces but at very small exclusive percentages. The existing single-writer channel and transaction batching are doing their job.

### 5. Process enumeration is not the hot path

The trace does not show native process enumeration or `Marshal.PtrToStructure` as meaningful active CPU cost. Rewriting process enumeration is unlikely to produce high ROI compared with GPU PerformanceCounter or sensor cadence work.

### 6. Memory is stable in default and interval-1 modes

Working set stays around 170–172 MB in GPU-off scenarios, with GC committed memory around 11 MB and negligible pause time.

GPU-on mode increases working set to about 186 MB and GC committed memory to about 15 MB, consistent with PerformanceCounter and additional sample volume.

## ETW baseline implications

For the upcoming ETW work, use **default interval 2s, GPU off** as the baseline:

| Metric | Baseline target |
|---|---:|
| Avg CPU | ~4.3% of one core by `dotnet-counters` |
| Max CPU | ~14% of one core by `dotnet-counters` |
| Avg allocation | ~0.9 MB/s |
| Working set | ~170–173 MB |
| GC pause | negligible |
| CPU shape | 2-second pulse, no sustained busy background thread |

Suggested ETW acceptance guardrails:

- ETW should not add a persistent always-busy consumer thread.
- Average CPU should ideally stay within **+1–2% of one core** over this baseline during idle/normal desktop use.
- Allocation rate should not grow substantially when network activity is low.
- Event callbacks should aggregate into mutable per-PID counters with minimal allocation.
- Lost events or provider failures should affect `SourceStatus`, not crash or block the sampling loop.
- Re-run these same scenarios after ETW is wired in, plus a network-heavy browser/download scenario.

## Recommendations

1. Keep **GPU Engine sampling disabled by default**.
2. Use `--interval 2` as the normal low-overhead baseline.
3. For ETW implementation, add profiling before deciding to keep ETW default-on permanently.
4. If GPU sampling needs to be more usable, optimize or throttle `WindowsGpuEngineCollector` before turning it on by default.
5. Avoid optimizing `ProcessResourceCollector` native enumeration prematurely; current evidence does not show it as hot.
6. Keep database writes batched through the existing write queue.
7. Consider a future sensor cadence optimization for LHM/hardware sensors if more profiling shows it dominating on other machines.

## Reproduction commands

Build:

```powershell
dotnet build -c Release src/PowerCulprit.Cli/PowerCulprit.Cli.csproj
```

Default baseline run:

```powershell
src\PowerCulprit.Cli\bin\Release\net10.0-windows10.0.26100.0\PowerCulprit.Cli.exe `
  --headless --duration 115s --interval 2 `
  --db C:\Users\92469\AppData\Local\Temp\powerculprit-baseline-profile-20260625\default_interval2_gpuoff.db
```

Runtime counters:

```powershell
dotnet-counters collect -p <PID> --providers System.Runtime `
  --refresh-interval 1 --duration 00:00:25 --format csv `
  -o C:\Users\92469\AppData\Local\Temp\powerculprit-baseline-profile-20260625\default_interval2_gpuoff.runtime-counters
```

CPU trace:

```powershell
dotnet-trace collect -p <PID> --profile dotnet-sampled-thread-time `
  --duration 00:00:30 `
  -o C:\Users\92469\AppData\Local\Temp\powerculprit-baseline-profile-20260625\default_interval2_gpuoff.cpu.nettrace

dotnet-trace report C:\Users\92469\AppData\Local\Temp\powerculprit-baseline-profile-20260625\default_interval2_gpuoff.cpu.nettrace topN -n 40
```
