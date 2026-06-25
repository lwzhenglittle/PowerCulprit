namespace PowerCulprit.Core.Models;

/// <summary>
/// A single sample of per-process resource usage.
/// </summary>
public record ProcessSample
{
    /// <summary>UTC timestamp of the sample.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Process identifier.</summary>
    public int Pid { get; init; }

    /// <summary>Process image name (e.g., "chrome.exe").</summary>
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Full path to the executable, or null if access denied.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Command line used to launch the process, or null if unavailable.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Parent process identifier, or null if unavailable.</summary>
    public int? ParentPid { get; init; }

    /// <summary>CPU usage percentage (0–100 * cores).</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Working set size in megabytes.</summary>
    public double? WorkingSetMb { get; init; }

    /// <summary>Private memory in megabytes.</summary>
    public double? PrivateMemoryMb { get; init; }

    /// <summary>Number of threads.</summary>
    public int? ThreadCount { get; init; }

    /// <summary>Number of open handles.</summary>
    public int? HandleCount { get; init; }

    /// <summary>Disk read rate in bytes per second (delta-computed).</summary>
    public double? DiskReadBytesPerSecond { get; init; }

    /// <summary>Disk write rate in bytes per second (delta-computed).</summary>
    public double? DiskWriteBytesPerSecond { get; init; }

    /// <summary>Network receive rate in bytes per second (may be null).</summary>
    public double? NetworkReceiveBytesPerSecond { get; init; }

    /// <summary>Network send rate in bytes per second (may be null).</summary>
    public double? NetworkSendBytesPerSecond { get; init; }

    /// <summary>Whether this process owns the foreground window.</summary>
    public bool IsForegroundProcess { get; init; }

    /// <summary>
    /// For svchost.exe and other service-host processes, the comma-joined list of
    /// Win32 services hosted by this PID (sorted, e.g. "Dnscache, NlaSvc"). Null
    /// for non-host processes or when the SCM lookup failed.
    /// </summary>
    public string? ServiceName { get; init; }
}
