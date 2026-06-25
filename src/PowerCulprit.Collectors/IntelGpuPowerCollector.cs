using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Derives Intel iGPU/Arc power and activity from LibreHardwareMonitor samples,
/// with GPU Engine utilization as fallback.
/// </summary>
public class IntelGpuPowerCollector
{
    private readonly ILogger<IntelGpuPowerCollector> _logger;

    public IntelGpuPowerCollector(ILogger<IntelGpuPowerCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Filters the LHM sensor batch for Intel GPU power/load/temp/clock readings.
    /// </summary>
    public IReadOnlyList<HardwareSensorSample> FilterFromLhm(
        IReadOnlyList<HardwareSensorSample> lhmSamples)
    {
        if (lhmSamples.Count == 0)
            return Array.Empty<HardwareSensorSample>();

        var gpuSamples = lhmSamples
            .Where(s =>
                s.DeviceName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                s.DeviceName.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                s.DeviceName.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("GT", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return gpuSamples.AsReadOnly();
    }

    /// <summary>
    /// Returns the best available iGPU power value in watts, or null.
    /// Uses LHM data first, falls back to GPU Engine aggregate utilization.
    /// </summary>
    public double? GetIgpuPowerWatts(
        IReadOnlyList<HardwareSensorSample> lhmSamples,
        IReadOnlyList<GpuProcessSample>? gpuSamples = null)
    {
        var value = TryGetIgpuPowerWatts(lhmSamples, gpuSamples, out _);
        return value;
    }

    /// <summary>
    /// Like <see cref="GetIgpuPowerWatts"/> but also reports whether the value
    /// came from an LHM hardware power sensor (true) vs the GPU Engine utilization
    /// fallback / no data (false). Lets <c>MonitoringService</c> avoid re-scanning
    /// the LHM sample list a second time just to label the snapshot.
    /// </summary>
    public double? TryGetIgpuPowerWatts(
        IReadOnlyList<HardwareSensorSample> lhmSamples,
        IReadOnlyList<GpuProcessSample>? gpuSamples,
        out bool fromHardwareSensor)
    {
        // Single scan of lhmSamples — used both for the value and the from-hardware flag.
        HardwareSensorSample? gpuPowerSample = null;
        for (int i = 0; i < lhmSamples.Count; i++)
        {
            var s = lhmSamples[i];
            if (s.MetricName == "Power" &&
                (s.SensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                 s.SensorName.Contains("Graphics", StringComparison.OrdinalIgnoreCase)))
            {
                gpuPowerSample = s;
                break;
            }
        }

        if (gpuPowerSample is not null)
        {
            fromHardwareSensor = true;
            return gpuPowerSample.Value;
        }

        fromHardwareSensor = false;

        // Fallback: aggregate GPU Engine utilization as a proxy
        if (gpuSamples is not null && gpuSamples.Count > 0)
        {
            var totalUtil = gpuSamples.Sum(g => g.UtilizationPercent);
            return totalUtil; // not watts, but a proxy for activity
        }

        return null;
    }

    /// <summary>
    /// Returns the aggregate iGPU utilization from GPU Engine samples (all engines),
    /// as a percentage 0–100 (capped).
    /// </summary>
    public double? GetIgpuActivityPercent(IReadOnlyList<GpuProcessSample> gpuSamples)
    {
        if (gpuSamples.Count == 0) return null;

        var totalUtil = gpuSamples.Sum(g => g.UtilizationPercent);
        return Math.Round(Math.Min(totalUtil, 100.0), 1);
    }

    /// <summary>
    /// Returns the SourceStatus for Intel iGPU power data.
    /// </summary>
    public SourceStatus GetStatus(
        IReadOnlyList<HardwareSensorSample>? lhmSamples = null,
        IReadOnlyList<GpuProcessSample>? gpuSamples = null)
    {
        var hasGpuPower = false;
        var hasGpuEngine = gpuSamples is { Count: > 0 };
        var sensorCount = 0;

        if (lhmSamples is not null)
        {
            var filtered = FilterFromLhm(lhmSamples);
            sensorCount = filtered.Count;
            hasGpuPower = filtered.Any(s =>
                s.MetricName == "Power" &&
                (s.SensorName.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                 s.SensorName.Contains("Graphics", StringComparison.OrdinalIgnoreCase)));
        }

        // Probe Level Zero
        var hasLevelZero = File.Exists(@"C:\Windows\System32\ze_loader.dll");

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "Intel_iGPU_Power",
            IsAvailable = hasGpuPower || hasGpuEngine,
            Status = hasGpuPower ? "Available"
                : (hasGpuEngine ? "Partial"
                : (hasLevelZero ? "Requires admin" : "Unavailable")),
            Details = hasGpuPower
                ? $"Reading from LHM: {sensorCount} GPU sensors"
                : (hasGpuEngine
                ? "Using GPU Engine utilization as fallback; precise power unavailable"
                : "No iGPU power sensor detected"),
            RequiresAdmin = !hasGpuPower
        };
    }
}
