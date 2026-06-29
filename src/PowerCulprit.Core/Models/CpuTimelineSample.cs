namespace PowerCulprit.Core.Models;

/// <summary>
/// A CPU-focused timeline sample aligned to a hardware sensor timestamp.
/// Values are nullable because hardware sensors may be unavailable on a machine
/// or may appear only after running with elevated privileges.
/// </summary>
public sealed record CpuTimelineSample(
    DateTime TimestampUtc,
    double? BatteryPercent,
    double? CumulativeEnergyWh,
    double? CpuAverageClockMhz,
    double? CpuLoadPercent,
    double? CpuPackagePowerWatts);
