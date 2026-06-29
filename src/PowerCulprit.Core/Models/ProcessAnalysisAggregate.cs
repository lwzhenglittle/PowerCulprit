namespace PowerCulprit.Core.Models;

/// <summary>
/// SQL-side aggregate of process activity over an analysis window.
/// </summary>
public sealed record ProcessAnalysisAggregate
{
    public string ProcessName { get; init; } = string.Empty;
    public string? ServiceName { get; init; }
    public double? AvgCpuPercent { get; init; }
    public double? MaxCpuPercent { get; init; }
    public double? AvgWorkingSetMb { get; init; }
    public double TotalDiskBytesPerSecond { get; init; }
    public double TotalNetworkBytesPerSecond { get; init; }
    public int? ProcessStartCount { get; init; }
    public int? ProcessStopCount { get; init; }
    public int? ShortLivedProcessCount { get; init; }
    public int ForegroundSampleCount { get; init; }
    public int BackgroundSampleCount { get; init; }
    public int SampleCount { get; init; }
}
