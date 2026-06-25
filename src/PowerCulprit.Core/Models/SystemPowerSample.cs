namespace PowerCulprit.Core.Models;

/// <summary>
/// A single sample of system-wide power state.
/// </summary>
public record SystemPowerSample
{
    /// <summary>UTC timestamp of the sample.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Whether the device is connected to AC power.</summary>
    public bool IsAcOnline { get; init; }

    /// <summary>Battery charge percentage (0–100), or null if unavailable.</summary>
    public double? BatteryPercent { get; init; }

    /// <summary>
    /// Charge/discharge rate in milliwatts.
    /// Positive when charging, negative when discharging.
    /// </summary>
    public double? ChargeRateMilliwatts { get; init; }

    /// <summary>Remaining battery capacity in milliwatt-hours.</summary>
    public double? RemainingCapacityMWh { get; init; }

    /// <summary>Full charge capacity in milliwatt-hours.</summary>
    public double? FullChargeCapacityMWh { get; init; }

    /// <summary>
    /// Estimated discharge rate in watts, derived when ChargeRate is unavailable.
    /// </summary>
    public double? EstimatedDischargeWatts { get; init; }

    /// <summary>Windows power mode (e.g., BestPowerEfficiency, Balanced, HighPerformance).</summary>
    public string? PowerMode { get; init; }
}
