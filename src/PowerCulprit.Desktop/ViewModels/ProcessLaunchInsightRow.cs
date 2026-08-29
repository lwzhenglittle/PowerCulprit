namespace PowerCulprit.Desktop.ViewModels;

public sealed class ProcessLaunchInsightRow
{
    public string LauncherProcessName { get; init; } = "";
    public string LaunchedProcessName { get; init; } = "";
    public string LaunchCountText { get; init; } = "--";
    public string ShortLivedCountText { get; init; } = "--";
    public string DescendantCountText { get; init; } = "--";
    public string MedianLifetimeText { get; init; } = "--";
    public string PowerDeltaText { get; init; } = "--";
    public string ConfidenceText { get; init; } = "--";
    public string EvidenceText { get; init; } = "";
    public bool IsRestartStorm { get; init; }
}
