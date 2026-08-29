namespace PowerCulprit.Core.Analysis;

/// <summary>
/// A paired whole-system battery and CPU-package power observation.
/// Pairing the readings at collection time avoids comparing unrelated points.
/// </summary>
public readonly record struct PowerBreakdownObservation(
    DateTime TimestampUtc,
    double? BatteryDischargeWatts,
    double? CpuPackagePowerWatts);

/// <summary>
/// A smoothed, package-based split of battery power. Non-CPU power is a
/// residual, not a direct measurement of individual peripherals.
/// </summary>
public sealed record PowerBreakdownResult
{
    public double BatteryDischargeWatts { get; init; }
    public double CpuPackagePowerWatts { get; init; }
    public double NonCpuResidualWatts { get; init; }
    public double? CpuSharePercent { get; init; }
    public double? NonCpuSharePercent { get; init; }
    public bool IsReliable { get; init; }
    public int ObservationCount { get; init; }
    public TimeSpan ObservedDuration { get; init; }
}

/// <summary>
/// Calculates a time-weighted CPU-package versus non-CPU residual split.
/// Power domains such as cores, DRAM, platform and GPU are intentionally not
/// added because they may be subsets of or overlap the package domain.
/// </summary>
public static class PowerBreakdownCalculator
{
    private const double MaximumCredibleCpuToBatteryRatio = 1.10;

    public static PowerBreakdownResult? Calculate(
        IReadOnlyList<PowerBreakdownObservation> observations)
    {
        var valid = observations
            .Where(IsValid)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        if (valid.Count == 0)
            return null;

        double batteryWattHours = 0;
        double cpuWattHours = 0;
        double observedHours = 0;

        for (var i = 1; i < valid.Count; i++)
        {
            var previous = valid[i - 1];
            var current = valid[i];
            var span = current.TimestampUtc - previous.TimestampUtc;
            if (span <= TimeSpan.Zero || span > GapDetector.MaxSampleGap)
                continue;

            var hours = span.TotalHours;
            batteryWattHours += (previous.BatteryDischargeWatts!.Value + current.BatteryDischargeWatts!.Value) / 2.0 * hours;
            cpuWattHours += (previous.CpuPackagePowerWatts!.Value + current.CpuPackagePowerWatts!.Value) / 2.0 * hours;
            observedHours += hours;
        }

        // A single valid point is still useful during startup. Once multiple
        // points arrive, the trapezoidal average prevents irregular sampling
        // cadence from biasing the split.
        var batteryWatts = observedHours > 0
            ? batteryWattHours / observedHours
            : valid.Average(sample => sample.BatteryDischargeWatts!.Value);
        var cpuWatts = observedHours > 0
            ? cpuWattHours / observedHours
            : valid.Average(sample => sample.CpuPackagePowerWatts!.Value);

        var rawCpuShare = cpuWatts / batteryWatts;
        var isReliable = rawCpuShare <= MaximumCredibleCpuToBatteryRatio;
        var cpuShare = isReliable ? Math.Clamp(rawCpuShare, 0, 1) : (double?)null;

        return new PowerBreakdownResult
        {
            BatteryDischargeWatts = batteryWatts,
            CpuPackagePowerWatts = cpuWatts,
            NonCpuResidualWatts = Math.Max(0, batteryWatts - cpuWatts),
            CpuSharePercent = cpuShare * 100,
            NonCpuSharePercent = (1 - cpuShare) * 100,
            IsReliable = isReliable,
            ObservationCount = valid.Count,
            ObservedDuration = valid.Count > 1 ? valid[^1].TimestampUtc - valid[0].TimestampUtc : TimeSpan.Zero
        };
    }

    private static bool IsValid(PowerBreakdownObservation sample)
        => sample.BatteryDischargeWatts is > 0 &&
           double.IsFinite(sample.BatteryDischargeWatts.Value) &&
           sample.CpuPackagePowerWatts is >= 0 &&
           double.IsFinite(sample.CpuPackagePowerWatts.Value);
}
