using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Collects per-process resource usage: CPU delta, memory, threads, handles,
/// disk IO delta, executable path, command line, parent PID, and foreground status.
/// </summary>
public class ProcessResourceCollector
{
    private readonly ILogger<ProcessResourceCollector> _logger;
    private readonly WindowsServiceLookup _serviceLookup;
    private ProcessSnapshot? _previousSnapshot;
    private Dictionary<int, WmiProcessInfo> _wmiInfoCache = new();
    private readonly Dictionary<int, CachedProcessInfo> _processInfoCache = new();
    private DateTime _lastWmiRefreshUtc = DateTime.MinValue;
    private bool _lastWmiRefreshSucceeded = true;

    // Service-host attribution. Querying the Service Control Manager is cheap (one
    // EnumServicesStatusEx call) but the mapping doesn't change often, so we cache it
    // on the same 30 s cadence as the WMI process info. svchost is the headline use
    // case; this lets the analyzer rank "svchost (Dnscache)" separately from
    // "svchost (wuauserv)" instead of lumping the whole svchost.exe family together.
    private IReadOnlyDictionary<int, IReadOnlyList<string>> _serviceMap
        = new Dictionary<int, IReadOnlyList<string>>();
    private DateTime _lastServiceMapRefreshUtc = DateTime.MinValue;

    private static readonly TimeSpan WmiRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessInfoCacheRetention = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ServiceMapRefreshInterval = TimeSpan.FromSeconds(30);

    // Process names whose primary job is hosting Windows services. We only do a
    // service lookup for these — every other process gets ServiceName left null.
    // Compared case-insensitively against the .exe-stripped process name.
    private static readonly HashSet<string> ServiceHostProcessNames =
        new(StringComparer.OrdinalIgnoreCase) { "svchost" };

    // Kernel-accounting pseudo-processes that NtQuerySystemInformation always returns
    // but that don't represent real user-mode work:
    //   PID 0 — System Idle Process (one "thread" per core whose CPU time tracks idle ticks;
    //           on a quiet machine it will easily outscore everything else)
    //   PID 4 — System (aggregated kernel-mode threads; no meaningful attribution target)
    // Excluding them at the source keeps the DB clean and stops the analyzer from ranking them.
    private static readonly HashSet<int> KernelPseudoPids = new() { 0, 4 };

    public ProcessResourceCollector(
        ILogger<ProcessResourceCollector> logger,
        WindowsServiceLookup? serviceLookup = null)
    {
        _logger = logger;
        // Default-construct the lookup if DI didn't supply one. Keeps callers that
        // already build ProcessResourceCollector by hand (tests, future tooling) working.
        _serviceLookup = serviceLookup ?? new WindowsServiceLookup();
    }

    /// <summary>
    /// Collects a batch of ProcessSample entries for the current cycle.
    /// On the first call, CPU and IO deltas will be null (no previous snapshot).
    /// </summary>
    public IReadOnlyList<ProcessSample> Collect()
    {
        var now = DateTime.UtcNow;
        var samples = new List<ProcessSample>();

        try
        {
            // ── Get foreground PID ─────────────────────
            var foregroundPid = GetForegroundPid();

            // ── Batch WMI query for command line / parent ──
            var wmiInfo = GetCachedWmiProcessInfo(now);

            // ── Refresh the service-host attribution map on the same cadence ──
            RefreshServiceMapIfDue(now);

            // ── Snapshot all processes ─────────────────
            var newSnapshot = CaptureProcessSnapshot(wmiInfo, now);
            if (newSnapshot is null)
                return samples;

            // ── Compute deltas from previous snapshot ──
            if (_previousSnapshot is not null)
            {
                var deltaSeconds = (now - _previousSnapshot.TimestampUtc).TotalSeconds;
                if (deltaSeconds > 0)
                {
                    foreach (var (pid, entry) in newSnapshot.Entries)
                    {
                        var sample = BuildSample(entry, now, foregroundPid);
                        sample = ApplyCpuDelta(sample, entry, pid, deltaSeconds);
                        sample = ApplyIoDelta(sample, entry, pid, deltaSeconds);
                        samples.Add(sample);
                    }
                }
            }
            else
            {
                // First cycle: return samples without deltas
                foreach (var (_, entry) in newSnapshot.Entries)
                {
                    samples.Add(BuildSample(entry, now, foregroundPid));
                }
            }

            _previousSnapshot = newSnapshot;
            PruneProcessInfoCache(now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessResourceCollector.Collect failed");
        }

        return samples;
    }

    /// <summary>
    /// Returns the SourceStatus for process-related data sources.
    /// </summary>
    public SourceStatus GetStatus()
    {
        var hasWmi = _lastWmiRefreshSucceeded;
        try
        {
            if (_lastWmiRefreshUtc == DateTime.MinValue ||
                DateTime.UtcNow - _lastWmiRefreshUtc >= WmiRefreshInterval)
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process");
                using var results = searcher.Get();
                using var enumerator = results.GetEnumerator();
                hasWmi = enumerator.MoveNext();
            }
        }
        catch
        {
            // WMI unavailable
        }

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "ProcessResource",
            IsAvailable = true,
            Status = hasWmi ? "Available" : "Partial",
            Details = hasWmi ? null : "WMI unavailable; command line and parent PID will be null",
            RequiresAdmin = false
        };
    }

    // ──────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────

    private static int? GetForegroundPid()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                return (int)pid;
            }
        }
        catch
        {
            // ignored
        }
        return null;
    }

    private Dictionary<int, WmiProcessInfo> GetCachedWmiProcessInfo(DateTime now)
    {
        if (_wmiInfoCache.Count > 0 && now - _lastWmiRefreshUtc < WmiRefreshInterval)
            return _wmiInfoCache;

        _wmiInfoCache = GetWmiProcessInfo();
        _lastWmiRefreshUtc = now;
        _lastWmiRefreshSucceeded = _wmiInfoCache.Count > 0;
        return _wmiInfoCache;
    }

    private void RefreshServiceMapIfDue(DateTime now)
    {
        if (_serviceMap.Count > 0 && now - _lastServiceMapRefreshUtc < ServiceMapRefreshInterval)
            return;

        try
        {
            _serviceMap = _serviceLookup.GetServicesByPid();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WindowsServiceLookup refresh threw");
            // Leave the previous map in place rather than clearing it — a transient
            // failure shouldn't cause svchost rows to suddenly lose their service
            // label and re-collapse into one aggregated row in the analyzer.
        }
        _lastServiceMapRefreshUtc = now;
    }

    private string? ResolveServiceName(int pid, string processName)
    {
        if (!ServiceHostProcessNames.Contains(processName))
            return null;

        if (!_serviceMap.TryGetValue(pid, out var services) || services.Count == 0)
            return null;

        return services.Count == 1 ? services[0] : string.Join(", ", services);
    }

    private static Dictionary<int, WmiProcessInfo> GetWmiProcessInfo()
    {
        var result = new Dictionary<int, WmiProcessInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine, ParentProcessId, ExecutablePath, CreationDate FROM Win32_Process");
            using var results = searcher.Get();

            foreach (var obj in results)
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"]);
                    result[pid] = new WmiProcessInfo
                    {
                        CommandLine = obj["CommandLine"] as string,
                        ParentPid = obj["ParentProcessId"] is null ? null : Convert.ToInt32(obj["ParentProcessId"]),
                        ExecutablePath = obj["ExecutablePath"] as string,
                        CreationTimeUtc = ParseWmiDateTime(obj["CreationDate"] as string)
                    };
                }
                catch
                {
                    // skip this entry
                }
            }
        }
        catch (Exception)
        {
            // WMI unavailable — fields will be null
        }
        return result;
    }

    private ProcessSnapshot? CaptureProcessSnapshot(
        Dictionary<int, WmiProcessInfo> wmiInfo,
        DateTime now)
    {
        var buffer = QueryNativeProcessBuffer();
        if (buffer == IntPtr.Zero)
            return null;

        try
        {
            var snapshot = new ProcessSnapshot { TimestampUtc = now };
            var current = buffer;

            while (true)
            {
                var info = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESS_INFORMATION>(current);
                var pid = info.UniqueProcessId.ToInt64();
                if (pid >= 0 && pid <= int.MaxValue && !KernelPseudoPids.Contains((int)pid))
                {
                    var processName = GetProcessName(info, (int)pid);
                    var cachedInfo = GetOrCreateCachedProcessInfo((int)pid, processName, wmiInfo, info.CreateTime, now);

                    snapshot.Entries[(int)pid] = new ProcessEntry
                    {
                        Pid = (int)pid,
                        ProcessName = cachedInfo.ProcessName,
                        ExecutablePath = cachedInfo.ExecutablePath,
                        CommandLine = cachedInfo.CommandLine,
                        ParentPid = cachedInfo.ParentPid ?? ToNullablePid(info.InheritedFromUniqueProcessId),
                        TotalCpuTime = TimeSpan.FromTicks(Math.Max(0, info.UserTime + info.KernelTime)),
                        WorkingSetBytes = checked((long)Math.Min((ulong)long.MaxValue, info.WorkingSetSize.ToUInt64())),
                        PrivateMemoryBytes = checked((long)Math.Min((ulong)long.MaxValue, info.PrivatePageCount.ToUInt64())),
                        ThreadCount = (int)Math.Min(int.MaxValue, (long)info.NumberOfThreads),
                        HandleCount = (int)Math.Min(int.MaxValue, (long)info.HandleCount),
                        ReadTransferBytes = info.ReadTransferCount >= 0 ? info.ReadTransferCount : null,
                        WriteTransferBytes = info.WriteTransferCount >= 0 ? info.WriteTransferCount : null
                    };
                }

                if (info.NextEntryOffset == 0)
                    break;

                current = IntPtr.Add(current, checked((int)info.NextEntryOffset));
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse native process snapshot");
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IntPtr QueryNativeProcessBuffer()
    {
        uint length = 1 << 20;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(checked((int)length));
            var status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemProcessInformationClass,
                buffer,
                length,
                out var requiredLength);

            if (status == 0)
                return buffer;

            Marshal.FreeHGlobal(buffer);

            if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                return IntPtr.Zero;

            length = Math.Max(requiredLength + 64 * 1024, length * 2);
        }

        return IntPtr.Zero;
    }

    private CachedProcessInfo GetOrCreateCachedProcessInfo(
        int pid,
        string processName,
        Dictionary<int, WmiProcessInfo> wmiInfo,
        long nativeCreationTime,
        DateTime now)
    {
        wmiInfo.TryGetValue(pid, out var wmi);
        var creationTimeUtc = wmi?.CreationTimeUtc ?? FromFileTimeUtc(nativeCreationTime);

        if (!_processInfoCache.TryGetValue(pid, out var cached) ||
            !IsSameProcess(cached, processName, creationTimeUtc))
        {
            cached = new CachedProcessInfo
            {
                ProcessName = processName,
                CreationTimeUtc = creationTimeUtc
            };
            _processInfoCache[pid] = cached;
        }

        cached.LastSeenUtc = now;
        cached.ProcessName = processName;

        if (wmi is not null)
        {
            cached.CreationTimeUtc = creationTimeUtc;
            cached.CommandLine = wmi.CommandLine;
            cached.ParentPid = wmi.ParentPid;
            if (!string.IsNullOrWhiteSpace(wmi.ExecutablePath))
                cached.ExecutablePath = wmi.ExecutablePath;
        }

        if (cached.ExecutablePath is null && !cached.ExecutablePathAttempted)
        {
            cached.ExecutablePathAttempted = true;
            cached.ExecutablePath = ResolveExecutablePath(pid);
        }

        return cached;
    }

    private static string GetProcessName(NativeMethods.SYSTEM_PROCESS_INFORMATION info, int pid)
    {
        if (info.ImageName.Buffer != IntPtr.Zero && info.ImageName.Length > 0)
        {
            var imageName = Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2);
            if (!string.IsNullOrWhiteSpace(imageName))
            {
                var extension = Path.GetExtension(imageName);
                return string.IsNullOrEmpty(extension)
                    ? imageName
                    : Path.GetFileNameWithoutExtension(imageName);
            }
        }

        return pid switch
        {
            0 => "Idle",
            4 => "System",
            _ => $"pid_{pid}"
        };
    }

    private static int? ToNullablePid(IntPtr pid)
    {
        var value = pid.ToInt64();
        return value > 0 && value <= int.MaxValue ? (int)value : null;
    }

    private static DateTime? FromFileTimeUtc(long fileTime)
    {
        if (fileTime <= 0)
            return null;

        try { return DateTime.FromFileTimeUtc(fileTime); }
        catch { return null; }
    }

    private static string? ResolveExecutablePath(int pid)
    {
        if (pid <= 0)
            return null;

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid);

        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var length = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref length)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool IsSameProcess(
        CachedProcessInfo cached,
        string processName,
        DateTime? creationTimeUtc)
    {
        if (!string.Equals(cached.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (cached.CreationTimeUtc.HasValue && creationTimeUtc.HasValue &&
            cached.CreationTimeUtc.Value != creationTimeUtc.Value)
        {
            return false;
        }

        return true;
    }

    private void PruneProcessInfoCache(DateTime now)
    {
        foreach (var (pid, info) in _processInfoCache.ToList())
        {
            if (now - info.LastSeenUtc >= ProcessInfoCacheRetention)
                _processInfoCache.Remove(pid);
        }
    }

    private static DateTime? ParseWmiDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private ProcessSample BuildSample(
        ProcessEntry entry, DateTime now, int? foregroundPid)
    {
        return new ProcessSample
        {
            TimestampUtc = now,
            Pid = entry.Pid,
            ProcessName = entry.ProcessName ?? string.Empty,
            ExecutablePath = entry.ExecutablePath,
            CommandLine = entry.CommandLine,
            ParentPid = entry.ParentPid,
            WorkingSetMb = entry.WorkingSetBytes.HasValue
                ? Math.Round(entry.WorkingSetBytes.Value / (1024.0 * 1024.0), 2)
                : null,
            PrivateMemoryMb = entry.PrivateMemoryBytes.HasValue
                ? Math.Round(entry.PrivateMemoryBytes.Value / (1024.0 * 1024.0), 2)
                : null,
            ThreadCount = entry.ThreadCount,
            HandleCount = entry.HandleCount,
            IsForegroundProcess = foregroundPid.HasValue && entry.Pid == foregroundPid.Value,
            ServiceName = ResolveServiceName(entry.Pid, entry.ProcessName ?? string.Empty)
        };
    }

    private ProcessSample ApplyCpuDelta(
        ProcessSample sample, ProcessEntry entry, int pid, double deltaSeconds)
    {
        var previousSnapshot = _previousSnapshot;
        if (previousSnapshot is null || !previousSnapshot.Entries.TryGetValue(pid, out var prevEntry))
            return sample;

        if (entry.TotalCpuTime.HasValue && prevEntry.TotalCpuTime.HasValue)
        {
            var deltaTicks = entry.TotalCpuTime.Value - prevEntry.TotalCpuTime.Value;
            if (deltaTicks.TotalSeconds > 0)
            {
                // CPU percent = consumed seconds / wall clock seconds * 100
                // This gives utilization relative to a single core
                var cpuPercent = deltaTicks.TotalSeconds / deltaSeconds * 100.0;
                // Clamp to reasonable range (can exceed 100% for multi-threaded)
                sample = sample with { CpuPercent = Math.Round(Math.Max(0, cpuPercent), 2) };
            }
        }

        return sample;
    }

    private ProcessSample ApplyIoDelta(
        ProcessSample sample, ProcessEntry entry, int pid, double deltaSeconds)
    {
        var previousSnapshot = _previousSnapshot;
        if (previousSnapshot is null || !previousSnapshot.Entries.TryGetValue(pid, out var prevEntry))
            return sample;

        if (entry.ReadTransferBytes.HasValue && prevEntry.ReadTransferBytes.HasValue)
        {
            var deltaRead = entry.ReadTransferBytes.Value - prevEntry.ReadTransferBytes.Value;
            if (deltaRead >= 0)
            {
                sample = sample with
                {
                    DiskReadBytesPerSecond = Math.Round(deltaRead / deltaSeconds, 1)
                };
            }
        }

        if (entry.WriteTransferBytes.HasValue && prevEntry.WriteTransferBytes.HasValue)
        {
            var deltaWrite = entry.WriteTransferBytes.Value - prevEntry.WriteTransferBytes.Value;
            if (deltaWrite >= 0)
            {
                sample = sample with
                {
                    DiskWriteBytesPerSecond = Math.Round(deltaWrite / deltaSeconds, 1)
                };
            }
        }

        return sample;
    }

    // ──────────────────────────────────────────────
    //  Internal state types
    // ──────────────────────────────────────────────

    private sealed class ProcessSnapshot
    {
        public DateTime TimestampUtc { get; init; }
        public Dictionary<int, ProcessEntry> Entries { get; } = new();
    }

    private sealed class ProcessEntry
    {
        public int Pid { get; init; }
        public string? ProcessName { get; init; }
        public string? ExecutablePath { get; set; }
        public string? CommandLine { get; set; }
        public int? ParentPid { get; set; }
        public TimeSpan? TotalCpuTime { get; set; }
        public long? WorkingSetBytes { get; set; }
        public long? PrivateMemoryBytes { get; set; }
        public int? ThreadCount { get; set; }
        public int? HandleCount { get; set; }
        public long? ReadTransferBytes { get; set; }
        public long? WriteTransferBytes { get; set; }
    }

    private sealed class WmiProcessInfo
    {
        public string? CommandLine { get; init; }
        public int? ParentPid { get; init; }
        public string? ExecutablePath { get; init; }
        public DateTime? CreationTimeUtc { get; init; }
    }

    private sealed class CachedProcessInfo
    {
        public string ProcessName { get; set; } = string.Empty;
        public string? ExecutablePath { get; set; }
        public string? CommandLine { get; set; }
        public int? ParentPid { get; set; }
        public DateTime? CreationTimeUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool ExecutablePathAttempted { get; set; }
    }
}
