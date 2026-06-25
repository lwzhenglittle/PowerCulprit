using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Derives Intel CPU power/load/temperature/clock readings from
/// LibreHardwareMonitorCollector samples.
/// Falls back: Intel Power Gadget / Intel PCM probing.
/// Returns null when no data source is available.
/// </summary>
public class IntelCpuPowerCollector
{
    private readonly ILogger<IntelCpuPowerCollector> _logger;

    public IntelCpuPowerCollector(ILogger<IntelCpuPowerCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Filters the LHM sensor batch for CPU power/load/temp/clock readings.
    /// Returns only samples relevant to Intel CPU package.
    /// </summary>
    public IReadOnlyList<HardwareSensorSample> FilterFromLhm(
        IReadOnlyList<HardwareSensorSample> lhmSamples)
    {
        if (lhmSamples.Count == 0)
            return Array.Empty<HardwareSensorSample>();

        // Filter for CPU-relevant samples
        var cpuSamples = lhmSamples
            .Where(s =>
                s.DeviceName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                s.DeviceName.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("IA Cores", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("CPU Core", StringComparison.OrdinalIgnoreCase) ||
                s.SensorName.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return cpuSamples.AsReadOnly();
    }

    /// <summary>
    /// Returns the best available CPU package power value in watts, or null.
    /// </summary>
    public double? GetCpuPackagePowerWatts(IReadOnlyList<HardwareSensorSample> lhmSamples)
    {
        // Look for CPU Package Power specifically
        var packageSample = lhmSamples
            .FirstOrDefault(s =>
                s.MetricName == "Power" &&
                s.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase) &&
                s.DeviceName.Contains("CPU", StringComparison.OrdinalIgnoreCase));

        if (packageSample is not null)
            return packageSample.Value;

        // Try "CPU Package" sensor name
        packageSample = lhmSamples
            .FirstOrDefault(s =>
                s.MetricName == "Power" &&
                s.SensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase));

        return packageSample?.Value;
    }

    /// <summary>
    /// Returns the SourceStatus for Intel CPU power data.
    /// </summary>
    public SourceStatus GetStatus(IReadOnlyList<HardwareSensorSample>? lhmSamples = null)
    {
        var hasCpuPower = false;
        var sensorCount = 0;

        if (lhmSamples is not null)
        {
            var filtered = FilterFromLhm(lhmSamples);
            sensorCount = filtered.Count;
            hasCpuPower = filtered.Any(s =>
                s.MetricName == "Power" &&
                s.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase));
        }

        // Probe Intel tools
        var hasPcm = File.Exists(@"C:\Program Files\Intel\PCM\pcm.exe") ||
                     File.Exists(@"C:\Windows\System32\pcm.exe");
        var hasPowerGadget = File.Exists(@"C:\Program Files\Intel\Power Gadget 3.6\PowerLog3.0.exe");

        return new SourceStatus
        {
            TimestampUtc = DateTime.UtcNow,
            SourceName = "CPU_Package_Power",
            IsAvailable = hasCpuPower,
            Status = hasCpuPower ? "Available"
                : (sensorCount > 0 ? "Partial"
                : (hasPcm || hasPowerGadget ? "Requires admin" : "Unavailable")),
            Details = hasCpuPower
                ? $"Reading from LHM: {sensorCount} CPU sensors"
                : (hasPcm ? "Intel PCM detected; may work with admin"
                : (hasPowerGadget ? "Intel Power Gadget detected; may work with admin"
                : "No CPU power sensor detected — try running as administrator")),
            RequiresAdmin = !hasCpuPower
        };
    }
}
