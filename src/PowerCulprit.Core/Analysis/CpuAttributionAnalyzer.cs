using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Summarizes CPU sensor timelines and their relationship to discharge.
/// </summary>
public static class CpuAttributionAnalyzer
{
    public static CpuAttributionResult Analyze(IReadOnlyList<CpuTimelineSample> samples)
    {
        var ordered = samples.OrderBy(s => s.TimestampUtc).ToList();
        if (ordered.Count == 0)
        {
            return new CpuAttributionResult
            {
                Summary = "No CPU attribution data is available for this range."
            };
        }

        var batteryDrop = CalculateBatteryDrop(ordered);
        var energyUsed = CalculateEnergyUsed(ordered);
        var clockValues = Values(ordered.Select(s => s.CpuAverageClockMhz));
        var loadValues = Values(ordered.Select(s => s.CpuLoadPercent));
        var powerValues = Values(ordered.Select(s => s.CpuPackagePowerWatts));
        var cpuEnergyWh = CalculateCpuEnergyWh(ordered);
        var correlation = ComputeCpuPowerEnergyCorrelation(ordered);

        var result = new CpuAttributionResult
        {
            BatteryDropPercent = batteryDrop,
            EnergyUsedWh = energyUsed,
            AvgCpuClockMhz = Average(clockValues),
            MaxCpuClockMhz = Max(clockValues),
            AvgCpuLoadPercent = Average(loadValues),
            MaxCpuLoadPercent = Max(loadValues),
            AvgCpuPackagePowerWatts = Average(powerValues),
            MaxCpuPackagePowerWatts = Max(powerValues),
            CpuPackageEnergyWh = cpuEnergyWh,
            CpuPowerDischargeCorrelation = correlation
        };

        return result with { Summary = BuildSummary(result) };
    }

    private static double? CalculateBatteryDrop(IReadOnlyList<CpuTimelineSample> samples)
    {
        var first = samples.FirstOrDefault(s => s.BatteryPercent.HasValue)?.BatteryPercent;
        var last = samples.LastOrDefault(s => s.BatteryPercent.HasValue)?.BatteryPercent;
        if (!first.HasValue || !last.HasValue)
            return null;

        return Math.Round(Math.Max(0, first.Value - last.Value), 2);
    }

    private static double? CalculateEnergyUsed(IReadOnlyList<CpuTimelineSample> samples)
    {
        var first = samples.FirstOrDefault(s => s.CumulativeEnergyWh.HasValue)?.CumulativeEnergyWh;
        var last = samples.LastOrDefault(s => s.CumulativeEnergyWh.HasValue)?.CumulativeEnergyWh;
        if (!first.HasValue || !last.HasValue)
            return null;

        return Math.Round(Math.Max(0, last.Value - first.Value), 4);
    }

    private static double? CalculateCpuEnergyWh(IReadOnlyList<CpuTimelineSample> samples)
    {
        if (samples.Count < 2)
            return null;

        double totalWh = 0;
        var hasPower = false;
        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            var elapsedHours = (current.TimestampUtc - previous.TimestampUtc).TotalHours;
            if (elapsedHours <= 0)
                continue;

            var previousWatts = previous.CpuPackagePowerWatts;
            var currentWatts = current.CpuPackagePowerWatts;
            if (!previousWatts.HasValue && !currentWatts.HasValue)
                continue;

            var watts = ((previousWatts ?? currentWatts!.Value) + (currentWatts ?? previousWatts!.Value)) / 2.0;
            totalWh += watts * elapsedHours;
            hasPower = true;
        }

        return hasPower ? Math.Round(totalWh, 4) : null;
    }

    private static double? ComputeCpuPowerEnergyCorrelation(IReadOnlyList<CpuTimelineSample> samples)
    {
        var power = new List<double>();
        var energyDelta = new List<double>();

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            if (!current.CpuPackagePowerWatts.HasValue ||
                !previous.CumulativeEnergyWh.HasValue ||
                !current.CumulativeEnergyWh.HasValue)
            {
                continue;
            }

            var elapsedHours = (current.TimestampUtc - previous.TimestampUtc).TotalHours;
            if (elapsedHours <= 0)
                continue;

            power.Add(current.CpuPackagePowerWatts.Value);
            energyDelta.Add((current.CumulativeEnergyWh.Value - previous.CumulativeEnergyWh.Value) / elapsedHours);
        }

        return CorrelationHelper.Pearson(power, energyDelta);
    }

    private static string BuildSummary(CpuAttributionResult result)
    {
        var parts = new List<string>();

        if (result.EnergyUsedWh.HasValue)
            parts.Add($"selected range used {result.EnergyUsedWh.Value:F2} Wh");
        else if (result.BatteryDropPercent.HasValue)
            parts.Add($"battery dropped {result.BatteryDropPercent.Value:F1}%");
        else
            parts.Add("no battery discharge energy is available");

        if (result.AvgCpuPackagePowerWatts.HasValue)
            parts.Add($"CPU package averaged {result.AvgCpuPackagePowerWatts.Value:F1} W");
        else
            parts.Add("CPU package power is unavailable");

        if (result.CpuPackageEnergyWh.HasValue)
            parts.Add($"CPU package energy was about {result.CpuPackageEnergyWh.Value:F2} Wh");

        if (result.AvgCpuLoadPercent.HasValue)
            parts.Add($"CPU load averaged {result.AvgCpuLoadPercent.Value:F1}%");

        if (result.AvgCpuClockMhz.HasValue)
            parts.Add($"average core clock was {result.AvgCpuClockMhz.Value / 1000.0:F2} GHz");

        if (result.CpuPowerDischargeCorrelation.HasValue)
            parts.Add($"CPU power/discharge correlation {result.CpuPowerDischargeCorrelation.Value:F2}");
        else
            parts.Add("not enough paired CPU power and discharge samples for correlation");

        return string.Join("; ", parts) + ".";
    }

    private static List<double> Values(IEnumerable<double?> values)
        => values.Where(v => v.HasValue).Select(v => v!.Value).ToList();

    private static double? Average(IReadOnlyList<double> values)
        => values.Count == 0 ? null : Math.Round(values.Average(), 3);

    private static double? Max(IReadOnlyList<double> values)
        => values.Count == 0 ? null : Math.Round(values.Max(), 3);
}
