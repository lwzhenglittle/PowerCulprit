using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;
using PowerCulprit.Core.Services;
using PowerCulprit.Storage;
using System.Threading.Channels;

namespace PowerCulprit.Collectors;

/// <summary>
/// Background monitoring service that orchestrates all collectors,
/// writes samples to SQLite, and publishes snapshots to the UI layer.
/// </summary>
public class MonitoringService : IMonitoringService
{
    // Collectors
    private readonly BatteryPowerCollector _batteryCollector;
    private readonly ProcessResourceCollector _processCollector;
    private readonly WindowsGpuEngineCollector _gpuEngineCollector;
    private readonly LibreHardwareMonitorCollector _lhmCollector;
    private readonly IntelCpuPowerCollector _cpuPowerCollector;
    private readonly IntelGpuPowerCollector _gpuPowerCollector;
    private readonly DatabaseManager _databaseManager;
    private readonly ILogger<MonitoringService> _logger;

    private readonly object _lock = new();
    private Task? _loopTask;
    private Task? _writerTask;
    private Channel<MonitoringWriteBatch>? _writeQueue;
    private CancellationTokenSource? _cts;
    private int _intervalSeconds = 2;
    private bool _gpuSamplingEnabled;

    private MonitoringSnapshot? _latestSnapshot;
    private DateTime _lastSourceStatusRefresh = DateTime.MinValue;
    private DateTime _lastGpuEngineCollectUtc = DateTime.MinValue;
    private DateTime _lastCleanupUtc = DateTime.MinValue;
    private IReadOnlyList<GpuProcessSample> _lastGpuSamples = Array.Empty<GpuProcessSample>();
    private static readonly TimeSpan SourceStatusRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GpuEngineCollectInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);
    private const int RetentionDays = 7;

    public bool IsRunning { get; private set; }

    public bool IsGpuSamplingEnabled
    {
        get { lock (_lock) { return _gpuSamplingEnabled; } }
    }

    public event Action<bool>? RunningChanged;

    public MonitoringService(
        BatteryPowerCollector batteryCollector,
        ProcessResourceCollector processCollector,
        WindowsGpuEngineCollector gpuEngineCollector,
        LibreHardwareMonitorCollector lhmCollector,
        IntelCpuPowerCollector cpuPowerCollector,
        IntelGpuPowerCollector gpuPowerCollector,
        DatabaseManager databaseManager,
        ILogger<MonitoringService> logger)
    {
        _batteryCollector = batteryCollector;
        _processCollector = processCollector;
        _gpuEngineCollector = gpuEngineCollector;
        _lhmCollector = lhmCollector;
        _cpuPowerCollector = cpuPowerCollector;
        _gpuPowerCollector = gpuPowerCollector;
        _databaseManager = databaseManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (IsRunning)
            {
                _logger.LogWarning("MonitoringService is already running");
                return Task.CompletedTask;
            }

            IsRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _writeQueue = Channel.CreateUnbounded<MonitoringWriteBatch>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });
            _writerTask = Task.Run(RunWriteLoopAsync);
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        }

        _logger.LogInformation("MonitoringService started with interval {Interval}s", _intervalSeconds);
        RaiseRunningChanged(true);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        Task? loopTask;
        Task? writerTask;
        Channel<MonitoringWriteBatch>? writeQueue;
        lock (_lock)
        {
            if (!IsRunning) return;

            IsRunning = false;
            _cts?.Cancel();
            loopTask = _loopTask;
            writerTask = _writerTask;
            writeQueue = _writeQueue;
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) { /* Expected */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for monitoring loop to stop");
            }
        }

        writeQueue?.Writer.TryComplete();
        if (writerTask is not null)
        {
            try
            {
                await writerTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for database writer to stop");
            }
        }

        lock (_lock)
        {
            _loopTask = null;
            _writerTask = null;
            _writeQueue = null;
            _cts?.Dispose();
            _cts = null;
        }

        _logger.LogInformation("MonitoringService stopped");
        RaiseRunningChanged(false);
    }

    private void RaiseRunningChanged(bool running)
    {
        // Capture handler outside the lock — subscribers must not block us.
        var handler = RunningChanged;
        if (handler is null) return;
        try { handler(running); }
        catch (Exception ex) { _logger.LogError(ex, "RunningChanged subscriber threw"); }
    }

    /// <inheritdoc/>
    public MonitoringSnapshot? GetLatestSnapshot()
    {
        lock (_lock) { return _latestSnapshot; }
    }

    /// <inheritdoc/>
    public void SetInterval(int seconds)
    {
        if (seconds < 1 || seconds > 5)
        {
            _logger.LogWarning("Invalid interval {Seconds}s; must be 1–5. Ignored.", seconds);
            return;
        }
        _intervalSeconds = seconds;
    }

    /// <inheritdoc/>
    public void SetGpuSamplingEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_gpuSamplingEnabled == enabled)
                return;

            _gpuSamplingEnabled = enabled;
            _lastSourceStatusRefresh = DateTime.MinValue;
            if (!enabled)
            {
                _lastGpuSamples = Array.Empty<GpuProcessSample>();
                _lastGpuEngineCollectUtc = DateTime.MinValue;
            }
        }

        _logger.LogInformation("Windows GPU Engine sampling {State}", enabled ? "enabled" : "disabled");
    }

    // ──────────────────────────────────────────────
    //  Main loop
    // ──────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await _databaseManager.InitializeAsync();
        await CleanupOldDataIfDueAsync(force: true);

        // One-time hardware init (must be done on startup)
        _lhmCollector.Initialize();

        while (!cancellationToken.IsCancellationRequested)
        {
            var cycleStart = DateTime.UtcNow;
            try
            {
                await CleanupOldDataIfDueAsync(force: false);
                await RunSingleCycleAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in monitoring cycle — continuing");
            }

            var elapsed = (DateTime.UtcNow - cycleStart).TotalMilliseconds;
            var delayMs = Math.Max(0, _intervalSeconds * 1000 - elapsed);
            try
            {
                await Task.Delay((int)delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Monitoring loop exited");
    }

    private async Task CleanupOldDataIfDueAsync(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastCleanupUtc < CleanupInterval)
            return;

        _lastCleanupUtc = now;
        try
        {
            var deleted = await _databaseManager.CleanupOldDataAsync(RetentionDays);
            if (deleted > 0)
                _logger.LogInformation("Cleaned up {DeletedRows} rows older than {RetentionDays} days", deleted, RetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical data cleanup failed");
        }
    }

    private async Task RunWriteLoopAsync()
    {
        var queue = _writeQueue;
        if (queue is null)
            return;

        await foreach (var batch in queue.Reader.ReadAllAsync())
        {
            try
            {
                await _databaseManager.InsertMonitoringCycleAsync(
                    batch.PowerSample,
                    batch.ProcessSamples,
                    batch.GpuSamples,
                    batch.HardwareSamples,
                    batch.SourceStatuses);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database write failed");
            }
        }
    }

    private async Task RunSingleCycleAsync()
    {
        var now = DateTime.UtcNow;
        SystemPowerSample? batterySample = null;
        IReadOnlyList<ProcessSample> processSamples = Array.Empty<ProcessSample>();
        IReadOnlyList<GpuProcessSample> gpuSamples = Array.Empty<GpuProcessSample>();
        IReadOnlyList<HardwareSensorSample> hwSamples = Array.Empty<HardwareSensorSample>();
        IReadOnlyList<SourceStatus> sourceStatuses = Array.Empty<SourceStatus>();
        var gpuSamplesAreFresh = false;
        var gpuSamplingEnabled = IsGpuSamplingEnabled;

        // ── Collect battery ────────────────────────
        try { batterySample = _batteryCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "BatteryPowerCollector threw"); }

        // ── Collect processes ─────────────────────
        try { processSamples = _processCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "ProcessResourceCollector threw"); }

        // ── Collect GPU Engine ────────────────────
        if (gpuSamplingEnabled && now - _lastGpuEngineCollectUtc >= GpuEngineCollectInterval)
        {
            try
            {
                gpuSamples = _gpuEngineCollector.Collect();
                _lastGpuSamples = gpuSamples;
                _lastGpuEngineCollectUtc = now;
                gpuSamplesAreFresh = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WindowsGpuEngineCollector threw");
                gpuSamples = _lastGpuSamples;
            }
        }
        else if (gpuSamplingEnabled)
        {
            gpuSamples = _lastGpuSamples;
        }
        else
        {
            gpuSamples = Array.Empty<GpuProcessSample>();
        }

        // ── Collect hardware sensors (LHM) ────────
        try { hwSamples = _lhmCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "LHMCollector threw"); }

        // ── Derive CPU / iGPU power ────────������──
        double? cpuPkgWatts = null;
        double? igpuValue = null;
        double? igpuActivityPct = null;
        bool igpuFromHw = false;

        try
        {
            cpuPkgWatts = _cpuPowerCollector.GetCpuPackagePowerWatts(hwSamples);
        }
        catch (Exception ex) { _logger.LogError(ex, "IntelCpuPowerCollector threw"); }

        try
        {
            // TryGetIgpuPowerWatts scans hwSamples once and reports whether the
            // value came from an LHM power sensor (true) or the GPU-Engine
            // fallback (false) — replaces a second hwSamples.Any() scan.
            igpuValue = _gpuPowerCollector.TryGetIgpuPowerWatts(hwSamples, gpuSamples, out igpuFromHw);
            igpuActivityPct = _gpuPowerCollector.GetIgpuActivityPercent(gpuSamples);
        }
        catch (Exception ex) { _logger.LogError(ex, "IntelGpuPowerCollector threw"); }

        // ── Refresh source statuses ───────────────
        if (now - _lastSourceStatusRefresh >= SourceStatusRefreshInterval)
        {
            sourceStatuses = RefreshSourceStatuses(now, hwSamples, gpuSamples, gpuSamplingEnabled);
            _lastSourceStatusRefresh = now;
        }

        // ── Write to database ─────────────────────
        var writeQueue = _writeQueue;
        if (writeQueue is not null)
        {
            await writeQueue.Writer.WriteAsync(new MonitoringWriteBatch
            {
                PowerSample = batterySample,
                ProcessSamples = processSamples,
                GpuSamples = gpuSamplesAreFresh ? gpuSamples : Array.Empty<GpuProcessSample>(),
                HardwareSamples = hwSamples,
                SourceStatuses = sourceStatuses
            });
        }

        // ── Publish snapshot ──────────────────────
        var snapshot = new MonitoringSnapshot
        {
            SnapshotUtc = now,
            PowerSample = batterySample,
            ProcessSamples = processSamples,
            GpuSamples = gpuSamples,
            GpuSamplesAreFresh = gpuSamplesAreFresh,
            GpuSamplingEnabled = gpuSamplingEnabled,
            HardwareSamples = hwSamples,
            CpuPackagePowerWatts = cpuPkgWatts,
            IgpuPowerValue = igpuValue,
            IgpuActivityPercent = igpuActivityPct,
            IgpuFromHardwareSensor = igpuFromHw,
            SourceStatuses = sourceStatuses
        };

        lock (_lock) { _latestSnapshot = snapshot; }
    }

    private IReadOnlyList<SourceStatus> RefreshSourceStatuses(
        DateTime now,
        IReadOnlyList<HardwareSensorSample> hwSamples,
        IReadOnlyList<GpuProcessSample> gpuSamples,
        bool gpuSamplingEnabled)
    {
        var statuses = new List<SourceStatus>();

        TryAddStatus(() => _batteryCollector.GetStatus(), statuses);
        TryAddStatus(() => _processCollector.GetStatus(), statuses);
        if (gpuSamplingEnabled)
        {
            TryAddStatus(() => _gpuEngineCollector.GetStatus(), statuses);
        }
        else
        {
            statuses.Add(new SourceStatus
            {
                TimestampUtc = now,
                SourceName = "WindowsGpuEngine",
                IsAvailable = false,
                Status = "Disabled",
                Details = "GPU Engine sampling is disabled",
                RequiresAdmin = false
            });
        }
        TryAddStatus(() => _lhmCollector.GetStatus(), statuses);
        TryAddStatus(() => _cpuPowerCollector.GetStatus(hwSamples), statuses);
        TryAddStatus(() => _gpuPowerCollector.GetStatus(hwSamples, gpuSamples), statuses);

        return statuses;
    }

    private void TryAddStatus(Func<SourceStatus> getStatus, List<SourceStatus> statuses)
    {
        try { statuses.Add(getStatus()); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get source status"); }
    }

    private sealed class MonitoringWriteBatch
    {
        public SystemPowerSample? PowerSample { get; init; }
        public IReadOnlyList<ProcessSample> ProcessSamples { get; init; } = Array.Empty<ProcessSample>();
        public IReadOnlyList<GpuProcessSample> GpuSamples { get; init; } = Array.Empty<GpuProcessSample>();
        public IReadOnlyList<HardwareSensorSample> HardwareSamples { get; init; } = Array.Empty<HardwareSensorSample>();
        public IReadOnlyList<SourceStatus> SourceStatuses { get; init; } = Array.Empty<SourceStatus>();
    }
}
