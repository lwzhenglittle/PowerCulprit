using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Analysis;

/// <summary>
/// Builds conservative launch-chain evidence from persisted process instances.
/// This is deliberately separate from the heuristic culprit score: a launch
/// relationship is causal evidence, while the power delta remains an estimate.
/// </summary>
public static class ProcessLaunchAnalyzer
{
    private static readonly TimeSpan ShortLivedThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PowerWindowInnerOffset = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PowerWindowOuterOffset = TimeSpan.FromSeconds(10);

    public static IReadOnlyList<ProcessLaunchInsight> Analyze(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<ProcessInstance> instances,
        IReadOnlyList<SystemPowerSample> powerSamples,
        int limit = 20)
    {
        if (toUtc <= fromUtc || instances.Count == 0 || limit <= 0)
            return Array.Empty<ProcessLaunchInsight>();

        var starts = instances
            .Where(instance => instance.StartObserved)
            .Where(instance => instance.StartTimeUtc >= fromUtc && instance.StartTimeUtc <= toUtc)
            .Where(instance => instance.Pid is not 0 and not 4)
            .ToList();
        if (starts.Count == 0)
            return Array.Empty<ProcessLaunchInsight>();

        var childrenByParent = starts
            .Where(instance => instance.ParentInstanceId.HasValue)
            .GroupBy(instance => instance.ParentInstanceId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var orderedPower = powerSamples.OrderBy(sample => sample.TimestampUtc).ToList();
        var durationHours = Math.Max(1.0 / 60.0, (toUtc - fromUtc).TotalHours);

        var insights = starts
            .GroupBy(instance => new LaunchKey(
                ResolveLauncherName(instance),
                NormalizeProcessName(instance.ProcessName)),
                LaunchKey.Comparer)
            .Select(group => BuildInsight(group.Key, group.ToList(), childrenByParent, orderedPower, durationHours))
            .OrderByDescending(insight => insight.IsRestartStorm)
            .ThenByDescending(insight => insight.LaunchCount)
            .ThenByDescending(insight => insight.DescendantLaunchCount)
            .ThenByDescending(insight => insight.EstimatedPowerDeltaWatts ?? double.NegativeInfinity)
            .ThenBy(insight => insight.LaunchedProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        return insights;
    }

    private static ProcessLaunchInsight BuildInsight(
        LaunchKey key,
        IReadOnlyList<ProcessInstance> launches,
        IReadOnlyDictionary<long, List<ProcessInstance>> childrenByParent,
        IReadOnlyList<SystemPowerSample> powerSamples,
        double durationHours)
    {
        var lifetimes = launches
            .Select(instance => instance.Lifetime)
            .Where(lifetime => lifetime.HasValue)
            .Select(lifetime => lifetime!.Value.TotalSeconds)
            .OrderBy(seconds => seconds)
            .ToList();
        var shortLivedCount = lifetimes.Count(seconds => seconds <= ShortLivedThreshold.TotalSeconds);
        var medianLifetime = Median(lifetimes);
        var descendantCount = launches.Sum(instance => CountDescendants(instance.Id, childrenByParent, new HashSet<long>()));
        var deltas = launches
            .Select(instance => EstimatePowerDelta(instance.StartTimeUtc, powerSamples))
            .Where(delta => delta.HasValue)
            .Select(delta => delta!.Value)
            .ToList();
        double? estimatedDelta = deltas.Count == 0 ? null : Math.Round(deltas.Average(), 2);
        var launchesPerHour = launches.Count / durationHours;
        var isRestartStorm = launches.Count >= 3 && shortLivedCount >= 2;
        var resolvedParents = launches.Count(instance => instance.ParentInstanceId.HasValue);
        var reliable = launches.All(instance => instance.CaptureReliable);
        var confidence = reliable && resolvedParents == launches.Count && launches.Count >= 3 && deltas.Count >= 2
            ? AttributionConfidence.High
            : reliable && resolvedParents > 0
                ? AttributionConfidence.Medium
                : AttributionConfidence.Low;

        var parts = new List<string>
        {
            $"{key.Launcher} started {key.Child} {launches.Count} time{(launches.Count == 1 ? "" : "s")}"
        };
        if (shortLivedCount > 0)
            parts.Add($"{shortLivedCount} exited within {ShortLivedThreshold.TotalSeconds:F0}s");
        if (isRestartStorm)
            parts.Add($"restart storm ({launchesPerHour:F1} starts/hour)");
        if (descendantCount > 0)
            parts.Add($"{descendantCount} descendant launch{(descendantCount == 1 ? "" : "es")}");
        if (estimatedDelta.HasValue)
            parts.Add($"estimated post-launch power change {estimatedDelta.Value:+0.0;-0.0;0.0} W");
        if (!reliable)
            parts.Add("ETW reported lost events");
        if (resolvedParents < launches.Count)
            parts.Add($"parent instance resolved for {resolvedParents}/{launches.Count}");

        return new ProcessLaunchInsight
        {
            LauncherProcessName = key.Launcher,
            LaunchedProcessName = key.Child,
            LaunchCount = launches.Count,
            ShortLivedCount = shortLivedCount,
            DescendantLaunchCount = descendantCount,
            MedianLifetimeSeconds = medianLifetime.HasValue ? Math.Round(medianLifetime.Value, 1) : null,
            EstimatedPowerDeltaWatts = estimatedDelta,
            IsRestartStorm = isRestartStorm,
            Confidence = confidence,
            Evidence = string.Join("; ", parts)
        };
    }

    private static int CountDescendants(
        long instanceId,
        IReadOnlyDictionary<long, List<ProcessInstance>> childrenByParent,
        HashSet<long> visited)
    {
        if (!visited.Add(instanceId) || !childrenByParent.TryGetValue(instanceId, out var children))
            return 0;

        var count = 0;
        foreach (var child in children)
            count += 1 + CountDescendants(child.Id, childrenByParent, visited);
        return count;
    }

    private static double? EstimatePowerDelta(
        DateTime startUtc,
        IReadOnlyList<SystemPowerSample> powerSamples)
    {
        var beforeFrom = startUtc - PowerWindowOuterOffset;
        var beforeTo = startUtc - PowerWindowInnerOffset;
        var afterFrom = startUtc + PowerWindowInnerOffset;
        var afterTo = startUtc + PowerWindowOuterOffset;
        var before = new List<double>();
        var after = new List<double>();

        foreach (var sample in powerSamples)
        {
            if (sample.TimestampUtc < beforeFrom)
                continue;
            if (sample.TimestampUtc > afterTo)
                break;

            var watts = GetDischargeWatts(sample);
            if (!watts.HasValue)
                continue;
            if (sample.TimestampUtc <= beforeTo)
                before.Add(watts.Value);
            else if (sample.TimestampUtc >= afterFrom)
                after.Add(watts.Value);
        }

        return before.Count >= 2 && after.Count >= 2
            ? after.Average() - before.Average()
            : null;
    }

    private static double? GetDischargeWatts(SystemPowerSample sample)
    {
        if (sample.IsAcOnline == true)
            return null;
        if (sample.ChargeRateMilliwatts.HasValue)
        {
            var watts = sample.ChargeRateMilliwatts.Value / 1000.0;
            return watts < 0 && double.IsFinite(watts) ? Math.Abs(watts) : null;
        }

        return sample.EstimatedDischargeWatts is double estimated && estimated >= 0 && double.IsFinite(estimated)
            ? estimated
            : null;
    }

    private static double? Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
            return null;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    private static string ResolveLauncherName(ProcessInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.ParentProcessName))
            return NormalizeProcessName(instance.ParentProcessName);
        return instance.ParentPid.HasValue ? $"PID {instance.ParentPid.Value}" : "Unknown launcher";
    }

    private static string NormalizeProcessName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown process" : value.Trim();

    private sealed record LaunchKey(string Launcher, string Child)
    {
        public static IEqualityComparer<LaunchKey> Comparer { get; } = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<LaunchKey>
        {
            public bool Equals(LaunchKey? x, LaunchKey? y)
                => x is not null && y is not null &&
                   string.Equals(x.Launcher, y.Launcher, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Child, y.Child, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(LaunchKey obj)
                => HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Launcher),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Child));
        }
    }
}
