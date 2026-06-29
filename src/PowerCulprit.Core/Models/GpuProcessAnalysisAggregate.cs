namespace PowerCulprit.Core.Models;

/// <summary>
/// SQL-side aggregate of GPU Engine activity over an analysis window.
/// </summary>
public sealed record GpuProcessAnalysisAggregate
{
    public string ProcessName { get; init; } = string.Empty;
    public double AvgUtilizationPercent { get; init; }
    public double MaxUtilizationPercent { get; init; }
    public double VideoActivityPercent { get; init; }
}
