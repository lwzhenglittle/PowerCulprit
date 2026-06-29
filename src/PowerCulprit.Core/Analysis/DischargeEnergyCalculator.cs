using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Discharge-rate extraction and energy integration over battery samples.
/// </summary>
/// <remarks>
/// Energy integration is gap-aware: any segment spanning an unobserved
/// interval (sleep / hibernate / monitoring gap larger than
/// <see cref="GapDetector.MaxSampleGap"/>) is skipped, so the result reflects
/// energy used while awake rather than extrapolating boundary wattage across
/// the gap. Reusing <see cref="GapDetector.MaxSampleGap"/> keeps the
/// "what counts as a gap" notion identical to cycle building.
/// </remarks>
public static class DischargeEnergyCalculator
{
    /// <summary>
    /// Discharge rate in watts for a sample: the absolute charge rate when
    /// discharging, the estimated rate as a fallback, otherwise 0 (charging)
    /// or null (no rate available at all).
    /// </summary>
    public static double? GetDischargeWatts(SystemPowerSample sample)
    {
        if (sample.ChargeRateMilliwatts.HasValue)
        {
            var chargeRateW = sample.ChargeRateMilliwatts.Value / 1000.0;
            return chargeRateW < 0 ? Math.Abs(chargeRateW) : 0.0;
        }

        return sample.EstimatedDischargeWatts;
    }

    /// <summary>
    /// Trapezoidal energy (Wh) over the given samples, expected in
    /// timestamp order. Segments whose span exceeds
    /// <see cref="GapDetector.MaxSampleGap"/> are skipped — such a span is an
    /// unobserved interval (sleep / hibernate / gap), and integrating across
    /// it would extrapolate boundary wattage over hours of missing data and
    /// massively over-count energy.
    /// </summary>
    public static double? CalculateEnergyUsedWh(IReadOnlyList<SystemPowerSample> samples)
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

            // Skip unobserved intervals — never extrapolate across a gap.
            if (span > GapDetector.MaxSampleGap)
                continue;

            var elapsedHours = span.TotalHours;

            var previousWatts = GetDischargeWatts(previous);
            var currentWatts = GetDischargeWatts(current);
            if (!previousWatts.HasValue && !currentWatts.HasValue)
                continue;

            var watts = (Math.Max(0, previousWatts ?? currentWatts!.Value) + Math.Max(0, currentWatts ?? previousWatts!.Value)) / 2.0;
            totalWh += watts * elapsedHours;
            hasPower = true;
        }

        return hasPower ? totalWh : null;
    }
}
