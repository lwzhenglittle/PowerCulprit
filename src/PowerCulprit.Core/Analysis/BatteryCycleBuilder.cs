using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

public static class BatteryCycleBuilder
{
    public const double FullChargePercent = 99.5;
    public const double FullChargeCapacityRatio = 0.995;
    public const double SmallCyclePercent = 20.0;
    public const double SmallCycleWh = 20.0;
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(10);

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

        var rawCycles = BuildRawCycles(ordered, orderedSessionStarts, sleepIntervals);
        var (assignedRawCycles, displayCycles) = BuildDisplayCycles(rawCycles);
        return new BatteryCycleBuildResult(assignedRawCycles, displayCycles);
    }

    private static List<BatteryCycle> BuildRawCycles(
        IReadOnlyList<SystemPowerSample> samples,
        IReadOnlyList<DateTime> sessionStarts,
        IReadOnlyList<PowerStateInterval> sleepIntervals)
    {
        var cycles = new List<BatteryCycle>();
        CycleDraft? current = null;
        SystemPowerSample? previous = null;
        var acSessionReachedFull = false;
        var nextSessionStartIndex = 0;

        // Pre-sort sleep intervals for fast gap-coverage lookup.
        var sortedSleep = sleepIntervals
            .Where(i => i.Kind == GapKind.ConfirmedSleep)
            .OrderBy(i => i.StartUtc)
            .ToList();

        foreach (var sample in samples)
        {
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

            if (sessionBoundary && current is not null)
            {
                cycles.Add(current.Finish(previous?.TimestampUtc ?? sample.TimestampUtc, isOpen: false));
                current = null;
            }

            // null AC == "unknown": treat the same as on-battery (false) so a
            // desktop with no battery / unknown AC does not get skipped out of
            // the discharge timeline. Only an explicit true pauses the cycle.
            if (sample.IsAcOnline == true)
            {
                if (current is not null)
                {
                    if (largeGap && !knownGap)
                        current.MarkLowConfidence();

                    cycles.Add(current.Finish(sample.TimestampUtc, isOpen: false));
                    current = null;
                }

                acSessionReachedFull = acSessionReachedFull || IsFullCharge(sample);
                previous = sample;
                continue;
            }

            if (current is null)
            {
                var partialStart = !sessionBoundary && (previous is null || previous.IsAcOnline != true);
                var lowConfidence = !sessionBoundary && (partialStart || (largeGap && !knownGap));
                var startedAtFullCharge = IsFullCharge(sample) ||
                    (previous?.IsAcOnline == true && IsFullCharge(previous)) ||
                    acSessionReachedFull;

                current = CycleDraft.Start(sample, startedAtFullCharge, lowConfidence, sessionBoundary);
                acSessionReachedFull = false;
            }
            else
            {
                current.AddOfflineSample(sample, largeGap && !knownGap);
            }

            previous = sample;
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
        var group = new List<BatteryCycle>();

        foreach (var cycle in rawCycles)
        {
            if (ShouldStartNewDisplayCycle(group, cycle))
                FlushGroup(group, assignedRawCycles, displayCycles);

            group.Add(cycle);

            if (cycle.Confidence == BatteryCycleConfidence.Low ||
                cycle.StartedAtSessionBoundary ||
                MeetsDisplayThreshold(group))
            {
                FlushGroup(group, assignedRawCycles, displayCycles);
            }
        }

        FlushGroup(group, assignedRawCycles, displayCycles);
        return (assignedRawCycles, displayCycles);
    }

    private static bool ShouldStartNewDisplayCycle(
        IReadOnlyList<BatteryCycle> currentGroup,
        BatteryCycle nextCycle)
    {
        if (currentGroup.Count == 0)
            return false;

        if (nextCycle.StartedAtSessionBoundary)
            return true;

        if (nextCycle.StartedAtFullCharge)
            return true;

        if (nextCycle.Confidence == BatteryCycleConfidence.Low ||
            currentGroup.Any(c => c.Confidence == BatteryCycleConfidence.Low))
            return true;

        return MeetsDisplayThreshold(currentGroup);
    }

    private static void FlushGroup(
        List<BatteryCycle> group,
        List<BatteryCycle> assignedRawCycles,
        List<BatteryDisplayCycle> displayCycles)
    {
        if (group.Count == 0)
            return;

        var displayId = displayCycles.Count + 1L;
        displayCycles.Add(CreateDisplayCycle(displayId, group));

        foreach (var cycle in group)
            assignedRawCycles.Add(cycle with { DisplayCycleId = displayId });

        group.Clear();
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

    private static bool MeetsDisplayThreshold(IReadOnlyList<BatteryCycle> group)
    {
        var percentValues = group
            .Where(c => c.DischargePercent.HasValue)
            .Select(c => c.DischargePercent!.Value)
            .ToList();
        var whValues = group
            .Where(c => c.DischargeWh.HasValue)
            .Select(c => c.DischargeWh!.Value)
            .ToList();

        if (percentValues.Count == 0 && whValues.Count == 0)
            return false;

        var percentMet = percentValues.Count == 0 || percentValues.Sum() >= SmallCyclePercent;
        var whMet = whValues.Count == 0 || whValues.Sum() >= SmallCycleWh;
        return percentMet && whMet;
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
            AddOfflineSample(firstSample, largeGap: false);
        }

        private DateTime StartUtc { get; }

        private double? StartBatteryPercent { get; }

        private double? StartRemainingMWh { get; }

        private bool StartedAtFullCharge { get; }

        private bool StartedAtSessionBoundary { get; }

        private bool LowConfidence { get; set; }

        private DateTime LastSampleUtc { get; set; }

        private double? EndBatteryPercent { get; set; }

        private double? EndRemainingMWh { get; set; }

        private int SampleCount { get; set; }

        public static CycleDraft Start(
            SystemPowerSample firstSample,
            bool startedAtFullCharge,
            bool lowConfidence,
            bool startedAtSessionBoundary)
            => new(firstSample, startedAtFullCharge, lowConfidence, startedAtSessionBoundary);

        public void AddOfflineSample(SystemPowerSample sample, bool largeGap)
        {
            if (largeGap)
                LowConfidence = true;

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
