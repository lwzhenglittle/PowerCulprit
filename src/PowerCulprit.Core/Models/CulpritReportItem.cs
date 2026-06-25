namespace PowerCulprit.Core.Models;

/// <summary>
/// A single item in the power culprit ranking report.
/// </summary>
public record CulpritReportItem
{
    /// <summary>Process image name.</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// For svchost.exe (or other service-host) rows, the comma-joined service
    /// name(s) hosted by this group key (e.g. "Dnscache" or "Dnscache, NlaSvc").
    /// Null for non-host processes. The UI typically renders this as
    /// "<c>{ProcessName} ({ServiceName})</c>".
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>Process identifier, or null if the process has exited.</summary>
    public int? Pid { get; init; }

    /// <summary>Composite score (higher = more likely power culprit).</summary>
    public double Score { get; init; }

    /// <summary>Rank position (1 = highest score).</summary>
    public int Rank { get; init; }

    /// <summary>Average CPU percentage over the window.</summary>
    public double? AvgCpuPercent { get; init; }

    /// <summary>Maximum CPU percentage observed.</summary>
    public double? MaxCpuPercent { get; init; }

    /// <summary>Average GPU percentage over the window.</summary>
    public double? AvgGpuPercent { get; init; }

    /// <summary>Maximum GPU percentage observed.</summary>
    public double? MaxGpuPercent { get; init; }

    /// <summary>Total disk I/O in megabytes over the window.</summary>
    public double? DiskMb { get; init; }

    /// <summary>Total network I/O in megabytes over the window.</summary>
    public double? NetworkMb { get; init; }

    /// <summary>
    /// Seconds the process spent in foreground (owning the focused window) over the window.
    /// Counted as (number of samples where the process owned the foreground window at the
    /// sampling instant) × the effective sampling interval (median gap between samples in
    /// the window). Window flicker shorter than the sampling interval is not reflected here.
    /// </summary>
    public double? ForegroundActiveSeconds { get; init; }

    /// <summary>
    /// Seconds the process spent in background (non-foreground) state over the window.
    /// Counted as (number of samples where the process did NOT own the foreground window)
    /// × the effective sampling interval (median gap between samples in the window). Window
    /// flicker shorter than the sampling interval is not reflected here.
    /// </summary>
    public double? BackgroundActiveSeconds { get; init; }

    /// <summary>Correlation of this process's resource usage with total discharge rate.</summary>
    public double? PowerCorrelation { get; init; }

    /// <summary>Correlation of this process's CPU usage with CPU package power.</summary>
    public double? CpuPowerCorrelation { get; init; }

    /// <summary>Correlation of this process with GPU activity.</summary>
    public double? GpuActivityCorrelation { get; init; }

    /// <summary>
    /// Human-readable explanation for why this process was ranked as a power culprit.
    /// </summary>
    public string Reason { get; init; } = string.Empty;
}
