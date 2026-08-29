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

        var energyUsed = CalculateEnergyUsed(ordered);
        var batterySamples = ordered.Where(s => s.IsAcOnline == false).ToList();
        var batteryDrop = CalculateBatteryDrop(batterySamples);
        var clockValues = Values(batterySamples.Select(s => s.CpuAverageClockMhz));
        var loadValues = Values(batterySamples.Select(s => s.CpuLoadPercent));
        var powerValues = Values(batterySamples.Select(s => s.CpuPackagePowerWatts));
        var platformPowerValues = Values(batterySamples.Select(s => s.CpuPlatformPowerWatts));
        var coresPowerValues = Values(batterySamples.Select(s => s.CpuCoresPowerWatts));
        var memoryPowerValues = Values(batterySamples.Select(s => s.CpuMemoryPowerWatts));
        var gpuPowerValues = Values(batterySamples.Select(s => s.GpuPowerWatts));
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
            AvgCpuPlatformPowerWatts = Average(platformPowerValues),
            MaxCpuPlatformPowerWatts = Max(platformPowerValues),
            AvgCpuCoresPowerWatts = Average(coresPowerValues),
            MaxCpuCoresPowerWatts = Max(coresPowerValues),
            AvgCpuMemoryPowerWatts = Average(memoryPowerValues),
            MaxCpuMemoryPowerWatts = Max(memoryPowerValues),
            AvgGpuPowerWatts = Average(gpuPowerValues),
            MaxGpuPowerWatts = Max(gpuPowerValues),
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
            var span = current.TimestampUtc - previous.TimestampUtc;
            if (span <= TimeSpan.Zero)
                continue;
            if (previous.IsAcOnline != false || current.IsAcOnline != false)
                continue;

            // Skip unobserved intervals (sleep / hibernate / monitoring gap) —
            // never extrapolate boundary wattage across a gap. Reusing
            // GapDetector.MaxSampleGap keeps the "what counts as a gap" notion
            // identical to DischargeEnergyCalculator and CpuTimelineBuilder, so
            // the CPU energy total agrees with the battery energy total on what
            // was awake. Without this, a 2-hour suspend would be integrated as
            // boundary watts × 2h and massively over-count CPU energy.
            if (span > GapDetector.MaxSampleGap)
                continue;

            var elapsedHours = span.TotalHours;
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
                previous.IsAcOnline != false ||
                current.IsAcOnline != false ||
                !previous.CumulativeEnergyWh.HasValue ||
                !current.CumulativeEnergyWh.HasValue)
            {
                continue;
            }

            var span = current.TimestampUtc - previous.TimestampUtc;
            if (span <= TimeSpan.Zero)
                continue;

            // Skip unobserved intervals — a span larger than GapDetector.MaxSampleGap
            // is a sleep/hibernate/monitoring gap. Pairing boundary power with the
            // near-zero cumulative-energy delta across hours would inject bogus
            // zero-rate points and distort the Pearson coefficient.
            if (span > GapDetector.MaxSampleGap)
                continue;

            var elapsedHours = span.TotalHours;

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
            parts.Add($"CPU package averaged {result.AvgCpuPackagePowerWatts.Value:F1} W while on battery");
        else
            parts.Add("CPU package power is unavailable");

        if (result.CpuPackageEnergyWh.HasValue)
            parts.Add($"CPU package energy while on battery was about {result.CpuPackageEnergyWh.Value:F2} Wh");

        if (result.AvgCpuPlatformPowerWatts.HasValue || result.AvgCpuCoresPowerWatts.HasValue ||
            result.AvgCpuMemoryPowerWatts.HasValue || result.AvgGpuPowerWatts.HasValue)
        {
            parts.Add("platform/package/core/memory/GPU domains are overlapping observations and are not summed");
        }

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
