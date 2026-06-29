using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Detects unobserved intervals from power state events and sample continuity gaps.
/// ConfirmedSleep intervals require a Suspend→Resume pair recorded by PowerCulprit.
/// UnknownGap intervals are inferred when consecutive power samples have a gap
/// larger than <see cref="MaxSampleGap"/> and are not already covered by a
/// confirmed sleep interval.
/// </summary>
public static class GapDetector
{
    /// <summary>
    /// Maximum gap between consecutive system power samples before an
    /// UnknownGap interval is emitted. Defaults to 10 minutes.
    /// </summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Detect sleep and unknown-gap intervals from power state events
    /// and battery sample continuity. Only complete Suspend→Resume pairs
    /// produce ConfirmedSleep intervals.
    /// </summary>
    public static IReadOnlyList<PowerStateInterval> Detect(
        IReadOnlyList<PowerStateEvent> powerStateEvents,
        IReadOnlyList<SystemPowerSample> powerSamples,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var sleepIntervals = BuildConfirmedSleepIntervals(powerStateEvents);
        var gapIntervals = BuildSampleGapIntervals(powerSamples, fromUtc, toUtc);
        var combined = MergeIntervals(sleepIntervals.Concat(gapIntervals).ToList());
        return combined;
    }

    /// <summary>
    /// Detect only confirmed-sleep intervals (ignoring unknown gaps).
    /// </summary>
    public static IReadOnlyList<PowerStateInterval> DetectSleepOnly(
        IReadOnlyList<PowerStateEvent> powerStateEvents)
        => BuildConfirmedSleepIntervals(powerStateEvents);

    private static List<PowerStateInterval> BuildConfirmedSleepIntervals(
        IReadOnlyList<PowerStateEvent> events)
    {
        var intervals = new List<PowerStateInterval>();
        PowerStateEvent? pendingSuspend = null;

        foreach (var evt in events.OrderBy(e => e.TimestampUtc))
        {
            if (evt.Kind == PowerStateEventKind.Suspend)
            {
                // Use the latest suspend if we get multiple before a resume.
                pendingSuspend = evt;
                continue;
            }

            if (evt.Kind is PowerStateEventKind.Resume or PowerStateEventKind.ResumeAutomatic)
            {
                if (pendingSuspend is not null &&
                    evt.TimestampUtc > pendingSuspend.TimestampUtc)
                {
                    intervals.Add(new PowerStateInterval
                    {
                        StartUtc = pendingSuspend.TimestampUtc,
                        EndUtc = evt.TimestampUtc,
                        Kind = GapKind.ConfirmedSleep
                    });
                }

                pendingSuspend = null;
            }
        }

        return intervals;
    }

    private static List<PowerStateInterval> BuildSampleGapIntervals(
        IReadOnlyList<SystemPowerSample> powerSamples,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (powerSamples.Count == 0)
            return new List<PowerStateInterval>();

        var ordered = powerSamples
            .Where(s => s.TimestampUtc >= fromUtc && s.TimestampUtc <= toUtc)
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        var gaps = new List<PowerStateInterval>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].TimestampUtc - ordered[i - 1].TimestampUtc;
            if (gap > MaxSampleGap)
            {
                gaps.Add(new PowerStateInterval
                {
                    StartUtc = ordered[i - 1].TimestampUtc,
                    EndUtc = ordered[i].TimestampUtc,
                    Kind = GapKind.UnknownGap
                });
            }
        }

        return gaps;
    }

    private static List<PowerStateInterval> MergeIntervals(
        List<PowerStateInterval> intervals)
    {
        if (intervals.Count <= 1)
            return intervals;

        // Sort by start time; ConfirmedSleep takes priority over UnknownGap.
        var sorted = intervals
            .OrderBy(i => i.StartUtc)
            .ThenBy(i => i.Kind == GapKind.ConfirmedSleep ? 0 : 1)
            .ToList();

        var merged = new List<PowerStateInterval> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            var current = sorted[i];

            if (current.StartUtc <= last.EndUtc)
            {
                // Overlapping or adjacent — merge. ConfirmedSleep wins.
                var end = current.EndUtc > last.EndUtc ? current.EndUtc : last.EndUtc;
                var kind = last.Kind == GapKind.ConfirmedSleep ||
                           current.Kind == GapKind.ConfirmedSleep
                    ? GapKind.ConfirmedSleep
                    : GapKind.UnknownGap;

                merged[^1] = last with
                {
                    EndUtc = end,
                    Kind = kind
                };
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }
}
