using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;

namespace PowerCulprit.Collectors;

/// <summary>
/// Collects system power status from Windows battery API.
/// Uses Windows.Devices.Power.Battery.AggregateBattery.GetReport()
/// with fallback to capacity-change estimation.
/// </summary>
public class BatteryPowerCollector
{
    private readonly ILogger<BatteryPowerCollector> _logger;
    private double? _previousRemainingCapacityMWh;
    private DateTime? _previousSampleTime;

    public BatteryPowerCollector(ILogger<BatteryPowerCollector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Collects a single SystemPowerSample. Returns null if no battery is present.
    /// Failures are logged and surfaced via SourceStatus, not thrown.
    /// </summary>
    public SystemPowerSample? Collect()
    {
        var timestampUtc = DateTime.UtcNow;
        bool? isAcOnline = null;
        string? powerMode = null;
        double? batteryPercent = null;
        double? chargeRateMilliwatts = null;
        double? remainingCapacityMWh = null;
        double? fullChargeCapacityMWh = null;
        double? estimatedDischargeWatts = null;

        try
        {
            // ── AC status ──────────────────────────────
            isAcOnline = SystemInformation.IsAcOnline();
            powerMode = SystemInformation.GetPowerMode();

            // ── Battery report ─────────────────────────
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();

            if (report is null)
            {
                _logger.LogDebug("Battery report is null — no battery present");
                return new SystemPowerSample
                {
                    TimestampUtc = timestampUtc,
                    IsAcOnline = isAcOnline,
                    PowerMode = powerMode
                };
            }

            // Battery percentage
            if (report.RemainingCapacityInMilliwattHours.HasValue &&
                report.FullChargeCapacityInMilliwattHours.HasValue &&
                report.FullChargeCapacityInMilliwattHours.Value > 0)
            {
                remainingCapacityMWh = report.RemainingCapacityInMilliwattHours.Value;
                fullChargeCapacityMWh = report.FullChargeCapacityInMilliwattHours.Value;
                batteryPercent = Math.Round(
                    (double)remainingCapacityMWh.Value /
                    fullChargeCapacityMWh.Value * 100.0, 1);
            }

            // Charge rate (may be negative when discharging)
            if (report.ChargeRateInMilliwatts.HasValue)
            {
                chargeRateMilliwatts = report.ChargeRateInMilliwatts.Value;
            }

            // Estimate discharge via capacity change fallback
            if (chargeRateMilliwatts is null &&
                remainingCapacityMWh.HasValue &&
                _previousRemainingCapacityMWh.HasValue &&
                _previousSampleTime.HasValue)
            {
                var deltaMWh = _previousRemainingCapacityMWh.Value - remainingCapacityMWh.Value;
                var deltaHours = (timestampUtc - _previousSampleTime.Value).TotalHours;
                if (deltaHours > 0 && deltaMWh > 0)
                {
                    var estimatedMw = deltaMWh / deltaHours;
                    estimatedDischargeWatts = Math.Round(estimatedMw / 1000.0, 2);
                }
            }

            // Update previous values for next cycle
            _previousRemainingCapacityMWh = remainingCapacityMWh;
            _previousSampleTime = timestampUtc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BatteryPowerCollector.Collect failed");
        }

        return new SystemPowerSample
        {
            TimestampUtc = timestampUtc,
            IsAcOnline = isAcOnline,
            BatteryPercent = batteryPercent,
            ChargeRateMilliwatts = chargeRateMilliwatts,
            RemainingCapacityMWh = remainingCapacityMWh,
            FullChargeCapacityMWh = fullChargeCapacityMWh,
            EstimatedDischargeWatts = estimatedDischargeWatts,
            PowerMode = powerMode
        };
    }

    /// <summary>
    /// Returns the current SourceStatus for this collector.
    /// </summary>
    public SourceStatus GetStatus()
    {
        try
        {
            var report = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            var hasBattery = report is not null;
            var hasChargeRate = report?.ChargeRateInMilliwatts is not null;

            return new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = "BatteryAPI",
                IsAvailable = hasBattery,
                Status = hasBattery
                    ? (hasChargeRate ? SourceStatusStrings.Available : SourceStatusStrings.Partial)
                    : SourceStatusStrings.Unavailable,
                Details = hasBattery && !hasChargeRate
                    ? "ChargeRateInMilliwatts not available; using capacity-change estimation"
                    : null,
                RequiresAdmin = false
            };
        }
        catch (Exception ex)
        {
            return new SourceStatus
            {
                TimestampUtc = DateTime.UtcNow,
                SourceName = "BatteryAPI",
                IsAvailable = false,
                Status = SourceStatusStrings.Unavailable,
                Details = $"Exception: {ex.Message}"
            };
        }
    }
}
