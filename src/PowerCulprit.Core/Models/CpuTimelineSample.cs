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
    double? CpuPackagePowerWatts)
{
    public bool? IsAcOnline { get; init; }
    public double? CpuPlatformPowerWatts { get; init; }
    public double? CpuCoresPowerWatts { get; init; }
    public double? CpuMemoryPowerWatts { get; init; }
    public double? GpuPowerWatts { get; init; }
}
