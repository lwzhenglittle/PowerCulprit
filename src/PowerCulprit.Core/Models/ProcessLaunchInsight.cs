namespace PowerCulprit.Core.Models;

public enum AttributionConfidence
{
    Low,
    Medium,
    High
}

/// <summary>Evidence that one process repeatedly launched another process tree.</summary>
public sealed record ProcessLaunchInsight
{
    public string LauncherProcessName { get; init; } = string.Empty;
    public string LaunchedProcessName { get; init; } = string.Empty;
    public int LaunchCount { get; init; }
    public int ShortLivedCount { get; init; }
    public int DescendantLaunchCount { get; init; }
    public double? MedianLifetimeSeconds { get; init; }
    public double? EstimatedPowerDeltaWatts { get; init; }
    public bool IsRestartStorm { get; init; }
    public AttributionConfidence Confidence { get; init; }
    public string Evidence { get; init; } = string.Empty;
}
