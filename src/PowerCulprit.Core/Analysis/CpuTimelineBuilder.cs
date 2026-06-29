using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Builds a compact CPU timeline from raw LHM sensor rows and system power samples.
/// </summary>
public static class CpuTimelineBuilder
{
    public static IReadOnlyList<CpuTimelineSample> Build(
        IReadOnlyList<SystemPowerSample> powerSamples,
        IReadOnlyList<HardwareSensorSample> hardwareSamples)
    {
        if (powerSamples.Count == 0 && hardwareSamples.Count == 0)
            return Array.Empty<CpuTimelineSample>();

        var orderedPower = powerSamples
            .OrderBy(s => s.TimestampUtc)
            .ToList();
        var energyByTimestamp = BuildEnergyTimeline(orderedPower);

        var cpuByTimestamp = hardwareSamples
            .Where(IsCpuSensor)
            .GroupBy(s => s.TimestampUtc)
            .ToDictionary(g => g.Key, BuildCpuValues);

        var timestamps = orderedPower
            .Select(s => s.TimestampUtc)
            .Concat(cpuByTimestamp.Keys)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var result = new List<CpuTimelineSample>(timestamps.Count);
        foreach (var ts in timestamps)
        {
            var power = FindNearestPowerSample(orderedPower, ts);
            energyByTimestamp.TryGetValue(power?.TimestampUtc ?? DateTime.MinValue, out var energyWh);
            cpuByTimestamp.TryGetValue(ts, out var cpu);

            result.Add(new CpuTimelineSample(
                ts,
                power?.BatteryPercent,
                power is null ? null : energyWh,
                cpu?.CpuAverageClockMhz,
                cpu?.CpuLoadPercent,
                cpu?.CpuPackagePowerWatts));
        }

        return result;
    }

    private static Dictionary<DateTime, double?> BuildEnergyTimeline(IReadOnlyList<SystemPowerSample> samples)
    {
        var result = new Dictionary<DateTime, double?>();
        if (samples.Count == 0)
            return result;

        double totalWh = 0;
        var hasPower = false;
        result[samples[0].TimestampUtc] = 0;

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            var elapsedHours = (current.TimestampUtc - previous.TimestampUtc).TotalHours;
            if (elapsedHours <= 0)
            {
                result[current.TimestampUtc] = hasPower ? totalWh : null;
                continue;
            }

            var previousWatts = GetDischargeWatts(previous);
            var currentWatts = GetDischargeWatts(current);
            if (previousWatts.HasValue || currentWatts.HasValue)
            {
                var left = Math.Max(0, previousWatts ?? currentWatts!.Value);
                var right = Math.Max(0, currentWatts ?? previousWatts!.Value);
                totalWh += (left + right) / 2.0 * elapsedHours;
                hasPower = true;
            }

            result[current.TimestampUtc] = hasPower ? Math.Round(totalWh, 4) : null;
        }

        return result;
    }

    private static CpuValues BuildCpuValues(IEnumerable<HardwareSensorSample> samples)
    {
        var list = samples.ToList();

        var clocks = list
            .Where(s => s.MetricName == "Clock" && UnitEquals(s, "MHz") && IsCoreClockSensor(s))
            .Select(s => s.Value)
            .ToList();

        var load = list.FirstOrDefault(s =>
            s.MetricName == "Load" &&
            UnitEquals(s, "%") &&
            s.SensorName.Equals("CPU Total", StringComparison.OrdinalIgnoreCase));

        load ??= list.FirstOrDefault(s =>
            s.MetricName == "Load" &&
            UnitEquals(s, "%") &&
            s.SensorName.Contains("Total", StringComparison.OrdinalIgnoreCase));

        var packagePower = list.FirstOrDefault(s =>
            s.MetricName == "Power" &&
            UnitEquals(s, "W") &&
            s.SensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase));

        packagePower ??= list.FirstOrDefault(s =>
            s.MetricName == "Power" &&
            UnitEquals(s, "W") &&
            s.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase));

        return new CpuValues(
            clocks.Count > 0 ? Math.Round(clocks.Average(), 3) : null,
            load?.Value,
            packagePower?.Value);
    }

    private static SystemPowerSample? FindNearestPowerSample(IReadOnlyList<SystemPowerSample> samples, DateTime timestampUtc)
    {
        if (samples.Count == 0)
            return null;

        var best = samples[0];
        var bestTicks = Math.Abs((best.TimestampUtc - timestampUtc).Ticks);
        for (var i = 1; i < samples.Count; i++)
        {
            var ticks = Math.Abs((samples[i].TimestampUtc - timestampUtc).Ticks);
            if (ticks < bestTicks)
            {
                best = samples[i];
                bestTicks = ticks;
            }
        }

        return best;
    }

    private static bool IsCpuSensor(HardwareSensorSample sample)
        => sample.DeviceName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
           sample.DeviceName.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
           sample.SensorName.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
           sample.SensorName.Contains("P-Core", StringComparison.OrdinalIgnoreCase) ||
           sample.SensorName.Contains("E-Core", StringComparison.OrdinalIgnoreCase) ||
           sample.SensorName.Contains("Package", StringComparison.OrdinalIgnoreCase);

    private static bool IsCoreClockSensor(HardwareSensorSample sample)
    {
        if (sample.SensorName.Contains("Bus", StringComparison.OrdinalIgnoreCase))
            return false;

        return sample.SensorName.Contains("P-Core", StringComparison.OrdinalIgnoreCase) ||
               sample.SensorName.Contains("E-Core", StringComparison.OrdinalIgnoreCase) ||
               sample.SensorName.Contains("CPU Core", StringComparison.OrdinalIgnoreCase) ||
               sample.SensorName.StartsWith("Core #", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UnitEquals(HardwareSensorSample sample, string unit)
        => string.Equals(sample.Unit, unit, StringComparison.OrdinalIgnoreCase);

    private static double? GetDischargeWatts(SystemPowerSample sample)
    {
        if (sample.ChargeRateMilliwatts.HasValue)
        {
            var chargeRateW = sample.ChargeRateMilliwatts.Value / 1000.0;
            return chargeRateW < 0 ? Math.Abs(chargeRateW) : 0.0;
        }

        return sample.EstimatedDischargeWatts;
    }

    private sealed record CpuValues(
        double? CpuAverageClockMhz,
        double? CpuLoadPercent,
        double? CpuPackagePowerWatts);
}
