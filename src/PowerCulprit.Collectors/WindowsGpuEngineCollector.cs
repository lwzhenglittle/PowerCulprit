using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Collects per-process GPU engine utilization from Windows PDH counters.
/// Reads the "GPU Engine" performance counter category and maps each instance
/// to a GpuProcessSample with PID and engine type.
/// </summary>
public class WindowsGpuEngineCollector : IDisposable
{
    private readonly ILogger<WindowsGpuEngineCollector> _logger;
    private PerformanceCounterCategory? _category;
    private bool _availabilityChecked;
    private bool _isAvailable;
    private bool _categoryKnownMissing;
    private DateTime _nextAvailabilityRetryUtc = DateTime.MinValue;
    private readonly Dictionary<string, GpuCounterState> _counters = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCounterRefreshUtc = DateTime.MinValue;
    private int _disposed;

    private static readonly TimeSpan CounterRefreshInterval = TimeSpan.FromSeconds(10);

    // Backoff before re-probing after a failed availability probe or a transient
    // read failure — PDH counter rebuilds (lodctr) resolve themselves, so the
    // collector must not latch off permanently on the first error.
    private static readonly TimeSpan AvailabilityRetryInterval = TimeSpan.FromSeconds(30);

    // Instance name pattern: pid_11872_luid_0x..._phys_0_eng_0_engtype_3d
    private static readonly Regex PidRegex = new(
        @"pid_(\d+)_luid", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, GpuEngineType> EngineTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3d"] = GpuEngineType.ThreeD,
        ["compute"] = GpuEngineType.Compute,
        ["videodecode"] = GpuEngineType.VideoDecode,
        ["videoencode"] = GpuEngineType.VideoEncode,
        ["copy"] = GpuEngineType.Copy,
    };

    public WindowsGpuEngineCollector(ILogger<WindowsGpuEngineCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Collects GpuProcessSamples from the GPU Engine performance counters.
    /// Returns an empty list if the counter is unavailable.
    /// </summary>
    public IReadOnlyList<GpuProcessSample> Collect()
    {
        var samples = new List<GpuProcessSample>();
        var now = DateTime.UtcNow;

        if (!EnsureAvailable())
            return samples;

        try
        {
            RefreshCountersIfNeeded(now);

            // RefreshCountersIfNeeded ran above and won't run again until the
            // next CounterRefreshInterval expires, so _counters is stable for
            // the duration of this loop — no need for a defensive .ToList().
            foreach (var counter in _counters.Values)
            {
                try
                {
                    var sample = counter.Read(now);
                    if (sample is not null)
                        samples.Add(sample);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to read GPU Engine instance '{Name}'", counter.InstanceName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query GPU Engine counters");
            // Transient PDH failure (counter library rebuild, corrupt instance
            // set) — do not latch off for the process lifetime. Drop the
            // possibly-corrupt counter state and let EnsureAvailable re-probe
            // after the backoff interval.
            foreach (var counter in _counters.Values)
                counter.Dispose();
            _counters.Clear();
            _category = null;
            _isAvailable = false;
            _availabilityChecked = false;
            _nextAvailabilityRetryUtc = now + AvailabilityRetryInterval;
        }

        return samples;
    }

    private void RefreshCountersIfNeeded(DateTime now)
    {
        if (_counters.Count > 0 && now - _lastCounterRefreshUtc < CounterRefreshInterval)
            return;

        var instanceNames = _category!.GetInstanceNames();
        var liveNames = new HashSet<string>(instanceNames, StringComparer.OrdinalIgnoreCase);

        foreach (var staleName in _counters.Keys.Where(name => !liveNames.Contains(name)).ToList())
        {
            _counters[staleName].Dispose();
            _counters.Remove(staleName);
        }

        foreach (var instanceName in instanceNames)
        {
            if (_counters.ContainsKey(instanceName))
                continue;

            _counters[instanceName] = new GpuCounterState(
                instanceName,
                ParsePid(instanceName),
                ParseEngineType(instanceName));
        }

        _lastCounterRefreshUtc = now;
    }

    private bool EnsureAvailable()
    {
        if (_availabilityChecked)
            return _isAvailable;

        var now = DateTime.UtcNow;
        if (now < _nextAvailabilityRetryUtc)
            return false;

        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                // Confirmed absent (no GPU counters registered on this machine).
                // Still retry periodically — a driver install can add them.
                _categoryKnownMissing = true;
                ScheduleAvailabilityRetry(now);
                _logger.LogWarning("GPU Engine counter category not found on this system");
                return false;
            }

            _category = new PerformanceCounterCategory("GPU Engine");
            _isAvailable = _category.CounterExists("Utilization Percentage");
            if (_isAvailable)
            {
                // Success latches — availability only needs to be proven once.
                _availabilityChecked = true;
                _categoryKnownMissing = false;
                _logger.LogInformation("GPU Engine counter category found");
            }
            else
            {
                _categoryKnownMissing = true;
                ScheduleAvailabilityRetry(now);
                _logger.LogWarning("GPU Engine counter does not have 'Utilization Percentage'");
            }
        }
        catch (Exception ex)
        {
            // Transient probe failure (perflib busy, counters being rebuilt).
            _isAvailable = false;
            _categoryKnownMissing = false;
            _category = null;
            ScheduleAvailabilityRetry(now);
            _logger.LogWarning(ex, "GPU Engine counter category probe failed; will retry");
        }

        return _isAvailable;
    }

    private void ScheduleAvailabilityRetry(DateTime now)
    {
        _availabilityChecked = false;
        _nextAvailabilityRetryUtc = now + AvailabilityRetryInterval;
    }

    internal static int? ParsePid(string instanceName)
    {
        var pidMatch = PidRegex.Match(instanceName);
        return pidMatch.Success && int.TryParse(pidMatch.Groups[1].Value, out var pid)
            ? pid
            : null;
    }

    private static GpuEngineType ParseEngineType(string instanceName)
    {
        var engineType = GpuEngineType.Other;
        foreach (var (key, value) in EngineTypeMap)
        {
            if (instanceName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                engineType = value;
                break;
            }
        }

        return engineType;
    }

    public void Dispose()
    {
        // Guard against double-dispose from DI container teardown following an
        // explicit Dispose during shutdown.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var counter in _counters.Values)
            counter.Dispose();
        _counters.Clear();
    }

    private sealed class GpuCounterState : IDisposable
    {
        private static readonly TimeSpan ProcessNameRefreshInterval = TimeSpan.FromSeconds(30);

        private readonly PerformanceCounter _counter;
        private readonly int? _pid;
        private readonly GpuEngineType _engineType;
        private string? _processName;
        private DateTime _lastProcessNameRefreshUtc = DateTime.MinValue;

        public GpuCounterState(string instanceName, int? pid, GpuEngineType engineType)
        {
            InstanceName = instanceName;
            _pid = pid;
            _engineType = engineType;
            _counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName);
        }

        public string InstanceName { get; }

        public GpuProcessSample? Read(DateTime now)
        {
            float utilization;
            try
            {
                utilization = _counter.NextValue();
            }
            catch
            {
                return null;
            }

            RefreshProcessName(now);

            return new GpuProcessSample
            {
                TimestampUtc = now,
                Pid = _pid,
                ProcessName = _processName,
                EngineName = InstanceName,
                EngineType = _engineType,
                UtilizationPercent = Math.Round(utilization, 2)
            };
        }

        private void RefreshProcessName(DateTime now)
        {
            if (!_pid.HasValue || now - _lastProcessNameRefreshUtc < ProcessNameRefreshInterval)
                return;

            _lastProcessNameRefreshUtc = now;
            try
            {
                using var process = Process.GetProcessById(_pid.Value);
                _processName = process.ProcessName;
            }
            catch
            {
                _processName = null;
            }
        }

        public void Dispose()
        {
            _counter.Dispose();
        }
    }

    /// <summary>
    /// Returns the SourceStatus for the GPU Engine counter.
    /// </summary>
    public SourceStatus GetStatus()
    {
        EnsureAvailable();

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "WindowsGpuEngine",
            IsAvailable = _isAvailable,
            Status = _isAvailable ? SourceStatusStrings.Available : SourceStatusStrings.Unavailable,
            Details = _isAvailable
                ? "GPU Engine performance counter available"
                : _categoryKnownMissing
                    ? "GPU Engine performance counter not found on this system"
                    : "GPU Engine performance counter temporarily unavailable; will retry",
            RequiresAdmin = false
        };
    }
}
