namespace PowerCulprit.Core.Models;

/// <summary>
/// A single sample of per-process GPU engine utilization.
/// </summary>
public record GpuProcessSample
{
    /// <summary>UTC timestamp of the sample.</summary>
    public DateTime TimestampUtc { get; init; }

    /// <summary>Process identifier, or null if PID resolution failed.</summary>
    public int? Pid { get; init; }

    /// <summary>Process image name, or null if unavailable.</summary>
    public string? ProcessName { get; init; }

    /// <summary>GPU engine instance name as reported by PDH.</summary>
    public string EngineName { get; init; } = string.Empty;

    /// <summary>Type of GPU activity.</summary>
    public GpuEngineType EngineType { get; init; }

    /// <summary>Utilization percentage of this engine (0–100).</summary>
    public double UtilizationPercent { get; init; }
}
