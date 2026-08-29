namespace PowerCulprit.Core.Models;

/// <summary>
/// Summary of CPU behavior and its relationship to discharge over a selected range.
/// </summary>
public sealed record CpuAttributionResult
{
    public double? BatteryDropPercent { get; init; }
    public double? EnergyUsedWh { get; init; }
    public double? AvgCpuClockMhz { get; init; }
    public double? MaxCpuClockMhz { get; init; }
    public double? AvgCpuLoadPercent { get; init; }
    public double? MaxCpuLoadPercent { get; init; }
    public double? AvgCpuPackagePowerWatts { get; init; }
    public double? MaxCpuPackagePowerWatts { get; init; }
    public double? AvgCpuPlatformPowerWatts { get; init; }
    public double? MaxCpuPlatformPowerWatts { get; init; }
    public double? AvgCpuCoresPowerWatts { get; init; }
    public double? MaxCpuCoresPowerWatts { get; init; }
    public double? AvgCpuMemoryPowerWatts { get; init; }
    public double? MaxCpuMemoryPowerWatts { get; init; }
    public double? AvgGpuPowerWatts { get; init; }
    public double? MaxGpuPowerWatts { get; init; }
    public double? CpuPackageEnergyWh { get; init; }
    public double? CpuPowerDischargeCorrelation { get; init; }
    public string Summary { get; init; } = string.Empty;
}
