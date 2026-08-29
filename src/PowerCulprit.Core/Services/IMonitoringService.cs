using PowerCulprit.Core.Models;

namespace PowerCulprit.Core.Services;

/// <summary>
/// Result of a single monitoring cycle containing the latest snapshot.
/// </summary>
public class MonitoringSnapshot
{
    /// <summary>The system power sample collected this cycle.</summary>
    public SystemPowerSample? PowerSample { get; init; }

    /// <summary>All process samples collected this cycle.</summary>
    public IReadOnlyList<ProcessSample> ProcessSamples { get; init; } = Array.Empty<ProcessSample>();

    /// <summary>GPU process samples collected this cycle.</summary>
    public IReadOnlyList<GpuProcessSample> GpuSamples { get; init; } = Array.Empty<GpuProcessSample>();

    /// <summary>Whether <see cref="GpuSamples"/> came from this cycle or a cached recent snapshot.</summary>
    public bool GpuSamplesAreFresh { get; init; } = true;

    /// <summary>Whether Windows GPU Engine sampling was enabled for this snapshot.</summary>
    public bool GpuSamplingEnabled { get; init; }

    /// <summary>Hardware sensor samples from LHM.</summary>
    public IReadOnlyList<HardwareSensorSample> HardwareSamples { get; init; } = Array.Empty<HardwareSensorSample>();

    /// <summary>CPU package power in watts, if available.</summary>
    public double? CpuPackagePowerWatts { get; init; }

    /// <summary>RAPL platform-domain power in watts; overlaps other domains and is not additive.</summary>
    public double? CpuPlatformPowerWatts { get; init; }

    /// <summary>RAPL PP0/core-domain power in watts.</summary>
    public double? CpuCoresPowerWatts { get; init; }

    /// <summary>RAPL DRAM/memory-domain power in watts.</summary>
    public double? CpuMemoryPowerWatts { get; init; }

    /// <summary>Direct Intel GPU hardware power in watts; never a utilization proxy.</summary>
    public double? GpuPowerWatts { get; init; }

    /// <summary>Intel GPU activity percentage from GPU Engine, independent of hardware watts.</summary>
    public double? IgpuActivityPercent { get; init; }

    /// <summary>Current source statuses (refreshed periodically).</summary>
    public IReadOnlyList<SourceStatus> SourceStatuses { get; init; } = Array.Empty<SourceStatus>();

    /// <summary>The UTC time of this snapshot.</summary>
    public DateTime SnapshotUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Contract for the background monitoring service that orchestrates
/// collectors and stores samples.
/// </summary>
public interface IMonitoringService
{
    /// <summary>Start the monitoring loop.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop the monitoring loop gracefully.</summary>
    Task StopAsync();

    /// <summary>Get the latest snapshot produced by the monitoring loop.</summary>
    MonitoringSnapshot? GetLatestSnapshot();

    /// <summary>Change the sampling interval (1–5 seconds).</summary>
    void SetInterval(int seconds);

    /// <summary>Enable or disable Windows GPU Engine sampling.</summary>
    void SetGpuSamplingEnabled(bool enabled);

    /// <summary>Whether Windows GPU Engine sampling is enabled.</summary>
    bool IsGpuSamplingEnabled { get; }

    /// <summary>Whether the monitoring loop is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Raised whenever <see cref="IsRunning"/> transitions. Value is the new
    /// running state. Subscribers may be invoked on a worker thread; marshal
    /// to the UI thread yourself if needed.
    /// </summary>
    event Action<bool>? RunningChanged;

    /// <summary>
    /// Raised after a monitoring cycle has produced a new snapshot. Subscribers
    /// may be invoked on a worker thread and must marshal UI work themselves.
    /// </summary>
    event Action<MonitoringSnapshot>? SnapshotPublished;

    /// <summary>
    /// Record a system power state transition (suspend / resume / resume-automatic)
    /// received via WM_POWERBROADCAST.
    /// </summary>
    Task RecordPowerStateEventAsync(
        PowerStateEventKind kind,
        DateTime? timestampUtc = null,
        string? details = null);

    /// <summary>
    /// Return power state events in the given time range.
    /// </summary>
    Task<IReadOnlyList<PowerStateEvent>> GetPowerStateEventsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
}
