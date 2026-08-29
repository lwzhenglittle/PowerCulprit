using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

public static class BatteryCycleBuilder
{
    public const double FullChargePercent = 99.5;
    public const double FullChargeCapacityRatio = 0.995;
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(10);

    private const int StableStateSampleCount = 2;

    public static BatteryCycleBuildResult Build(IReadOnlyList<SystemPowerSample> samples)
        => Build(samples, Array.Empty<DateTime>(), Array.Empty<PowerStateInterval>());

    public static BatteryCycleBuildResult Build(
        IReadOnlyList<SystemPowerSample> samples,
        IReadOnlyList<DateTime> sessionStarts)
        => Build(samples, sessionStarts, Array.Empty<PowerStateInterval>());

    /// <summary>
    /// Build battery cycles with awareness of confirmed sleep intervals.
    /// When a large sample gap is covered by a ConfirmedSleep interval the
    /// cycle confidence is NOT lowered — the gap has a known explanation.
    /// </summary>
    public static BatteryCycleBuildResult Build(
        IReadOnlyList<SystemPowerSample> samples,
        IReadOnlyList<DateTime> sessionStarts,
        IReadOnlyList<PowerStateInterval> sleepIntervals)
    {
        var ordered = samples
            .Where(s => s.TimestampUtc != default)
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        var orderedSessionStarts = sessionStarts
            .Where(t => t != default)
            .OrderBy(t => t)
            .ToList();

        var classified = ClassifyAndDebounce(ordered);
        var rawCycles = BuildRawCycles(classified, orderedSessionStarts, sleepIntervals);
        var (assignedRawCycles, displayCycles) = BuildDisplayCycles(rawCycles);
        return new BatteryCycleBuildResult(assignedRawCycles, displayCycles);
    }

    private enum BatteryState
    {
        Unknown,
        Ac,
        Battery
    }

    private sealed record ClassifiedSample(SystemPowerSample Sample, BatteryState State);

    /// <summary>
    /// Normalizes the raw AC signal and suppresses a single contradictory
    /// sample between two stable states. A transition at either end of the
    /// retained history is accepted as-is because there is no look-ahead on
    /// one side; such a cycle is already represented as partial/open where
    /// appropriate.
    /// </summary>
    private static List<ClassifiedSample> ClassifyAndDebounce(
        IReadOnlyList<SystemPowerSample> samples)
    {
        var classified = samples
            .Select(sample => new ClassifiedSample(sample, Classify(sample)))
            .ToList();

        var runs = new List<(int Start, int End, BatteryState State)>();
        for (var index = 0; index < classified.Count;)
        {
            var state = classified[index].State;
            var end = index + 1;
            while (end < classified.Count && classified[end].State == state)
                end++;

            runs.Add((index, end, state));
            index = end;
        }

        for (var runIndex = 1; runIndex < runs.Count - 1; runIndex++)
        {
            var run = runs[runIndex];
            if (run.End - run.Start >= StableStateSampleCount ||
                run.State is not (BatteryState.Ac or BatteryState.Battery))
            {
                continue;
            }

            var previous = runs[runIndex - 1].State;
            var next = runs[runIndex + 1].State;
            if (previous != next || previous is not (BatteryState.Ac or BatteryState.Battery))
                continue;

            for (var index = run.Start; index < run.End; index++)
                classified[index] = classified[index] with { State = previous };
        }

        return classified;
    }

    private static BatteryState Classify(SystemPowerSample sample)
    {
        // An AC flag without any battery telemetry is not enough to establish
        // a battery cycle. This covers desktops/no-battery devices as well as
        // transient Battery API failures without fabricating a discharge run.
        var hasBatteryTelemetry = sample.BatteryPercent.HasValue ||
                                  sample.ChargeRateMilliwatts.HasValue ||
                                  sample.RemainingCapacityMWh.HasValue ||
                                  sample.FullChargeCapacityMWh.HasValue ||
                                  sample.EstimatedDischargeWatts.HasValue;
        if (!hasBatteryTelemetry)
            return BatteryState.Unknown;

        return sample.IsAcOnline switch
        {
            true => BatteryState.Ac,
            false => BatteryState.Battery,
            _ => BatteryState.Unknown
        };
    }

    private static List<BatteryCycle> BuildRawCycles(
        IReadOnlyList<ClassifiedSample> samples,
        IReadOnlyList<DateTime> sessionStarts,
        IReadOnlyList<PowerStateInterval> sleepIntervals)
    {
        var cycles = new List<BatteryCycle>();
        CycleDraft? current = null;
        SystemPowerSample? previous = null;
        var previousState = BatteryState.Unknown;
        var acSessionReachedFull = false;
        var nextSessionStartIndex = 0;

        // Pre-sort sleep intervals for fast gap-coverage lookup.
        var sortedSleep = sleepIntervals
            .Where(i => i.Kind == GapKind.ConfirmedSleep)
            .OrderBy(i => i.StartUtc)
            .ToList();

        foreach (var observation in samples)
        {
            var sample = observation.Sample;
            var largeGap = previous is not null &&
                sample.TimestampUtc - previous.TimestampUtc > MaxSampleGap;

            // A large gap whose entire span is covered by a confirmed-sleep interval
            // is not a signal of low confidence — we know why sampling stopped.
            var knownGap = largeGap &&
                           IsGapCoveredBySleep(
                               previous!.TimestampUtc,
                               sample.TimestampUtc,
                               sortedSleep);

            var sessionBoundary = ConsumeSessionBoundary(
                sessionStarts,
                ref nextSessionStartIndex,
                previous?.TimestampUtc,
                sample.TimestampUtc);

            // An unexplained gap is a physical continuity boundary for this
            // feature. Keep both sides, but do not include the unobserved
            // interval in either cycle.
            if (largeGap && !knownGap && current is not null)
            {
                current.MarkLowConfidence();
                cycles.Add(current.Finish(current.LastObservedUtc, isOpen: false));
                current = null;
            }

            // A session marker records a monitoring restart, not a power
            // transition. It is retained as metadata when a new segment starts
            // but never splits an already continuous battery segment.
            switch (observation.State)
            {
                case BatteryState.Ac:
                    if (current is not null)
                    {
                        cycles.Add(current.Finish(sample.TimestampUtc, isOpen: false));
                        current = null;
                    }

                    acSessionReachedFull = acSessionReachedFull || IsFullCharge(sample);
                    break;

                case BatteryState.Battery:
                    if (current is null)
                    {
                        var partialStart = previous is null || previousState != BatteryState.Ac;
                        var lowConfidence = partialStart || (largeGap && !knownGap);
                        var startedAtFullCharge = IsFullCharge(sample) ||
                            (previousState == BatteryState.Ac && previous is not null && IsFullCharge(previous)) ||
                            acSessionReachedFull;

                        current = CycleDraft.Start(sample, startedAtFullCharge, lowConfidence, sessionBoundary);
                    }
                    else
                    {
                        // A known sleep gap preserves the physical unplugged
                        // segment. The post-sleep boundary sample updates the
                        // endpoint, so capacity lost during sleep remains part
                        // of the cycle; process-level energy integration still
                        // skips the unobserved interval separately.
                        current.AddOfflineSample(sample);
                    }

                    acSessionReachedFull = false;
                    break;

                case BatteryState.Unknown:
                    // Unknown samples neither start nor end a cycle. If one is
                    // observed inside a segment, lower confidence without
                    // pretending that the device was on battery.
                    current?.MarkLowConfidence();
                    break;
            }

            previous = sample;
            previousState = observation.State;
        }

        if (current is not null)
            cycles.Add(current.Finish(endUtc: null, isOpen: true));

        return cycles;
    }

    private static bool ConsumeSessionBoundary(
        IReadOnlyList<DateTime> sessionStarts,
        ref int nextSessionStartIndex,
        DateTime? previousUtc,
        DateTime currentUtc)
    {
        var crossedBoundary = false;
        while (nextSessionStartIndex < sessionStarts.Count &&
               sessionStarts[nextSessionStartIndex] <= currentUtc)
        {
            if (!previousUtc.HasValue || sessionStarts[nextSessionStartIndex] > previousUtc.Value)
                crossedBoundary = true;

            nextSessionStartIndex++;
        }

        return crossedBoundary;
    }

    private static (List<BatteryCycle> RawCycles, List<BatteryDisplayCycle> DisplayCycles)
        BuildDisplayCycles(IReadOnlyList<BatteryCycle> rawCycles)
    {
        var assignedRawCycles = new List<BatteryCycle>();
        var displayCycles = new List<BatteryDisplayCycle>();
        // A display cycle is intentionally one-to-one with a continuous
        // unplugged segment. Separate battery runs must never be merged merely
        // because their combined discharge exceeds a presentation threshold.
        foreach (var cycle in rawCycles)
        {
            var displayId = displayCycles.Count + 1L;
            displayCycles.Add(CreateDisplayCycle(displayId, new[] { cycle }));
            assignedRawCycles.Add(cycle with { DisplayCycleId = displayId });
        }

        return (assignedRawCycles, displayCycles);
    }

    private static BatteryDisplayCycle CreateDisplayCycle(long id, IReadOnlyList<BatteryCycle> group)
    {
        var first = group[0];
        var last = group[^1];
        var confidence = group.Any(c => c.Confidence == BatteryCycleConfidence.Low)
            ? BatteryCycleConfidence.Low
            : BatteryCycleConfidence.High;

        return new BatteryDisplayCycle
        {
            Id = id,
            StartUtc = first.StartUtc,
            EndUtc = last.IsOpen ? null : last.EndUtc,
            LastSampleUtc = last.LastSampleUtc,
            StartBatteryPercent = first.StartBatteryPercent,
            EndBatteryPercent = last.EndBatteryPercent,
            DischargePercent = SumKnown(group.Select(c => c.DischargePercent)),
            DischargeWh = SumKnown(group.Select(c => c.DischargeWh)),
            RawCycleCount = group.Count,
            StartedAtFullCharge = first.StartedAtFullCharge,
            IsOpen = last.IsOpen,
            Confidence = confidence
        };
    }

    private static double? SumKnown(IEnumerable<double?> values)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return known.Count == 0 ? null : Math.Round(known.Sum(), 3);
    }

    private static bool IsFullCharge(SystemPowerSample sample)
    {
        if (sample.BatteryPercent.HasValue &&
            sample.BatteryPercent.Value >= FullChargePercent)
            return true;

        if (sample.RemainingCapacityMWh.HasValue &&
            sample.FullChargeCapacityMWh.HasValue &&
            sample.FullChargeCapacityMWh.Value > 0)
        {
            var ratio = sample.RemainingCapacityMWh.Value / sample.FullChargeCapacityMWh.Value;
            return ratio >= FullChargeCapacityRatio;
        }

        return false;
    }

    /// <summary>
    /// Returns true when a gap between two sample timestamps is entirely
    /// covered by at least one confirmed-sleep interval — meaning the
    /// gap has a known, benign cause and should not lower cycle confidence.
    /// </summary>
    private static bool IsGapCoveredBySleep(
        DateTime gapStart,
        DateTime gapEnd,
        IReadOnlyList<PowerStateInterval> sleepIntervals)
    {
        foreach (var interval in sleepIntervals)
        {
            // The confirmed sleep interval only needs to overlap the sample gap.
            // In normal operation the sleep is contained within [lastSample, nextSample],
            // so a strict containment check would falsely mark cycles as low confidence.
            if (interval.StartUtc < gapEnd && interval.EndUtc > gapStart)
                return true;
        }

        return false;
    }

    private sealed class CycleDraft
    {
        private CycleDraft(
            SystemPowerSample firstSample,
            bool startedAtFullCharge,
            bool lowConfidence,
            bool startedAtSessionBoundary)
        {
            StartUtc = firstSample.TimestampUtc;
            StartBatteryPercent = firstSample.BatteryPercent;
            StartRemainingMWh = firstSample.RemainingCapacityMWh;
            StartedAtFullCharge = startedAtFullCharge;
            StartedAtSessionBoundary = startedAtSessionBoundary;
            LowConfidence = lowConfidence;
            AddOfflineSample(firstSample);
        }

        private DateTime StartUtc { get; }

        private double? StartBatteryPercent { get; }

        private double? StartRemainingMWh { get; }

        private bool StartedAtFullCharge { get; }

        private bool StartedAtSessionBoundary { get; }

        private bool LowConfidence { get; set; }

        private DateTime LastSampleUtc { get; set; }

        public DateTime LastObservedUtc => LastSampleUtc;

        private double? EndBatteryPercent { get; set; }

        private double? EndRemainingMWh { get; set; }

        private int SampleCount { get; set; }

        public static CycleDraft Start(
            SystemPowerSample firstSample,
            bool startedAtFullCharge,
            bool lowConfidence,
            bool startedAtSessionBoundary)
            => new(firstSample, startedAtFullCharge, lowConfidence, startedAtSessionBoundary);

        public void AddOfflineSample(SystemPowerSample sample)
        {
            LastSampleUtc = sample.TimestampUtc;
            EndBatteryPercent = sample.BatteryPercent;
            EndRemainingMWh = sample.RemainingCapacityMWh;
            SampleCount++;
        }

        public void MarkLowConfidence()
            => LowConfidence = true;

        public BatteryCycle Finish(DateTime? endUtc, bool isOpen)
        {
            return new BatteryCycle
            {
                StartUtc = StartUtc,
                EndUtc = endUtc,
                LastSampleUtc = LastSampleUtc,
                StartBatteryPercent = StartBatteryPercent,
                EndBatteryPercent = EndBatteryPercent,
                DischargePercent = PositiveDelta(StartBatteryPercent, EndBatteryPercent),
                StartRemainingMWh = StartRemainingMWh,
                EndRemainingMWh = EndRemainingMWh,
                DischargeWh = PositiveDelta(StartRemainingMWh, EndRemainingMWh) is { } mWh
                    ? Math.Round(mWh / 1000.0, 3)
                    : null,
                SampleCount = SampleCount,
                StartedAtFullCharge = StartedAtFullCharge,
                StartedAtSessionBoundary = StartedAtSessionBoundary,
                IsOpen = isOpen,
                Confidence = LowConfidence
                    ? BatteryCycleConfidence.Low
                    : BatteryCycleConfidence.High
            };
        }

        private static double? PositiveDelta(double? start, double? end)
        {
            if (!start.HasValue || !end.HasValue)
                return null;

            return Math.Round(Math.Max(0.0, start.Value - end.Value), 3);
        }
    }
}
