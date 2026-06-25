namespace PowerCulprit.Desktop.ViewModels;

public sealed class HistoricalProcessRow
{
    public int Rank { get; init; }
    public string ProcessName { get; init; } = "";
    public string ScoreText { get; init; } = "--";
    public string AvgCpuText { get; init; } = "--";
    public string MaxCpuText { get; init; } = "--";
    public string AvgGpuText { get; init; } = "--";
    public string MaxGpuText { get; init; } = "--";
    public string DiskMbText { get; init; } = "--";
    public string ForegroundMinutesText { get; init; } = "--";
    public string BackgroundMinutesText { get; init; } = "--";
    public string ReasonText { get; init; } = "";
}
