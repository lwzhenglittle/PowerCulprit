using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;
using PowerCulprit.Core.Services;
using PowerCulprit.Storage;

namespace PowerCulprit.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int DisplayProcessLimit = 20;
    private const int DisplayCycleListLimit = 20;
    private const double DefaultDischargeAxisMax = 10;
    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(400);

    private readonly IMonitoringService _monitor;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DatabaseManager _database;
    private readonly PowerCulpritAnalyzer _analyzer = new();

    private IReadOnlyList<SystemPowerSample> _loadedPowerSamples = Array.Empty<SystemPowerSample>();
    private IReadOnlyList<BatteryCycle> _selectedCycleSegments = Array.Empty<BatteryCycle>();
    private BatteryDisplayCycle? _selectedDisplayCycle;
    private DateTime _historyFromUtc = DateTime.MinValue;
    private DateTime _historyToUtc = DateTime.MinValue;
    private DateTime _selectedFromUtc = DateTime.MinValue;
    private DateTime _selectedToUtc = DateTime.MinValue;
    private bool _isCycleMode;
    private bool _suppressCycleSelection;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _analysisCts;
    private DispatcherQueueTimer? _selectionDebounceTimer;

    public MainViewModel(
        IMonitoringService monitor,
        ILogger<MainViewModel> logger,
        DispatcherQueue dispatcher,
        DatabaseManager database)
    {
        _monitor = monitor;
        _logger = logger;
        _dispatcher = dispatcher;
        _database = database;

        IsGpuSamplingEnabled = _monitor.IsGpuSamplingEnabled;

        _monitor.RunningChanged += OnMonitorRunningChanged;
    }

    [ObservableProperty]
    public partial string BatteryPercentText { get; set; } = "--";

    [ObservableProperty]
    public partial string AcStatusText { get; set; } = "--";

    [ObservableProperty]
    public partial string DischargeRateText { get; set; } = "--";

    [ObservableProperty]
    public partial string SamplingStatusText { get; set; } = "Stopped";

    [ObservableProperty]
    public partial string HistoryStatusText { get; set; } = "No history loaded";

    [ObservableProperty]
    public partial string AnalysisStatusText { get; set; } = "No range analyzed";

    [ObservableProperty]
    public partial string SelectedRangeText { get; set; } = "--";

    [ObservableProperty]
    public partial string CycleStatusText { get; set; } = "No cycle selected";

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsGpuSamplingEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsHistoryLoading { get; set; }

    [ObservableProperty]
    public partial bool IsAnalyzing { get; set; }

    [ObservableProperty]
    public partial string ExcludedIntervalsSummary { get; set; } = "";

    [ObservableProperty]
    public partial BatteryDisplayCycleRow? SelectedCycleRow { get; set; }

    partial void OnIsGpuSamplingEnabledChanged(bool value)
    {
        _monitor.SetGpuSamplingEnabled(value);
    }

    partial void OnSelectedCycleRowChanged(BatteryDisplayCycleRow? value)
    {
        if (_suppressCycleSelection || value is null)
            return;

        _ = LoadSelectedCycleAsync(value.Cycle, resetRange: true);
    }

    [ObservableProperty]
    public partial ObservableCollection<BatteryDisplayCycleRow> CycleRows { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<HistoricalProcessRow> ProcessRows { get; set; } = new();

    [ObservableProperty]
    public partial ObservableCollection<SourceStatusRow> SourceStatusRows { get; set; } = new();

    [ObservableProperty]
    public partial IReadOnlyList<PowerChartSample> ChartSamples { get; set; } = Array.Empty<PowerChartSample>();

    [ObservableProperty]
    public partial DateTime ChartHistoryFromUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartHistoryToUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartVisibleFromUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartVisibleToUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial double ChartDischargeAxisMax { get; set; } = DefaultDischargeAxisMax;

    [RelayCommand]
    private async Task StartMonitoring()
    {
        try { await _monitor.StartAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to start monitoring"); }
    }

    [RelayCommand]
    private async Task StopMonitoring()
    {
        try { await _monitor.StopAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to stop monitoring"); }
    }

    [RelayCommand]
    private Task RefreshHistory()
        => _isCycleMode
            ? RefreshCycleHistoryAsync(preferredCycleId: _selectedDisplayCycle?.Id, resetRange: false)
            : RefreshLastSixHoursAsync(resetRange: false);

    [RelayCommand]
    private Task Latest()
        => LatestCycle();

    [RelayCommand]
    private Task LatestCycle()
        => RefreshCycleHistoryAsync(preferredCycleId: null, resetRange: true);

    [RelayCommand]
    private Task Last6Hours()
        => RefreshLastSixHoursAsync(resetRange: true);

    [RelayCommand]
    private async Task ClearHistory()
    {
        _refreshCts?.Cancel();
        _analysisCts?.Cancel();
        _selectionDebounceTimer?.Stop();

        try
        {
            IsHistoryLoading = true;
            ErrorText = "";
            HistoryStatusText = "Clearing history...";
            AnalysisStatusText = "Clearing history...";

            var deleted = await _database.ClearHistoricalDataAsync();

            ClearHistoricalView();
            HistoryStatusText = $"Cleared {deleted} historical rows";
            AnalysisStatusText = "No range analyzed";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear historical data");
            ErrorText = $"Clear history failed: {ex.Message}";
            HistoryStatusText = "Clear history failed";
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        try
        {
            var (fromUtc, toUtc) = GetSelectedOrDefaultRange();
            var intervals = GetSelectedAnalysisIntervals(fromUtc, toUtc);
            var powerSamples = await _database.GetSystemPowerSamplesAsync(fromUtc, toUtc);
            var processSamples = await _database.GetProcessSamplesForAnalysisAsync(fromUtc, toUtc);

            powerSamples = FilterByIntervals(powerSamples, s => s.TimestampUtc, intervals)
                .Where(s => !_isCycleMode || s.IsAcOnline != true)
                .ToList();
            processSamples = FilterByIntervals(processSamples, s => s.TimestampUtc, intervals);

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var filePath = Path.Combine(desktopPath, $"powerculprit_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");

            await WriteCsvFileAsync(filePath, powerSamples, processSamples, _selectedDisplayCycle);
            _logger.LogInformation("Exported CSV to {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV export failed");
            ErrorText = $"CSV export failed: {ex.Message}";
        }
    }

    private async Task RefreshCycleHistoryAsync(long? preferredCycleId, bool resetRange)
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsHistoryLoading = true;
                ErrorText = "";
                HistoryStatusText = "Rebuilding battery cycles...";
            });

            await _database.RebuildBatteryCyclesAsync();
            token.ThrowIfCancellationRequested();

            var cyclesTask = _database.GetLatestBatteryDisplayCyclesAsync(DisplayCycleListLimit);
            var statusTask = _database.GetLatestSourceStatusesAsync();
            await Task.WhenAll(cyclesTask, statusTask);
            token.ThrowIfCancellationRequested();

            var cycles = await cyclesTask;
            var statuses = await statusTask;

            var targetCycle = preferredCycleId.HasValue
                ? cycles.FirstOrDefault(c => c.Id == preferredCycleId.Value)
                : null;
            targetCycle ??= cycles.FirstOrDefault();

            await RunOnUiThreadAsync(() =>
            {
                ApplySourceStatuses(statuses);
                ApplyCycleRows(cycles, targetCycle?.Id);
            });

            if (targetCycle is null)
            {
                await RunOnUiThreadAsync(() => CycleStatusText = "No battery cycles found");
                await LoadLastSixHoursCoreAsync(resetRange: true, token);
            }
            else
            {
                await LoadDisplayCycleCoreAsync(targetCycle, resetRange, token, statuses);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh battery cycles");
            await RunOnUiThreadAsync(() =>
            {
                ErrorText = $"Refresh failed: {ex.Message}";
                HistoryStatusText = "Refresh failed";
            });
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                await RunOnUiThreadAsync(() => IsHistoryLoading = false);
                _refreshCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task RefreshLastSixHoursAsync(bool resetRange)
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsHistoryLoading = true;
                ErrorText = "";
                HistoryStatusText = "Loading last 6 hours...";
            });

            await _database.RebuildBatteryCyclesAsync();
            token.ThrowIfCancellationRequested();

            var cyclesTask = _database.GetLatestBatteryDisplayCyclesAsync(DisplayCycleListLimit);
            var statusTask = _database.GetLatestSourceStatusesAsync();
            await Task.WhenAll(cyclesTask, statusTask);
            token.ThrowIfCancellationRequested();

            await RunOnUiThreadAsync(() =>
            {
                ApplyCycleRows(cyclesTask.Result, selectedCycleId: null);
                ApplySourceStatuses(statusTask.Result);
            });
            await LoadLastSixHoursCoreAsync(resetRange, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh last 6 hours");
            await RunOnUiThreadAsync(() =>
            {
                ErrorText = $"Refresh failed: {ex.Message}";
                HistoryStatusText = "Refresh failed";
            });
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                await RunOnUiThreadAsync(() => IsHistoryLoading = false);
                _refreshCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task LoadSelectedCycleAsync(BatteryDisplayCycle displayCycle, bool resetRange)
    {
        _refreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsHistoryLoading = true;
                ErrorText = "";
                HistoryStatusText = "Loading battery cycle...";
            });

            var statuses = await _database.GetLatestSourceStatusesAsync();
            token.ThrowIfCancellationRequested();
            await LoadDisplayCycleCoreAsync(displayCycle, resetRange, token, statuses);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load selected battery cycle");
            await RunOnUiThreadAsync(() =>
            {
                ErrorText = $"Cycle load failed: {ex.Message}";
                HistoryStatusText = "Cycle load failed";
            });
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                await RunOnUiThreadAsync(() => IsHistoryLoading = false);
                _refreshCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task LoadDisplayCycleCoreAsync(
        BatteryDisplayCycle displayCycle,
        bool resetRange,
        CancellationToken token,
        IReadOnlyList<SourceStatus>? statuses = null)
    {
        var fromUtc = displayCycle.StartUtc;
        var toUtc = displayCycle.EffectiveEndUtc;
        if (toUtc <= fromUtc)
            toUtc = fromUtc.AddMinutes(1);

        var powerTask = _database.GetSystemPowerSamplesAsync(fromUtc, toUtc);
        var rawCyclesTask = _database.GetBatteryCyclesForDisplayCycleAsync(displayCycle.Id);
        var statusTask = statuses is null ? _database.GetLatestSourceStatusesAsync() : Task.FromResult(statuses);
        await Task.WhenAll(powerTask, rawCyclesTask, statusTask);
        token.ThrowIfCancellationRequested();

        var powerSamples = await powerTask;
        var rawCycles = await rawCyclesTask;
        var loadedStatuses = await statusTask;

        await RunOnUiThreadAsync(() =>
        {
            _isCycleMode = true;
            _selectedDisplayCycle = displayCycle;
            _selectedCycleSegments = rawCycles;
            _loadedPowerSamples = powerSamples;
            _historyFromUtc = fromUtc;
            _historyToUtc = toUtc;

            ReplaceChartData(powerSamples);
            UpdateStatusBar(powerSamples.LastOrDefault());
            ApplySourceStatuses(loadedStatuses);

            if (resetRange || !HasSelectedRange())
            {
                SetSelectedRange(fromUtc, toUtc, updateAxis: true);
            }
            else
            {
                var clamped = ClampRange(_selectedFromUtc, _selectedToUtc);
                var changed = clamped.FromUtc != _selectedFromUtc || clamped.ToUtc != _selectedToUtc;
                SetSelectedRange(clamped.FromUtc, clamped.ToUtc, updateAxis: changed);
            }

            CycleStatusText = FormatCycleSummary(displayCycle);
            HistoryStatusText = $"{powerSamples.Count} power samples, {FormatCycleSummary(displayCycle)}; 24h raw + 7d aggregate history";
        });
        StartAnalyzeSelectedRangeInBackground();
    }

    private async Task LoadLastSixHoursCoreAsync(bool resetRange, CancellationToken token)
    {
        var toUtc = DateTime.UtcNow;
        var fromUtc = toUtc - DefaultHistoryWindow;

        var powerTask = _database.GetSystemPowerSamplesAsync(fromUtc, toUtc);
        var statusTask = _database.GetLatestSourceStatusesAsync();
        await Task.WhenAll(powerTask, statusTask);
        token.ThrowIfCancellationRequested();

        var powerSamples = await powerTask;
        var statuses = await statusTask;

        await RunOnUiThreadAsync(() =>
        {
            _isCycleMode = false;
            _selectedDisplayCycle = null;
            _selectedCycleSegments = Array.Empty<BatteryCycle>();
            _loadedPowerSamples = powerSamples;
            _historyFromUtc = fromUtc;
            _historyToUtc = toUtc;

            _suppressCycleSelection = true;
            try
            {
                SelectedCycleRow = null;
            }
            finally
            {
                _suppressCycleSelection = false;
            }

            ReplaceChartData(powerSamples);
            UpdateStatusBar(powerSamples.LastOrDefault());
            ApplySourceStatuses(statuses);

            if (resetRange || !HasSelectedRange())
            {
                SetSelectedRange(fromUtc, toUtc, updateAxis: true);
            }
            else
            {
                var clamped = ClampRange(_selectedFromUtc, _selectedToUtc);
                var changed = clamped.FromUtc != _selectedFromUtc || clamped.ToUtc != _selectedToUtc;
                SetSelectedRange(clamped.FromUtc, clamped.ToUtc, updateAxis: changed);
            }

            CycleStatusText = "Last 6 hours";
            HistoryStatusText = $"{powerSamples.Count} power samples, {FormatRange(_historyFromUtc, _historyToUtc)}; 24h raw + 7d aggregate history";
        });
        StartAnalyzeSelectedRangeInBackground();
    }

    private async Task AnalyzeSelectedRangeAsync()
    {
        if (!HasSelectedRange())
        {
            await RunOnUiThreadAsync(() =>
            {
                AnalysisStatusText = "No range selected";
                ProcessRows.Clear();
            });
            return;
        }

        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        var token = cts.Token;
        var fromUtc = _selectedFromUtc;
        var toUtc = _selectedToUtc;

        try
        {
            await RunOnUiThreadAsync(() =>
            {
                IsAnalyzing = true;
                AnalysisStatusText = $"Analyzing {FormatRange(fromUtc, toUtc)}...";
            });

            var rawIntervals = GetSelectedAnalysisIntervals(fromUtc, toUtc);
            if (rawIntervals.Count == 0)
            {
                await RunOnUiThreadAsync(() =>
                {
                    ProcessRows.Clear();
                    AnalysisStatusText = "No offline cycle segment in selected range";
                });
                return;
            }

            // Fetch power state events and power samples to detect sleep/gap
            // intervals that should be excluded from process attribution.
            var powerTask = _database.GetSystemPowerSamplesAsync(fromUtc, toUtc);
            var eventsTask = _monitor.GetPowerStateEventsAsync(fromUtc, toUtc);
            var processTask = _database.GetProcessAggregatesForAnalysisAsync(rawIntervals);
            var gpuTask = _database.GetGpuProcessAggregatesAsync(rawIntervals);
            await Task.WhenAll(powerTask, eventsTask, processTask, gpuTask);
            token.ThrowIfCancellationRequested();

            var powerSamples = await powerTask;
            var powerEvents = await eventsTask;
            var processAggregates = await processTask;
            var gpuAggregates = await gpuTask;

            // Detect unobserved gaps and subtract them from analysis intervals.
            var gapIntervals = GapDetector.Detect(powerEvents, powerSamples, fromUtc, toUtc);
            var awakeIntervals = IntervalSubtractor.Exclude(rawIntervals,
                gapIntervals.Select(g => (g.StartUtc, g.EndUtc)).ToList());

            if (awakeIntervals.Count == 0)
            {
                var sleepDuration = gapIntervals
                    .Where(g => g.Kind == GapKind.ConfirmedSleep)
                    .Aggregate(TimeSpan.Zero, (sum, g) => sum + (g.EndUtc - g.StartUtc));
                var unknownDuration = gapIntervals
                    .Where(g => g.Kind == GapKind.UnknownGap)
                    .Aggregate(TimeSpan.Zero, (sum, g) => sum + (g.EndUtc - g.StartUtc));

                await RunOnUiThreadAsync(() =>
                {
                    ProcessRows.Clear();
                    var parts = new List<string>();
                    if (sleepDuration > TimeSpan.Zero)
                        parts.Add($"sleep/hibernate ({FormatDuration(sleepDuration)})");
                    if (unknownDuration > TimeSpan.Zero)
                        parts.Add($"unknown gaps ({FormatDuration(unknownDuration)})");
                    var reason = parts.Count > 0 ? string.Join(", ", parts) : "no awake samples";
                    AnalysisStatusText = $"No awake intervals in selected range — {reason} excluded from attribution";
                    ExcludedIntervalsSummary = BuildExcludedSummary(gapIntervals);
                });
                return;
            }

            // Build excluded-interval summary for the UI.
            var excludedSummary = BuildExcludedSummary(gapIntervals);

            // Limit power samples and aggregates to awake intervals only.
            powerSamples = FilterByIntervals(powerSamples, s => s.TimestampUtc, awakeIntervals)
                .Where(s => !_isCycleMode || s.IsAcOnline != true)
                .ToList();

            // Re-query aggregates narrowed to awake intervals.
            processAggregates = await _database.GetProcessAggregatesForAnalysisAsync(awakeIntervals);
            gpuAggregates = await _database.GetGpuProcessAggregatesAsync(awakeIntervals);

            var windowStart = awakeIntervals.Min(i => i.FromUtc);
            var windowEnd = awakeIntervals.Max(i => i.ToUtc);
            var window = windowEnd - windowStart;

            var results = await Task.Run(
                () => _analyzer.AnalyzeAggregates(
                    window,
                    DisplayProcessLimit,
                    powerSamples,
                    processAggregates,
                    gpuAggregates),
                token);
            token.ThrowIfCancellationRequested();

            var processSampleCount = processAggregates.Sum(a => a.SampleCount);
            await RunOnUiThreadAsync(() =>
            {
                ApplyAnalysisResults(results);
                ExcludedIntervalsSummary = excludedSummary;
                var excludedNote = gapIntervals.Count > 0
                    ? $"; {gapIntervals.Count(g => g.Kind == GapKind.ConfirmedSleep)} sleep, {gapIntervals.Count(g => g.Kind == GapKind.UnknownGap)} unknown gaps excluded"
                    : "";
                AnalysisStatusText = $"{results.Count} processes, {processSampleCount} active/topN process samples aggregated{excludedNote}";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical analysis failed");
            await RunOnUiThreadAsync(() =>
            {
                ErrorText = $"Analysis failed: {ex.Message}";
                AnalysisStatusText = "Analysis failed";
            });
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
            {
                await RunOnUiThreadAsync(() => IsAnalyzing = false);
                _analysisCts = null;
            }
            cts.Dispose();
        }
    }

    private void StartAnalyzeSelectedRangeInBackground()
    {
        _ = AnalyzeSelectedRangeAsync();
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue UI update."));
        }

        return tcs.Task;
    }

    private void OnMonitorRunningChanged(bool running)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsRunning = running;
            SamplingStatusText = running ? "Running" : "Stopped";
        });
    }

    private void ScheduleAnalyzeSelectedRange()
    {
        _selectionDebounceTimer ??= CreateSelectionDebounceTimer();
        _selectionDebounceTimer.Stop();
        _selectionDebounceTimer.Start();
    }

    private DispatcherQueueTimer CreateSelectionDebounceTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = SelectionDebounce;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _ = AnalyzeSelectedRangeAsync();
        return timer;
    }

    private void ReplaceChartData(IReadOnlyList<SystemPowerSample> powerSamples)
    {
        ChartSamples = powerSamples
            .Select(sample => new PowerChartSample(
                sample.TimestampUtc,
                sample.BatteryPercent,
                GetDischargeWatts(sample)))
            .ToList();

        ChartHistoryFromUtc = _historyFromUtc;
        ChartHistoryToUtc = _historyToUtc;
        ChartDischargeAxisMax = CalculateDischargeAxisMax(powerSamples);
    }

    private void ClearChartSeries()
    {
        ChartSamples = Array.Empty<PowerChartSample>();
        ChartHistoryFromUtc = DateTime.MinValue;
        ChartHistoryToUtc = DateTime.MinValue;
        ChartVisibleFromUtc = DateTime.MinValue;
        ChartVisibleToUtc = DateTime.MinValue;
        ChartDischargeAxisMax = DefaultDischargeAxisMax;
    }

    private static double CalculateDischargeAxisMax(IReadOnlyList<SystemPowerSample> powerSamples)
    {
        var max = powerSamples
            .Select(GetDischargeWatts)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var padded = max <= 0 ? DefaultDischargeAxisMax : Math.Ceiling(max * 1.15);
        return Math.Max(DefaultDischargeAxisMax, padded);
    }

    private void ClearHistoricalView()
    {
        _loadedPowerSamples = Array.Empty<SystemPowerSample>();
        _selectedCycleSegments = Array.Empty<BatteryCycle>();
        _selectedDisplayCycle = null;
        _historyFromUtc = DateTime.MinValue;
        _historyToUtc = DateTime.MinValue;
        _selectedFromUtc = DateTime.MinValue;
        _selectedToUtc = DateTime.MinValue;
        _isCycleMode = false;

        ClearChartSeries();
        CycleRows = new ObservableCollection<BatteryDisplayCycleRow>();
        ProcessRows = new ObservableCollection<HistoricalProcessRow>();
        SourceStatusRows = new ObservableCollection<SourceStatusRow>();

        AcStatusText = "--";
        BatteryPercentText = "--";
        DischargeRateText = "--";
        SelectedRangeText = "--";
        CycleStatusText = "No cycle selected";

        _suppressCycleSelection = true;
        try
        {
            SelectedCycleRow = null;
        }
        finally
        {
            _suppressCycleSelection = false;
        }

    }

    private void UpdateStatusBar(SystemPowerSample? latest)
    {
        AcStatusText = "--";

        if (latest is null)
            return;

        AcStatusText = latest.IsAcOnline switch
        {
            true => "AC",
            false => "Battery",
            _ => "--"
        };
    }

    private void UpdateRangeMetrics(DateTime fromUtc, DateTime toUtc)
    {
        var rangeSamples = _loadedPowerSamples
            .Where(sample => sample.TimestampUtc >= fromUtc && sample.TimestampUtc <= toUtc)
            .OrderBy(sample => sample.TimestampUtc)
            .ToList();

        BatteryPercentText = FormatBatteryDrop(rangeSamples);
        DischargeRateText = FormatEnergyUsed(rangeSamples);
    }

    private static string FormatBatteryDrop(IReadOnlyList<SystemPowerSample> samples)
    {
        var first = samples.FirstOrDefault(sample => sample.BatteryPercent.HasValue);
        var last = samples.LastOrDefault(sample => sample.BatteryPercent.HasValue);
        if (first?.BatteryPercent is null || last?.BatteryPercent is null)
            return "--";

        var from = first.BatteryPercent.Value;
        var to = last.BatteryPercent.Value;
        var drop = Math.Max(0, from - to);
        return $"{drop:F1}% ({from:F1}% -> {to:F1}%)";
    }

    private static string FormatEnergyUsed(IReadOnlyList<SystemPowerSample> samples)
    {
        var wh = CalculateEnergyUsedWh(samples);
        if (!wh.HasValue)
            return "--";

        var firstCapacity = samples.FirstOrDefault(sample => sample.RemainingCapacityMWh.HasValue)?.RemainingCapacityMWh;
        var lastCapacity = samples.LastOrDefault(sample => sample.RemainingCapacityMWh.HasValue)?.RemainingCapacityMWh;
        if (firstCapacity.HasValue && lastCapacity.HasValue)
        {
            var fromWh = firstCapacity.Value / 1000.0;
            var toWh = lastCapacity.Value / 1000.0;
            return $"{wh.Value:F1} Wh ({fromWh:F1} -> {toWh:F1} Wh)";
        }

        var watts = samples
            .Select(GetDischargeWatts)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        return watts.Count == 0
            ? $"{wh.Value:F1} Wh"
            : $"{wh.Value:F1} Wh ({watts.Min():F1}-{watts.Max():F1} W)";
    }

    private static double? CalculateEnergyUsedWh(IReadOnlyList<SystemPowerSample> samples)
        => DischargeEnergyCalculator.CalculateEnergyUsedWh(samples);

    private void ApplySourceStatuses(IReadOnlyList<SourceStatus> statuses)
    {
        var rows = new ObservableCollection<SourceStatusRow>(
            statuses.Select(ss => new SourceStatusRow
            {
                SourceName = ss.SourceName,
                Status = ss.Status,
                Details = ss.Details ?? ""
            }));

        SourceStatusRows = rows;
    }

    private void ApplyCycleRows(IReadOnlyList<BatteryDisplayCycle> cycles, long? selectedCycleId)
    {
        var rows = new ObservableCollection<BatteryDisplayCycleRow>();
        BatteryDisplayCycleRow? selectedRow = null;

        foreach (var cycle in cycles)
        {
            var row = new BatteryDisplayCycleRow
            {
                Cycle = cycle,
                Label = FormatCycleLabel(cycle),
                RangeText = FormatRange(cycle.StartUtc, cycle.EffectiveEndUtc),
                DischargeText = FormatCycleDischarge(cycle),
                RawCycleCountText = cycle.RawCycleCount.ToString(),
                ConfidenceText = cycle.Confidence.ToString()
            };

            rows.Add(row);
            if (selectedCycleId.HasValue && cycle.Id == selectedCycleId.Value)
                selectedRow = row;
        }

        _suppressCycleSelection = true;
        try
        {
            CycleRows = rows;
            SelectedCycleRow = selectedRow;
        }
        finally
        {
            _suppressCycleSelection = false;
        }
    }

    private void ApplyAnalysisResults(IReadOnlyList<CulpritReportItem> results)
    {
        var rows = new ObservableCollection<HistoricalProcessRow>(results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.AvgCpuPercent ?? 0.0)
            .ThenByDescending(r => r.MaxGpuPercent ?? 0.0)
            .ThenBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(DisplayProcessLimit)
            .Select(item => new HistoricalProcessRow
            {
                Rank = item.Rank,
                ProcessName = string.IsNullOrEmpty(item.ServiceName)
                    ? item.ProcessName
                    : $"{item.ProcessName} ({item.ServiceName})",
                ScoreText = item.Score.ToString("F1"),
                AvgCpuText = FormatPercent(item.AvgCpuPercent),
                MaxCpuText = FormatPercent(item.MaxCpuPercent),
                AvgGpuText = FormatPercent(item.AvgGpuPercent),
                MaxGpuText = FormatPercent(item.MaxGpuPercent),
                DiskMbText = item.DiskMb.HasValue ? item.DiskMb.Value.ToString("F1") : "--",
                ForegroundMinutesText = item.ForegroundActiveSeconds.HasValue
                    ? (item.ForegroundActiveSeconds.Value / 60.0).ToString("F1")
                    : "--",
                BackgroundMinutesText = item.BackgroundActiveSeconds.HasValue
                    ? (item.BackgroundActiveSeconds.Value / 60.0).ToString("F1")
                    : "--",
                ReasonText = item.Reason
            }));

        ProcessRows = rows;
    }

    private IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> GetSelectedAnalysisIntervals(
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (!_isCycleMode || _selectedCycleSegments.Count == 0)
            return new List<(DateTime FromUtc, DateTime ToUtc)> { (fromUtc, toUtc) };

        var intervals = new List<(DateTime FromUtc, DateTime ToUtc)>();
        foreach (var cycle in _selectedCycleSegments)
        {
            var segmentFrom = cycle.StartUtc > fromUtc ? cycle.StartUtc : fromUtc;
            var segmentTo = cycle.LastSampleUtc < toUtc ? cycle.LastSampleUtc : toUtc;
            if (segmentTo > segmentFrom)
                intervals.Add((segmentFrom, segmentTo));
        }

        return intervals;
    }

    private static IReadOnlyList<T> FilterByIntervals<T>(
        IReadOnlyList<T> samples,
        Func<T, DateTime> getTimestamp,
        IReadOnlyList<(DateTime FromUtc, DateTime ToUtc)> intervals)
    {
        if (intervals.Count == 0)
            return Array.Empty<T>();

        var filtered = new List<T>();
        foreach (var sample in samples)
        {
            var timestamp = getTimestamp(sample);
            if (intervals.Any(i => timestamp >= i.FromUtc && timestamp <= i.ToUtc))
                filtered.Add(sample);
        }

        return filtered;
    }

    private void SetSelectedRange(DateTime fromUtc, DateTime toUtc, bool updateAxis)
    {
        if (toUtc <= fromUtc)
            return;

        _selectedFromUtc = fromUtc;
        _selectedToUtc = toUtc;
        SelectedRangeText = FormatRangeWithDuration(fromUtc, toUtc);
        UpdateRangeMetrics(fromUtc, toUtc);
        ChartVisibleFromUtc = fromUtc;
        ChartVisibleToUtc = toUtc;
    }

    public void SetChartVisibleRangeFromUserInteraction(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            return;

        var clamped = ClampRange(fromUtc, toUtc);
        SetSelectedRange(clamped.FromUtc, clamped.ToUtc, updateAxis: false);
        ScheduleAnalyzeSelectedRange();
    }

    private (DateTime FromUtc, DateTime ToUtc) ClampRange(DateTime fromUtc, DateTime toUtc)
    {
        if (_historyFromUtc == DateTime.MinValue || _historyToUtc == DateTime.MinValue)
            return (fromUtc, toUtc);

        var from = fromUtc < _historyFromUtc ? _historyFromUtc : fromUtc;
        var to = toUtc > _historyToUtc ? _historyToUtc : toUtc;
        if (to <= from)
            return (_historyFromUtc, _historyToUtc);

        return (from, to);
    }

    private bool HasSelectedRange()
        => _selectedFromUtc != DateTime.MinValue && _selectedToUtc > _selectedFromUtc;

    private (DateTime FromUtc, DateTime ToUtc) GetSelectedOrDefaultRange()
    {
        if (HasSelectedRange())
            return (_selectedFromUtc, _selectedToUtc);

        if (_selectedDisplayCycle is not null)
            return (_selectedDisplayCycle.StartUtc, _selectedDisplayCycle.EffectiveEndUtc);

        var toUtc = DateTime.UtcNow;
        return (toUtc - DefaultHistoryWindow, toUtc);
    }

    private static double? GetDischargeWatts(SystemPowerSample sample)
        => DischargeEnergyCalculator.GetDischargeWatts(sample);

    private static string FormatCycleLabel(BatteryDisplayCycle cycle)
    {
        var state = cycle.IsOpen ? "Open" : "Closed";
        return $"{cycle.StartUtc.ToLocalTime():MM-dd HH:mm} | {FormatCycleDischarge(cycle)} | {cycle.RawCycleCount} raw | {state}";
    }

    private static string FormatCycleSummary(BatteryDisplayCycle cycle)
    {
        var state = cycle.IsOpen ? "open" : "closed";
        return $"Cycle {FormatRange(cycle.StartUtc, cycle.EffectiveEndUtc)}, {FormatCycleDischarge(cycle)}, {cycle.RawCycleCount} raw, {cycle.Confidence} confidence, {state}";
    }

    private static string FormatCycleDischarge(BatteryDisplayCycle cycle)
    {
        var percent = cycle.DischargePercent.HasValue
            ? $"{cycle.DischargePercent.Value:F1}%"
            : "--%";
        var wh = cycle.DischargeWh.HasValue
            ? $"{cycle.DischargeWh.Value:F1} Wh"
            : "-- Wh";
        return $"{percent} / {wh}";
    }

    private static string FormatRange(DateTime fromUtc, DateTime toUtc)
        => $"{fromUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {toUtc.ToLocalTime():HH:mm}";

    private static string FormatRangeWithDuration(DateTime fromUtc, DateTime toUtc)
    {
        var minutes = Math.Max(0, (toUtc - fromUtc).TotalMinutes);
        return $"{FormatRange(fromUtc, toUtc)} ({minutes:F0} min)";
    }

    private static string BuildExcludedSummary(IReadOnlyList<PowerStateInterval> gaps)
    {
        if (gaps.Count == 0)
            return "";

        var sleepCount = 0;
        var unknownCount = 0;
        var sleepDuration = TimeSpan.Zero;
        var unknownDuration = TimeSpan.Zero;

        foreach (var gap in gaps)
        {
            if (gap.Kind == GapKind.ConfirmedSleep)
            {
                sleepCount++;
                sleepDuration += gap.EndUtc - gap.StartUtc;
            }
            else
            {
                unknownCount++;
                unknownDuration += gap.EndUtc - gap.StartUtc;
            }
        }

        var parts = new List<string>();
        if (sleepCount > 0)
            parts.Add($"{sleepCount} confirmed sleep/hibernate interval{(sleepCount > 1 ? "s" : "")} ({FormatDuration(sleepDuration)})");
        if (unknownCount > 0)
            parts.Add($"{unknownCount} unknown monitoring gap{(unknownCount > 1 ? "s" : "")} ({FormatDuration(unknownDuration)})");

        return parts.Count > 0
            ? $"Excluded from attribution: {string.Join(", ", parts)}"
            : "";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return $"{hours}h {minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            var minutes = (int)duration.TotalMinutes;
            var seconds = duration.Seconds;
            return $"{minutes}m {seconds}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }

    private static string FormatPercent(double? value)
        => value.HasValue ? value.Value.ToString("F1") : "--";

    private static async Task WriteCsvFileAsync(
        string path,
        IReadOnlyList<SystemPowerSample> powerSamples,
        IReadOnlyList<ProcessSample> processSamples,
        BatteryDisplayCycle? displayCycle)
    {
        await using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);

        await writer.WriteLineAsync("# PowerCulprit Data Export");
        await writer.WriteLineAsync($"# Exported: {DateTime.UtcNow:O}");
        if (displayCycle is not null)
        {
            await writer.WriteLineAsync($"# BatteryCycleId: {displayCycle.Id}");
            await writer.WriteLineAsync($"# BatteryCycleRangeUtc: {displayCycle.StartUtc:O} - {displayCycle.EffectiveEndUtc:O}");
            await writer.WriteLineAsync($"# BatteryCycleDischarge: {FormatCycleDischarge(displayCycle)}");
            await writer.WriteLineAsync($"# BatteryCycleRawCount: {displayCycle.RawCycleCount}");
            await writer.WriteLineAsync($"# BatteryCycleConfidence: {displayCycle.Confidence}");
        }
        await writer.WriteLineAsync();

        await writer.WriteLineAsync("## System Power Samples");
        await writer.WriteLineAsync("TimestampUtc,IsAcOnline,BatteryPercent,ChargeRateMilliwatts,EstimatedDischargeWatts,PowerMode");
        foreach (var s in powerSamples)
        {
            var ac = s.IsAcOnline switch { true => "true", false => "false", _ => "" };
            await writer.WriteLineAsync(
                $"{s.TimestampUtc:O},{ac},{s.BatteryPercent},{s.ChargeRateMilliwatts},{s.EstimatedDischargeWatts},{s.PowerMode}");
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("## Process Samples");
        await writer.WriteLineAsync("TimestampUtc,Pid,ProcessName,CpuPercent,WorkingSetMb,DiskReadBytesPerSec,DiskWriteBytesPerSec,IsForeground");
        foreach (var s in processSamples)
        {
            await writer.WriteLineAsync(
                $"{s.TimestampUtc:O},{s.Pid},\"{s.ProcessName}\",{s.CpuPercent},{s.WorkingSetMb},{s.DiskReadBytesPerSecond},{s.DiskWriteBytesPerSecond},{s.IsForegroundProcess}");
        }
    }
}
