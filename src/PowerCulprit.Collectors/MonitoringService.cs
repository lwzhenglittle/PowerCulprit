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
    private readonly IWindowsEtwActivityCollector _etwCollector;
    private readonly IWmiActivityCollector _wmiActivityCollector;
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
    private DateTime _lastCompactionUtc = DateTime.MinValue;
    private DateTime? _lastSuspendEventUtc;
    private IReadOnlyList<GpuProcessSample> _lastGpuSamples = Array.Empty<GpuProcessSample>();
    private static readonly TimeSpan SourceStatusRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GpuEngineCollectInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan CompactionInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RawRetention = TimeSpan.FromHours(24);
    private const int RetentionDays = 7;
    private const int PersistedProcessTopN = 50;
    private const double PersistedProcessCpuThreshold = 0.1;
    private const double PersistedGpuUtilizationThreshold = 0.1;

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
        IWindowsEtwActivityCollector etwCollector,
        IWmiActivityCollector wmiActivityCollector,
        DatabaseManager databaseManager,
        ILogger<MonitoringService> logger)
    {
        _batteryCollector = batteryCollector;
        _processCollector = processCollector;
        _gpuEngineCollector = gpuEngineCollector;
        _lhmCollector = lhmCollector;
        _cpuPowerCollector = cpuPowerCollector;
        _gpuPowerCollector = gpuPowerCollector;
        _etwCollector = etwCollector;
        _wmiActivityCollector = wmiActivityCollector;
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

        try { await _etwCollector.StopAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Windows ETW activity collector failed to stop"); }

        try { await _wmiActivityCollector.StopAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "WMI Activity collector failed to stop"); }

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

    /// <inheritdoc/>
    public async Task RecordPowerStateEventAsync(
        PowerStateEventKind kind,
        DateTime? timestampUtc = null,
        string? details = null)
    {
        var evt = new PowerStateEvent
        {
            TimestampUtc = timestampUtc ?? DateTime.UtcNow,
            Kind = kind,
            Source = "PowerCulprit",
            Details = details
        };

        _logger.LogInformation("Power state event: {Kind} at {Timestamp}", kind, evt.TimestampUtc);

        // Suspend events must be persisted before the system sleeps —
        // use a short timeout to avoid blocking the power transition.
        if (kind == PowerStateEventKind.Suspend)
        {
            try
            {
                await _databaseManager.InsertPowerStateEventAsync(evt)
                    .WaitAsync(TimeSpan.FromMilliseconds(750));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Suspend power state event timed out after 750ms — may not have persisted");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist suspend power state event");
            }
        }
        else
        {
            try
            {
                await _databaseManager.InsertPowerStateEventAsync(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist power state event {Kind}", kind);
            }
        }

        // Track last suspend time for snapshot awareness
        if (kind == PowerStateEventKind.Suspend)
            _lastSuspendEventUtc = evt.TimestampUtc;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PowerStateEvent>> GetPowerStateEventsAsync(
        DateTime fromUtc,
        DateTime toUtc)
    {
        try
        {
            return await _databaseManager.GetPowerStateEventsAsync(fromUtc, toUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query power state events");
            return Array.Empty<PowerStateEvent>();
        }
    }

    // ──────────────────────────────────────────────
    //  Main loop
    // ──────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Database initialization is the one startup step that must succeed for
        // monitoring to be useful. If it fails (disk full, locked DB file,
        // permissions), bail out and flip IsRunning to false so the UI does not
        // sit showing "running" with no data ever arriving. Every other startup
        // step below degrades independently.
        try
        {
            await _databaseManager.InitializeAsync();
            await _databaseManager.InsertSessionStartMarkerAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database initialization failed — monitoring cannot start");
            TransitionToStopped();
            return;
        }

        try
        {
            try
            {
                var deduplicated = await _databaseManager.DeduplicateSourceStatusTimestampTiesAsync();
                if (deduplicated > 0)
                    _logger.LogInformation("Removed {Count} duplicate source status rows", deduplicated);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source status deduplication failed — continuing");
            }

            try
            {
                var lastWmiRecordId = await _databaseManager.GetLatestWmiActivityEventRecordIdAsync();
                if (lastWmiRecordId.HasValue)
                    _wmiActivityCollector.SetLastRecordId(lastWmiRecordId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize WMI Activity event-log cursor");
            }

            await CleanupOldDataIfDueAsync(force: true);

            // Optional ETW collection starts a background consumer and degrades on failure.
            try { await _etwCollector.StartAsync(cancellationToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Windows ETW activity collector failed to start"); }

            // Optional WMI Activity collection reads the Operational event log incrementally.
            try { await _wmiActivityCollector.StartAsync(cancellationToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "WMI Activity collector failed to start"); }

            // One-time hardware init (must be done on startup)
            _lhmCollector.Initialize();

            while (!cancellationToken.IsCancellationRequested)
            {
                var cycleStart = DateTime.UtcNow;
                try
                {
                    await CleanupOldDataIfDueAsync(force: false);
                    await CompactRawDataIfDueAsync(force: false);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown via StopAsync cancellation — do not transition,
            // StopAsync owns the final IsRunning=false.
            _logger.LogInformation("Monitoring loop cancelled");
        }
        catch (Exception ex)
        {
            // A fault that escaped the per-cycle guard would otherwise leave
            // IsRunning stuck at true with the loop dead. Flip to stopped so the
            // UI reflects reality. _loopTask never enters the Faulted state, so
            // there is no unobserved-task window.
            _logger.LogError(ex, "Monitoring loop terminated unexpectedly");
            TransitionToStopped();
        }
    }

    /// <summary>
    /// Flips IsRunning to false and signals the writer loop to exit, without
    /// awaiting the loop/writer tasks (the caller is the loop task itself, so
    /// awaiting would deadlock). Used when the loop dies on its own — startup
    /// failure or an unexpected exception — so the UI does not report "running"
    /// while nothing is sampling. Idempotent: StopAsync calling it later is a
    /// no-op. Does not dispose _cts or stop ETW/WMI — that remains StopAsync's
    /// job.
    /// </summary>
    private void TransitionToStopped()
    {
        Channel<MonitoringWriteBatch>? writeQueue;
        lock (_lock)
        {
            if (!IsRunning)
                return;
            IsRunning = false;
            writeQueue = _writeQueue;
        }

        // Let the writer drain and exit rather than blocking on a full queue.
        writeQueue?.Writer.TryComplete();
        RaiseRunningChanged(false);
    }

    private async Task CleanupOldDataIfDueAsync(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastCleanupUtc < CleanupInterval)
            return;

        _lastCleanupUtc = now;
        try
        {
            var deleted = await _databaseManager.CleanupOldDataAsync(RetentionDays, RawRetention);
            if (deleted > 0)
                _logger.LogInformation(
                    "Cleaned up {DeletedRows} rows older than raw retention {RawRetentionHours}h / aggregate retention {RetentionDays}d",
                    deleted,
                    RawRetention.TotalHours,
                    RetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical data cleanup failed");
        }
    }

    private async Task CompactRawDataIfDueAsync(bool force)
    {
        if (Environment.GetEnvironmentVariable("POWERCULPRIT_DISABLE_COMPACTION") == "1")
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastCompactionUtc < CompactionInterval)
            return;

        _lastCompactionUtc = now;
        try
        {
            var result = await _databaseManager.CompactRawDataAsync(now - RawRetention);
            if (result.ProcessRowsCompacted > 0 || result.GpuRowsCompacted > 0 || result.HardwareRowsCompacted > 0)
            {
                _logger.LogInformation(
                    "Compacted raw database rows through {CompactedThroughUtc}: process={ProcessRows}, gpu={GpuRows}, hardware={HardwareRows}",
                    result.CompactedThroughUtc,
                    result.ProcessRowsCompacted,
                    result.GpuRowsCompacted,
                    result.HardwareRowsCompacted);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical data compaction failed");
        }
    }

    private async Task RunWriteLoopAsync()
    {
        var queue = _writeQueue;
        if (queue is null)
            return;

        try
        {
            await foreach (var batch in queue.Reader.ReadAllAsync())
            {
                try
                {
                    await _databaseManager.InsertMonitoringCycleAsync(
                        batch.PowerSample,
                        batch.ProcessSamples,
                        batch.GpuSamples,
                        batch.HardwareSamples,
                        batch.SourceStatuses,
                        batch.WmiActivitySamples);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Database write failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel completion during shutdown — expected.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database writer loop terminated unexpectedly");
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
        IReadOnlyList<WmiActivitySample> wmiActivitySamples = Array.Empty<WmiActivitySample>();
        var gpuSamplesAreFresh = false;
        var gpuSamplingEnabled = IsGpuSamplingEnabled;

        // Snapshot the GPU-sampling bookkeeping fields under the same lock that
        // SetGpuSamplingEnabled mutates them under. This avoids racing the UI
        // thread toggling GPU sampling mid-cycle: reads below use the locals,
        // and writes below also take the lock so a disable cannot leave stale
        // values behind for the next cycle.
        DateTime lastGpuEngineCollectUtc;
        IReadOnlyList<GpuProcessSample> lastGpuSamples;
        DateTime lastSourceStatusRefresh;
        lock (_lock)
        {
            lastGpuEngineCollectUtc = _lastGpuEngineCollectUtc;
            lastGpuSamples = _lastGpuSamples;
            lastSourceStatusRefresh = _lastSourceStatusRefresh;
        }

        // ── Collect battery ────────────────────────
        try { batterySample = _batteryCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "BatteryPowerCollector threw"); }

        // ── Collect processes ─────────────────────
        try { processSamples = _processCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "ProcessResourceCollector threw"); }

        // ── Snapshot and merge ETW activity ─────────
        try
        {
            foreach (var sample in processSamples)
                _etwCollector.RecordPolledProcess(sample.Pid);
            var etwSnapshot = _etwCollector.SnapshotAndReset(now);
            processSamples = ProcessEtwMerger.Merge(processSamples, etwSnapshot);
        }
        catch (Exception ex) { _logger.LogError(ex, "WindowsEtwActivityCollector snapshot/merge threw"); }

        // ── Snapshot WMI Activity events ─────────────
        try
        {
            var wmiSnapshot = _wmiActivityCollector.SnapshotAndReset(now);
            wmiActivitySamples = wmiSnapshot.Samples;
        }
        catch (Exception ex) { _logger.LogError(ex, "WMI Activity collector snapshot threw"); }

        // ── Collect GPU Engine ────────────────────
        if (gpuSamplingEnabled && now - lastGpuEngineCollectUtc >= GpuEngineCollectInterval)
        {
            try
            {
                gpuSamples = _gpuEngineCollector.Collect();
                lastGpuSamples = gpuSamples;
                lastGpuEngineCollectUtc = now;
                lock (_lock)
                {
                    _lastGpuSamples = lastGpuSamples;
                    _lastGpuEngineCollectUtc = lastGpuEngineCollectUtc;
                }
                gpuSamplesAreFresh = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WindowsGpuEngineCollector threw");
                gpuSamples = lastGpuSamples;
            }
        }
        else if (gpuSamplingEnabled)
        {
            gpuSamples = lastGpuSamples;
        }
        else
        {
            gpuSamples = Array.Empty<GpuProcessSample>();
        }

        // ── Collect hardware sensors (LHM) ────────
        try { hwSamples = _lhmCollector.Collect(); }
        catch (Exception ex) { _logger.LogError(ex, "LHMCollector threw"); }

        // ── Derive CPU / iGPU power ─────────────────
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
        if (now - lastSourceStatusRefresh >= SourceStatusRefreshInterval)
        {
            sourceStatuses = RefreshSourceStatuses(now, hwSamples, gpuSamples, gpuSamplingEnabled);
            lastSourceStatusRefresh = now;
            lock (_lock) { _lastSourceStatusRefresh = lastSourceStatusRefresh; }
        }

        // ── Write to database ─────────────────────
        var writeQueue = _writeQueue;
        if (writeQueue is not null)
        {
            await writeQueue.Writer.WriteAsync(new MonitoringWriteBatch
            {
                PowerSample = batterySample,
                ProcessSamples = SelectPersistedProcessSamples(processSamples),
                GpuSamples = gpuSamplesAreFresh
                    ? SelectPersistedGpuSamples(gpuSamples)
                    : Array.Empty<GpuProcessSample>(),
                HardwareSamples = hwSamples,
                SourceStatuses = sourceStatuses,
                WmiActivitySamples = wmiActivitySamples
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

    private static IReadOnlyList<ProcessSample> SelectPersistedProcessSamples(IReadOnlyList<ProcessSample> samples)
    {
        if (samples.Count == 0)
            return samples;

        var selected = new HashSet<int>();
        var results = new List<ProcessSample>(Math.Min(samples.Count, PersistedProcessTopN * 3));

        AddWhere(samples, results, selected, IsAlwaysPersistedProcess);
        AddTopBy(samples, results, selected, s => s.CpuPercent ?? 0, PersistedProcessTopN);
        AddTopBy(samples, results, selected, s => s.WorkingSetMb ?? 0, PersistedProcessTopN);
        AddTopBy(samples, results, selected, s => GetDiskActivity(s) + GetNetworkActivity(s), PersistedProcessTopN);

        return results.Count == samples.Count ? samples : results;
    }

    private static bool IsAlwaysPersistedProcess(ProcessSample sample)
    {
        return sample.IsForegroundProcess
            || (sample.CpuPercent ?? 0) >= PersistedProcessCpuThreshold
            || GetDiskActivity(sample) > 0
            || GetNetworkActivity(sample) > 0
            || (sample.ProcessStartCount ?? 0) > 0
            || (sample.ProcessStopCount ?? 0) > 0
            || (sample.ShortLivedProcessCount ?? 0) > 0
            || !string.IsNullOrWhiteSpace(sample.ServiceName);
    }

    private static void AddWhere(
        IReadOnlyList<ProcessSample> samples,
        List<ProcessSample> results,
        HashSet<int> selected,
        Func<ProcessSample, bool> predicate)
    {
        for (var i = 0; i < samples.Count; i++)
        {
            if (predicate(samples[i]) && selected.Add(i))
                results.Add(samples[i]);
        }
    }

    private static void AddTopBy(
        IReadOnlyList<ProcessSample> samples,
        List<ProcessSample> results,
        HashSet<int> selected,
        Func<ProcessSample, double> scoreSelector,
        int count)
    {
        foreach (var item in samples
            .Select((sample, index) => new { sample, index, score = scoreSelector(sample) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(count))
        {
            if (selected.Add(item.index))
                results.Add(item.sample);
        }
    }

    private static double GetDiskActivity(ProcessSample sample)
        => Math.Max(0, sample.DiskReadBytesPerSecond ?? 0)
            + Math.Max(0, sample.DiskWriteBytesPerSecond ?? 0);

    private static double GetNetworkActivity(ProcessSample sample)
        => Math.Max(0, sample.NetworkReceiveBytesPerSecond ?? 0)
            + Math.Max(0, sample.NetworkSendBytesPerSecond ?? 0);

    private static IReadOnlyList<GpuProcessSample> SelectPersistedGpuSamples(IReadOnlyList<GpuProcessSample> samples)
    {
        if (samples.Count == 0)
            return samples;

        var results = samples
            .Where(s => s.UtilizationPercent > PersistedGpuUtilizationThreshold)
            .ToList();
        return results.Count == samples.Count ? samples : results;
    }

    private IReadOnlyList<SourceStatus> RefreshSourceStatuses(
        DateTime now,
        IReadOnlyList<HardwareSensorSample> hwSamples,
        IReadOnlyList<GpuProcessSample> gpuSamples,
        bool gpuSamplingEnabled)
    {
        var statuses = new List<SourceStatus>();

        TryAddStatus(() => _batteryCollector.GetStatus(), statuses, now);
        TryAddStatus(() => _processCollector.GetStatus(), statuses, now);
        if (gpuSamplingEnabled)
        {
            TryAddStatus(() => _gpuEngineCollector.GetStatus(), statuses, now);
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
        TryAddStatus(() => _lhmCollector.GetStatus(), statuses, now);
        TryAddStatus(() => _cpuPowerCollector.GetStatus(hwSamples), statuses, now);
        TryAddStatus(() => _gpuPowerCollector.GetStatus(hwSamples, gpuSamples), statuses, now);
        TryAddStatus(() => _etwCollector.GetStatus(), statuses, now);
        TryAddStatus(() => _wmiActivityCollector.GetStatus(), statuses, now);

        return statuses;
    }

    private void TryAddStatus(Func<SourceStatus> getStatus, List<SourceStatus> statuses, DateTime now)
    {
        try { statuses.Add(getStatus() with { TimestampUtc = now }); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to get source status"); }
    }

    private sealed class MonitoringWriteBatch
    {
        public SystemPowerSample? PowerSample { get; init; }
        public IReadOnlyList<ProcessSample> ProcessSamples { get; init; } = Array.Empty<ProcessSample>();
        public IReadOnlyList<GpuProcessSample> GpuSamples { get; init; } = Array.Empty<GpuProcessSample>();
        public IReadOnlyList<HardwareSensorSample> HardwareSamples { get; init; } = Array.Empty<HardwareSensorSample>();
        public IReadOnlyList<SourceStatus> SourceStatuses { get; init; } = Array.Empty<SourceStatus>();
        public IReadOnlyList<WmiActivitySample> WmiActivitySamples { get; init; } = Array.Empty<WmiActivitySample>();
    }
}
