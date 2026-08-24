using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Analyzes collected samples over a time window, scores processes,
/// ranks them by estimated power impact, and generates human-readable reasons.
/// </summary>
public class PowerCulpritAnalyzer
{
    private static readonly HashSet<int> KernelPseudoPids = new() { 0, 4 };

    public IReadOnlyList<CulpritReportItem> AnalyzeAggregates(
        TimeSpan window,
        int topN,
        IReadOnlyList<SystemPowerSample> powerSamples,
        IReadOnlyList<ProcessAnalysisAggregate> processAggregates,
        IReadOnlyList<GpuProcessAnalysisAggregate> gpuAggregates)
    {
        if (processAggregates.Count == 0)
            return Array.Empty<CulpritReportItem>();

        var powerInWindow = powerSamples
            .OrderBy(s => s.TimestampUtc)
            .ToList();
        var dischargeTimeline = BuildDischargeTimeline(powerInWindow);
        var effectiveIntervalSeconds = ComputeMedianIntervalSeconds(powerInWindow);

        var gpuByName = gpuAggregates
            .Where(g => !string.IsNullOrWhiteSpace(g.ProcessName))
            .GroupBy(g => g.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new GpuAggregate(
                    g.Average(x => x.AvgUtilizationPercent),
                    g.Max(x => x.MaxUtilizationPercent),
                    // VideoActivityPercent arrives as a per-engine-row average from
                    // SQL (one row per process name), so Average — not Sum — keeps
                    // it a true percentage.
                    g.Average(x => x.VideoActivityPercent)),
                StringComparer.OrdinalIgnoreCase);

        var scored = new List<CulpritReportItem>();
        foreach (var aggregate in processAggregates)
        {
            var name = aggregate.ProcessName;
            var serviceName = NormalizeService(aggregate.ServiceName);
            var avgCpu = aggregate.AvgCpuPercent;
            var maxCpu = aggregate.MaxCpuPercent;
            var avgMem = aggregate.AvgWorkingSetMb ?? 0;
            var totalDiskMb = aggregate.TotalDiskBytesPerSecond > 0
                ? Math.Round(aggregate.TotalDiskBytesPerSecond / (1024.0 * 1024.0), 2)
                : (double?)null;
            var totalNetMb = aggregate.TotalNetworkBytesPerSecond > 0
                ? Math.Round(aggregate.TotalNetworkBytesPerSecond / (1024.0 * 1024.0), 2)
                : (double?)null;
            var fgSeconds = aggregate.ForegroundSampleCount * effectiveIntervalSeconds;
            var bgSeconds = aggregate.BackgroundSampleCount * effectiveIntervalSeconds;

            double? avgGpu = null;
            double? maxGpu = null;
            double videoDecodeActivity = 0;
            if (gpuByName.TryGetValue(name, out var gpuAgg))
            {
                avgGpu = gpuAgg.AvgUtil;
                maxGpu = gpuAgg.MaxUtil;
                videoDecodeActivity = gpuAgg.VideoDecodeUtil;
            }

            var score = 0.0;
            var reasons = new List<string>();

            if (avgCpu.HasValue)
            {
                var cpuScore = avgCpu.Value * 1.5 + (maxCpu ?? 0) * 0.3;
                score += cpuScore;
                if (avgCpu.Value >= 1.0)
                    reasons.Add($"avg CPU {avgCpu.Value:F1}%");
            }

            if (avgGpu.HasValue)
            {
                var gpuScore = avgGpu.Value * 2.0 + (maxGpu ?? 0) * 0.4;
                score += gpuScore;
                if (avgGpu.Value >= 0.5)
                    reasons.Add($"GPU {avgGpu.Value:F1}%");
            }

            if (videoDecodeActivity > 0)
            {
                score += videoDecodeActivity * 1.5;
                reasons.Add($"GPU Video {videoDecodeActivity:F1}% active");
            }

            if (bgSeconds > 60)
            {
                score += (bgSeconds / 3600.0) * 5.0;
                var bgMin = (int)(bgSeconds / 60);
                reasons.Add($"background {bgMin} min");
            }

            if (totalDiskMb.HasValue && totalDiskMb.Value > 0)
            {
                score += totalDiskMb.Value * 0.01;
                if (totalDiskMb.Value > 1.0)
                    reasons.Add($"disk {totalDiskMb.Value:F0} MB");
            }

            if (totalNetMb.HasValue && totalNetMb.Value > 0)
                score += totalNetMb.Value * 0.005;

            if (aggregate.ShortLivedProcessCount.HasValue && aggregate.ShortLivedProcessCount.Value > 0)
            {
                score += aggregate.ShortLivedProcessCount.Value * 2.0;
                reasons.Add($"{aggregate.ShortLivedProcessCount.Value} short-lived instance{(aggregate.ShortLivedProcessCount.Value == 1 ? "" : "s")}");
            }

            if (aggregate.ProcessStartCount.HasValue && aggregate.ProcessStartCount.Value > 10)
            {
                score += (aggregate.ProcessStartCount.Value - 10) * 0.5;
                reasons.Add($"{aggregate.ProcessStartCount.Value} process starts");
            }

            // The aggregate path carries no per-timestamp CPU series, so a real
            // Pearson correlation against the discharge timeline cannot be
            // computed (unlike the Analyze path). Say so honestly instead of
            // claiming the discharge data itself is missing.
            double? powerCorr = null;
            var dischargeState = dischargeTimeline.Count > 0
                ? DischargeCorrelationState.AggregatedCannotCompute
                : DischargeCorrelationState.NoData;
            var reason = BuildReason(name, reasons, avgCpu, avgGpu, bgSeconds, powerCorr, avgMem, dischargeState);

            scored.Add(new CulpritReportItem
            {
                ProcessName = name,
                ServiceName = serviceName,
                Pid = null,
                Score = Math.Round(score, 2),
                Rank = 0,
                AvgCpuPercent = avgCpu.HasValue ? Math.Round(avgCpu.Value, 2) : null,
                MaxCpuPercent = maxCpu.HasValue ? Math.Round(maxCpu.Value, 2) : null,
                AvgGpuPercent = avgGpu.HasValue ? Math.Round(avgGpu.Value, 2) : null,
                MaxGpuPercent = maxGpu.HasValue ? Math.Round(maxGpu.Value, 2) : null,
                DiskMb = totalDiskMb,
                NetworkMb = totalNetMb,
                ProcessStartCount = aggregate.ProcessStartCount,
                ProcessStopCount = aggregate.ProcessStopCount,
                ShortLivedProcessCount = aggregate.ShortLivedProcessCount,
                BackgroundActiveSeconds = bgSeconds > 0 ? Math.Round(bgSeconds, 1) : null,
                ForegroundActiveSeconds = fgSeconds > 0 ? Math.Round(fgSeconds, 1) : null,
                PowerCorrelation = null,
                CpuPowerCorrelation = null,
                GpuActivityCorrelation = avgGpu.HasValue ? avgGpu.Value / 100.0 : null,
                Reason = reason
            });
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.AvgCpuPercent ?? 0.0)
            .ThenByDescending(s => s.MaxGpuPercent ?? 0.0)
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, topN))
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { Rank = i + 1 };

        return ranked;
    }

    public IReadOnlyList<CulpritReportItem> Analyze(
        TimeSpan window,
        int topN,
        IReadOnlyList<SystemPowerSample> powerSamples,
        IReadOnlyList<ProcessSample> processSamples,
        IReadOnlyList<GpuProcessSample> gpuSamples,
        ProcessGroupingMode groupingMode = ProcessGroupingMode.ProcessInstance)
    {
        var now = processSamples.Any() ? processSamples.Max(s => s.TimestampUtc) : DateTime.UtcNow;
        if (powerSamples.Any()) now = MaxT(now, powerSamples.Max(s => s.TimestampUtc));
        if (gpuSamples.Any()) now = MaxT(now, gpuSamples.Max(s => s.TimestampUtc));

        var windowStart = now - window;

        var powerInWindow = powerSamples
            .Where(s => s.TimestampUtc >= windowStart)
            .OrderBy(s => s.TimestampUtc)
            .ToList();

        var processInWindow = processSamples
            .Where(s => s.TimestampUtc >= windowStart)
            .Where(s => !KernelPseudoPids.Contains(s.Pid))
            .ToList();

        var gpuInWindow = gpuSamples
            .Where(s => s.TimestampUtc >= windowStart)
            .ToList();

        if (processInWindow.Count == 0)
            return Array.Empty<CulpritReportItem>();

        var dischargeTimeline = BuildDischargeTimeline(powerInWindow);
        var processGroups = BuildProcessGroups(processInWindow, groupingMode);
        var gpuByPid = BuildGpuByPid(gpuInWindow);
        var gpuByName = groupingMode == ProcessGroupingMode.ProcessName
            ? BuildGpuByName(gpuInWindow, processInWindow)
            : new Dictionary<GroupKey, GpuAggregate>(GroupKey.Comparer);

        // Used to multiply sample counts (FG/BG) into seconds. Reads the typical gap
        // between observed timestamps in the window so the result stays correct when
        // the user runs with --interval 1, or when the interval changed mid-window.
        var effectiveIntervalSeconds = ComputeMedianIntervalSeconds(powerInWindow, processInWindow);

        var scored = new List<CulpritReportItem>();

        foreach (var group in processGroups)
        {
            var pid = group.Pid;
            var name = group.ProcessName;
            var serviceName = group.ServiceName;
            var samples = group.Samples.OrderBy(s => s.TimestampUtc).ToList();

            var cpuValues = samples
                .Where(s => s.CpuPercent.HasValue)
                .Select(s => s.CpuPercent!.Value)
                .ToList();

            var avgCpu = cpuValues.Count > 0 ? cpuValues.Average() : (double?)null;
            var maxCpu = cpuValues.Count > 0 ? cpuValues.Max() : (double?)null;

            var avgMem = samples
                .Where(s => s.WorkingSetMb.HasValue)
                .Select(s => s.WorkingSetMb!.Value)
                .DefaultIfEmpty()
                .Average();

            var totalDiskBytes = samples
                .Where(s => s.DiskReadBytesPerSecond.HasValue || s.DiskWriteBytesPerSecond.HasValue)
                .Sum(s => (s.DiskReadBytesPerSecond ?? 0) + (s.DiskWriteBytesPerSecond ?? 0));
            var totalDiskMb = totalDiskBytes > 0
                ? Math.Round(totalDiskBytes / (1024.0 * 1024.0), 2)
                : (double?)null;

            var totalNetBytes = samples
                .Where(s => s.NetworkReceiveBytesPerSecond.HasValue || s.NetworkSendBytesPerSecond.HasValue)
                .Sum(s => (s.NetworkReceiveBytesPerSecond ?? 0) + (s.NetworkSendBytesPerSecond ?? 0));
            var totalNetMb = totalNetBytes > 0
                ? Math.Round(totalNetBytes / (1024.0 * 1024.0), 2)
                : (double?)null;

            var totalProcessStarts = SumNullableInt(samples.Select(s => s.ProcessStartCount));
            var totalProcessStops = SumNullableInt(samples.Select(s => s.ProcessStopCount));
            var totalShortLived = SumNullableInt(samples.Select(s => s.ShortLivedProcessCount));

            var bgSamples = samples.Count(s => !s.IsForegroundProcess);
            var fgSamples = samples.Count - bgSamples;
            var bgSeconds = bgSamples * effectiveIntervalSeconds;
            var fgSeconds = fgSamples * effectiveIntervalSeconds;

            double? avgGpu = null;
            double? maxGpu = null;
            double videoDecodeActivity = 0;

            GpuAggregate? gpuAgg = null;
            if (groupingMode == ProcessGroupingMode.ProcessName)
                gpuByName.TryGetValue(new GroupKey(name, serviceName), out gpuAgg);
            else if (pid.HasValue)
                gpuByPid.TryGetValue(pid.Value, out gpuAgg);

            if (gpuAgg is not null)
            {
                avgGpu = gpuAgg.AvgUtil;
                maxGpu = gpuAgg.MaxUtil;
                videoDecodeActivity = gpuAgg.VideoDecodeUtil;
            }

            var score = 0.0;
            var reasons = new List<string>();

            if (avgCpu.HasValue)
            {
                var cpuScore = avgCpu.Value * 1.5 + (maxCpu ?? 0) * 0.3;
                score += cpuScore;
                if (avgCpu.Value >= 1.0)
                    reasons.Add($"avg CPU {avgCpu.Value:F1}%");
            }

            if (avgGpu.HasValue)
            {
                var gpuScore = avgGpu.Value * 2.0 + (maxGpu ?? 0) * 0.4;
                score += gpuScore;
                if (avgGpu.Value >= 0.5)
                    reasons.Add($"GPU {avgGpu.Value:F1}%");
            }

            if (videoDecodeActivity > 0)
            {
                score += videoDecodeActivity * 1.5;
                reasons.Add($"GPU Video {videoDecodeActivity:F1}% active");
            }

            if (bgSeconds > 60)
            {
                score += (bgSeconds / 3600.0) * 5.0;
                var bgMin = (int)(bgSeconds / 60);
                reasons.Add($"background {bgMin} min");
            }

            if (totalDiskMb.HasValue && totalDiskMb.Value > 0)
            {
                score += totalDiskMb.Value * 0.01;
                if (totalDiskMb.Value > 1.0)
                    reasons.Add($"disk {totalDiskMb.Value:F0} MB");
            }

            if (totalNetMb.HasValue && totalNetMb.Value > 0)
                score += totalNetMb.Value * 0.005;

            if (totalShortLived.HasValue && totalShortLived.Value > 0)
            {
                score += totalShortLived.Value * 2.0;
                reasons.Add($"{totalShortLived.Value} short-lived instance{(totalShortLived.Value == 1 ? "" : "s")}");
            }

            if (totalProcessStarts.HasValue && totalProcessStarts.Value > 10)
            {
                score += (totalProcessStarts.Value - 10) * 0.5;
                reasons.Add($"{totalProcessStarts.Value} process starts");
            }

            double? powerCorr = null;
            double? cpuPowerCorr = null;

            if (dischargeTimeline.Count >= 3)
            {
                powerCorr = ComputeProcessPowerCorrelation(samples, dischargeTimeline);
                if (powerCorr.HasValue && powerCorr.Value > 0.3)
                    score += powerCorr.Value * 10.0;
            }

            var dischargeState = powerCorr.HasValue
                ? DischargeCorrelationState.HasValue
                : dischargeTimeline.Count > 0
                    ? DischargeCorrelationState.InsufficientData
                    : DischargeCorrelationState.NoData;

            var reason = BuildReason(name, reasons, avgCpu, avgGpu, bgSeconds, powerCorr, avgMem, dischargeState);

            scored.Add(new CulpritReportItem
            {
                ProcessName = name,
                ServiceName = serviceName,
                Pid = pid,
                Score = Math.Round(score, 2),
                Rank = 0,
                AvgCpuPercent = avgCpu.HasValue ? Math.Round(avgCpu.Value, 2) : null,
                MaxCpuPercent = maxCpu.HasValue ? Math.Round(maxCpu.Value, 2) : null,
                AvgGpuPercent = avgGpu.HasValue ? Math.Round(avgGpu.Value, 2) : null,
                MaxGpuPercent = maxGpu.HasValue ? Math.Round(maxGpu.Value, 2) : null,
                DiskMb = totalDiskMb,
                NetworkMb = totalNetMb,
                ProcessStartCount = totalProcessStarts,
                ProcessStopCount = totalProcessStops,
                ShortLivedProcessCount = totalShortLived,
                BackgroundActiveSeconds = bgSeconds > 0 ? Math.Round(bgSeconds, 1) : null,
                ForegroundActiveSeconds = fgSeconds > 0 ? Math.Round(fgSeconds, 1) : null,
                PowerCorrelation = powerCorr,
                CpuPowerCorrelation = cpuPowerCorr,
                GpuActivityCorrelation = avgGpu.HasValue ? avgGpu.Value / 100.0 : null,
                Reason = reason
            });
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.AvgCpuPercent ?? 0.0)
            .ThenByDescending(s => s.MaxGpuPercent ?? 0.0)
            .ThenBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Pid ?? int.MaxValue)
            .Take(Math.Max(1, topN))
            .ToList();

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { Rank = i + 1 };

        return ranked;
    }

    private static List<ProcessAnalysisGroup> BuildProcessGroups(
        List<ProcessSample> processSamples,
        ProcessGroupingMode groupingMode)
    {
        if (groupingMode == ProcessGroupingMode.ProcessName)
        {
            // svchost.exe sits behind a service-host frontend, so when we group by
            // process name we further partition by service name. This is what makes
            // "svchost (Dnscache)" rank separately from "svchost (wuauserv)" — without
            // it the entire svchost.exe family collapses into one opaque row.
            //
            // ServiceName is normalised to null/empty for the bucket key so non-host
            // processes (chrome, code, etc.) still produce a single row per name.
            return processSamples
                .GroupBy(p => new GroupKey(p.ProcessName, p.ServiceName), GroupKey.Comparer)
                .Select(g =>
                {
                    var first = g.First();
                    return new ProcessAnalysisGroup(
                        null,
                        first.ProcessName,
                        NormalizeService(first.ServiceName),
                        AggregateByTimestamp(first.ProcessName, NormalizeService(first.ServiceName), g));
                })
                .ToList();
        }

        return processSamples
            .GroupBy(p => (p.Pid, p.ProcessName, Svc: NormalizeService(p.ServiceName)))
            .Select(g => new ProcessAnalysisGroup(
                g.Key.Pid, g.Key.ProcessName, g.Key.Svc, g.ToList()))
            .ToList();
    }

    private static string? NormalizeService(string? serviceName)
        => string.IsNullOrWhiteSpace(serviceName) ? null : serviceName;

    private static List<ProcessSample> AggregateByTimestamp(
        string processName,
        string? serviceName,
        IEnumerable<ProcessSample> samples)
    {
        return samples
            .GroupBy(s => s.TimestampUtc)
            .Select(g => new ProcessSample
            {
                TimestampUtc = g.Key,
                Pid = 0,
                ProcessName = processName,
                ServiceName = serviceName,
                CpuPercent = SumNullable(g.Select(s => s.CpuPercent)),
                WorkingSetMb = SumNullable(g.Select(s => s.WorkingSetMb)),
                DiskReadBytesPerSecond = SumNullable(g.Select(s => s.DiskReadBytesPerSecond)),
                DiskWriteBytesPerSecond = SumNullable(g.Select(s => s.DiskWriteBytesPerSecond)),
                NetworkReceiveBytesPerSecond = SumNullable(g.Select(s => s.NetworkReceiveBytesPerSecond)),
                NetworkSendBytesPerSecond = SumNullable(g.Select(s => s.NetworkSendBytesPerSecond)),
                ProcessStartCount = SumNullableInt(g.Select(s => s.ProcessStartCount)),
                ProcessStopCount = SumNullableInt(g.Select(s => s.ProcessStopCount)),
                ShortLivedProcessCount = SumNullableInt(g.Select(s => s.ShortLivedProcessCount)),
                IsForegroundProcess = g.Any(s => s.IsForegroundProcess)
            })
            .ToList();
    }

    private static double? SumNullable(IEnumerable<double?> values)
    {
        var hasValue = false;
        var sum = 0.0;
        foreach (var value in values)
        {
            if (!value.HasValue) continue;
            hasValue = true;
            sum += value.Value;
        }

        return hasValue ? sum : null;
    }

    private static Dictionary<int, GpuAggregate> BuildGpuByPid(List<GpuProcessSample> gpuSamples)
    {
        return gpuSamples
            .Where(g => g.Pid.HasValue)
            .GroupBy(g => g.Pid!.Value)
            .ToDictionary(g => g.Key, BuildGpuAggregate);
    }

    private static Dictionary<GroupKey, GpuAggregate> BuildGpuByName(
        List<GpuProcessSample> gpuSamples,
        List<ProcessSample> processSamples)
    {
        // pidToKey: for each pid we pick the (processName, serviceName) that the most
        // samples agree on. GPU samples carry their own ProcessName but never a
        // ServiceName — we recover that via the pid → service mapping that the
        // ProcessResourceCollector stamped onto the process samples.
        var pidToKey = processSamples
            .GroupBy(p => p.Pid)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(p => new GroupKey(p.ProcessName, NormalizeService(p.ServiceName)),
                               GroupKey.Comparer)
                      .OrderByDescending(kg => kg.Count())
                      .First().Key);

        return gpuSamples
            .Select(g =>
            {
                // Prefer the pid-keyed lookup whenever we can, because that's the
                // only way we know which service inside an svchost the GPU sample
                // belongs to. Fall back to the raw ProcessName for non-host gpu
                // samples (the common case).
                GroupKey? key = null;
                if (g.Pid.HasValue && pidToKey.TryGetValue(g.Pid.Value, out var fromPid))
                    key = fromPid;
                else if (!string.IsNullOrWhiteSpace(g.ProcessName))
                    key = new GroupKey(g.ProcessName, null);

                return (Key: key, Sample: g);
            })
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, GroupKey.Comparer)
            .ToDictionary(
                grp => grp.Key,
                grp => BuildGpuAggregate(grp.Select(x => x.Sample)),
                GroupKey.Comparer);
    }

    private static GpuAggregate BuildGpuAggregate(IEnumerable<GpuProcessSample> samples)
    {
        var list = samples.ToList();
        return new GpuAggregate(
            list.Average(x => x.UtilizationPercent),
            list.Max(x => x.UtilizationPercent),
            // Per-engine-row average video contribution — the same convention as
            // AvgUtil, so the value stays a true percentage instead of growing
            // with the number of samples in the window (list.Count >= 1 here
            // because the input comes out of a GroupBy).
            list.Where(x => x.EngineType is GpuEngineType.VideoDecode or GpuEngineType.VideoEncode)
                .Sum(x => x.UtilizationPercent) / list.Count);
    }

    private static DateTime MaxT(DateTime a, DateTime b) => a > b ? a : b;

    // Used as the multiplier for FG/BG sample counts. We don't want to hard-code 2.0 because
    //  (a) the user can pass --interval 1, and (b) the interval may have changed mid-window
    //  for historical data. We read the typical gap from the observed timestamps: power_samples
    //  is the preferred source (1 row per cycle in MonitoringService), with process_samples as
    //  a fallback in case battery collection was failing. Gaps > 30s are dropped so a system
    //  suspend doesn't push the median up.
    private const double DefaultIntervalSeconds = 2.0;
    private const double MaxValidGapSeconds = 30.0;

    private static double ComputeMedianIntervalSeconds(
        List<SystemPowerSample> powerSamples,
        List<ProcessSample> processSamples)
    {
        IEnumerable<DateTime> timestamps;
        if (powerSamples.Count >= 2)
        {
            timestamps = powerSamples.Select(s => s.TimestampUtc);
        }
        else
        {
            timestamps = processSamples.Select(s => s.TimestampUtc).Distinct();
        }

        var sorted = timestamps.OrderBy(t => t).ToList();
        if (sorted.Count < 2)
            return DefaultIntervalSeconds;

        var gaps = new List<double>(sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            var dt = (sorted[i] - sorted[i - 1]).TotalSeconds;
            if (dt > 0 && dt <= MaxValidGapSeconds)
                gaps.Add(dt);
        }

        if (gaps.Count == 0)
            return DefaultIntervalSeconds;

        gaps.Sort();
        // Lower median (gaps[count / 2]) — fine for our purposes; we don't need the
        // average-of-two-middles refinement that statisticians use for even counts.
        return gaps[gaps.Count / 2];
    }

    private static double ComputeMedianIntervalSeconds(List<SystemPowerSample> powerSamples)
    {
        if (powerSamples.Count < 2)
            return DefaultIntervalSeconds;

        var sorted = powerSamples
            .Select(s => s.TimestampUtc)
            .OrderBy(t => t)
            .ToList();
        var gaps = new List<double>(sorted.Count - 1);
        for (int i = 1; i < sorted.Count; i++)
        {
            var dt = (sorted[i] - sorted[i - 1]).TotalSeconds;
            if (dt > 0 && dt <= MaxValidGapSeconds)
                gaps.Add(dt);
        }

        if (gaps.Count == 0)
            return DefaultIntervalSeconds;

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    private static List<(DateTime Time, double DischargeWatts)> BuildDischargeTimeline(
        List<SystemPowerSample> powerSamples)
    {
        var timeline = new List<(DateTime Time, double DischargeWatts)>();

        foreach (var sample in powerSamples)
        {
            double? dischargeW = null;

            if (sample.ChargeRateMilliwatts.HasValue)
            {
                var chargeRateW = sample.ChargeRateMilliwatts.Value / 1000.0;
                dischargeW = chargeRateW < 0 ? Math.Abs(chargeRateW) : 0;
            }
            else if (sample.EstimatedDischargeWatts.HasValue)
            {
                dischargeW = sample.EstimatedDischargeWatts.Value;
            }

            if (dischargeW.HasValue)
                timeline.Add((sample.TimestampUtc, dischargeW.Value));
        }

        return timeline;
    }

    private static int? SumNullableInt(IEnumerable<int?> values)
    {
        var hasValue = false;
        var sum = 0;
        foreach (var value in values)
        {
            if (!value.HasValue) continue;
            hasValue = true;
            sum += value.Value;
        }
        return hasValue ? sum : null;
    }

    private static double? ComputeProcessPowerCorrelation(
        List<ProcessSample> processSamples,
        List<(DateTime Time, double DischargeWatts)> dischargeTimeline)
    {
        if (dischargeTimeline.Count == 0) return null;

        var pairedCpu = new List<double>();
        var pairedDischarge = new List<double>();

        var cursor = 0;
        var lastIndex = dischargeTimeline.Count - 1;

        foreach (var ps in processSamples)
        {
            if (!ps.CpuPercent.HasValue) continue;

            var target = ps.TimestampUtc;
            while (cursor < lastIndex)
            {
                var hereDelta = AbsTicks(target - dischargeTimeline[cursor].Time);
                var nextDelta = AbsTicks(target - dischargeTimeline[cursor + 1].Time);
                if (nextDelta <= hereDelta)
                    cursor++;
                else
                    break;
            }

            pairedCpu.Add(ps.CpuPercent.Value);
            pairedDischarge.Add(dischargeTimeline[cursor].DischargeWatts);
        }

        if (pairedCpu.Count < 3) return null;

        return CorrelationHelper.Pearson(pairedCpu, pairedDischarge);
    }

    private static long AbsTicks(TimeSpan span)
    {
        var t = span.Ticks;
        return t < 0 ? -t : t;
    }

    /// <summary>
    /// How much discharge-correlation information is available for a reason.
    /// Lets BuildReason distinguish "no discharge data at all" from "data is
    /// present but a per-process correlation could not be computed" so the
    /// reason text never falsely claims the discharge data itself is missing.
    /// </summary>
    private enum DischargeCorrelationState
    {
        /// <summary>A Pearson correlation was computed (Analyze path only).</summary>
        HasValue,
        /// <summary>Discharge data exists, but the aggregate path has no per-timestamp CPU series to correlate.</summary>
        AggregatedCannotCompute,
        /// <summary>Discharge data exists, but too few points / zero variance prevented a correlation.</summary>
        InsufficientData,
        /// <summary>No discharge data at all.</summary>
        NoData,
    }

    private static string BuildReason(
        string processName,
        List<string> reasons,
        double? avgCpu,
        double? avgGpu,
        double bgSeconds,
        double? powerCorr,
        double avgMem,
        DischargeCorrelationState dischargeState)
    {
        // Only short-circuit to "limited power impact" when there is genuinely
        // nothing contributing to the score — no CPU/GPU/video/background/disk
        // or lifecycle reasons. A process with low avg CPU but many short-lived
        // spawns or process starts still has reasons populated, so it must fall
        // through to the parts list rather than being dismissed as harmless.
        if (reasons.Count == 0)
        {
            return $"{processName}: low activity (avg CPU {avgCpu:F1}%, mem {avgMem:F0} MB) - limited power impact";
        }

        var parts = reasons;

        var hasDischarge = dischargeState switch
        {
            DischargeCorrelationState.HasValue => $"; discharge correlation {powerCorr!.Value:F2}",
            DischargeCorrelationState.AggregatedCannotCompute => "; discharge data present (per-process correlation needs raw samples, not aggregates)",
            DischargeCorrelationState.InsufficientData => "; discharge data present but correlation could not be computed",
            _ => "; discharge data unavailable",
        };

        return $"{processName}: {string.Join(", ", parts)}{hasDischarge}";
    }

    private sealed record ProcessAnalysisGroup(
        int? Pid,
        string ProcessName,
        string? ServiceName,
        List<ProcessSample> Samples);

    // Composite group key used both for grouping process samples and for indexing the
    // GPU-by-name dictionary, so svchost (Dnscache) and svchost (wuauserv) get two
    // separate buckets instead of fighting over a single "svchost" entry.
    private sealed record GroupKey(string ProcessName, string? ServiceName)
    {
        public static IEqualityComparer<GroupKey> Comparer { get; } = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<GroupKey>
        {
            public bool Equals(GroupKey? x, GroupKey? y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x is null || y is null) return false;
                return string.Equals(x.ProcessName, y.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.ServiceName ?? "", y.ServiceName ?? "", StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(GroupKey obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProcessName),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ServiceName ?? ""));
        }
    }

    private sealed record GpuAggregate(
        double AvgUtil,
        double MaxUtil,
        double VideoDecodeUtil);
}
