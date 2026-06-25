using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;
using PowerCulprit.Core.Services;
using PowerCulprit.Storage;
using SkiaSharp;

namespace PowerCulprit.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int DisplayProcessLimit = 20;
    private const int DisplayCycleListLimit = 20;
    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(400);
    private static readonly double TenMinutesInTicks = TimeSpan.FromMinutes(10).Ticks;

    private readonly IMonitoringService _monitor;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly DatabaseManager _database;
    private readonly PowerCulpritAnalyzer _analyzer = new();
    private readonly Axis _timeAxis;

    private IReadOnlyList<SystemPowerSample> _loadedPowerSamples = Array.Empty<SystemPowerSample>();
    private IReadOnlyList<BatteryCycle> _selectedCycleSegments = Array.Empty<BatteryCycle>();
    private BatteryDisplayCycle? _selectedDisplayCycle;
    private DateTime _historyFromUtc = DateTime.MinValue;
    private DateTime _historyToUtc = DateTime.MinValue;
    private DateTime _selectedFromUtc = DateTime.MinValue;
    private DateTime _selectedToUtc = DateTime.MinValue;
    private bool _isCycleMode;
    private bool _suppressAxisSelection;
    private bool _suppressCycleSelection;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _selectionDebounceCts;

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

        _timeAxis = BuildTimeAxis();
        _timeAxis.PropertyChanged += OnTimeAxisPropertyChanged;
        TimeAxes = new ICartesianAxis[] { _timeAxis };
        PowerYAxes = BuildPowerYAxes();
        MainChartSeries = BuildMainChartSeries();

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

    public ObservableCollection<DateTimePoint> BatteryPercentSeries { get; } = new();
    public ObservableCollection<DateTimePoint> DischargeRateSeries { get; } = new();
    public ObservableCollection<BatteryDisplayCycleRow> CycleRows { get; } = new();
    public ObservableCollection<HistoricalProcessRow> ProcessRows { get; } = new();
    public ObservableCollection<SourceStatusRow> SourceStatusRows { get; } = new();

    public ISeries[] MainChartSeries { get; }
    public ICartesianAxis[] TimeAxes { get; }
    public ICartesianAxis[] PowerYAxes { get; }

    private ISeries[] BuildMainChartSeries()
    {
        return new ISeries[]
        {
            new LineSeries<DateTimePoint>
            {
                Values = BatteryPercentSeries,
                Name = "Battery %",
                Stroke = new SolidColorPaint(SKColors.DodgerBlue) { StrokeThickness = 2 },
                GeometrySize = 0,
                Fill = null,
                ScalesYAt = 0
            },
            new LineSeries<DateTimePoint>
            {
                Values = DischargeRateSeries,
                Name = "Discharge W",
                Stroke = new SolidColorPaint(SKColors.OrangeRed) { StrokeThickness = 2 },
                GeometrySize = 0,
                Fill = null,
                ScalesYAt = 1
            }
        };
    }

    private static Axis BuildTimeAxis()
    {
        return new Axis
        {
            Labeler = value =>
            {
                try
                {
                    var ticks = (long)value;
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                        return string.Empty;
                    return new DateTime(ticks).ToString("HH:mm");
                }
                catch
                {
                    return string.Empty;
                }
            },
            LabelsRotation = 20,
            MinStep = TenMinutesInTicks,
            UnitWidth = TenMinutesInTicks,
            TextSize = 11
        };
    }

    private static ICartesianAxis[] BuildPowerYAxes()
    {
        return new ICartesianAxis[]
        {
            new Axis
            {
                Name = "Battery %",
                MinLimit = 0,
                MaxLimit = 100,
                TextSize = 11
            },
            new Axis
            {
                Name = "Discharge W",
                Position = AxisPosition.End,
                TextSize = 11
            }
        };
    }

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
        _selectionDebounceCts?.Cancel();

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
                .Where(s => !_isCycleMode || !s.IsAcOnline)
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
            IsHistoryLoading = true;
            ErrorText = "";
            HistoryStatusText = "Rebuilding battery cycles...";

            await _database.RebuildBatteryCyclesAsync();
            token.ThrowIfCancellationRequested();

            var cyclesTask = _database.GetLatestBatteryDisplayCyclesAsync(DisplayCycleListLimit);
            var statusTask = _database.GetLatestSourceStatusesAsync();
            await Task.WhenAll(cyclesTask, statusTask);
            token.ThrowIfCancellationRequested();

            var cycles = await cyclesTask;
            var statuses = await statusTask;
            ApplySourceStatuses(statuses);

            var targetCycle = preferredCycleId.HasValue
                ? cycles.FirstOrDefault(c => c.Id == preferredCycleId.Value)
                : null;
            targetCycle ??= cycles.FirstOrDefault();

            ApplyCycleRows(cycles, targetCycle?.Id);

            if (targetCycle is null)
            {
                CycleStatusText = "No battery cycles found";
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
            ErrorText = $"Refresh failed: {ex.Message}";
            HistoryStatusText = "Refresh failed";
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                IsHistoryLoading = false;
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
            IsHistoryLoading = true;
            ErrorText = "";
            HistoryStatusText = "Loading last 6 hours...";

            await _database.RebuildBatteryCyclesAsync();
            token.ThrowIfCancellationRequested();

            var cyclesTask = _database.GetLatestBatteryDisplayCyclesAsync(DisplayCycleListLimit);
            var statusTask = _database.GetLatestSourceStatusesAsync();
            await Task.WhenAll(cyclesTask, statusTask);
            token.ThrowIfCancellationRequested();

            ApplyCycleRows(await cyclesTask, selectedCycleId: null);
            ApplySourceStatuses(await statusTask);
            await LoadLastSixHoursCoreAsync(resetRange, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh last 6 hours");
            ErrorText = $"Refresh failed: {ex.Message}";
            HistoryStatusText = "Refresh failed";
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                IsHistoryLoading = false;
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
            IsHistoryLoading = true;
            ErrorText = "";
            HistoryStatusText = "Loading battery cycle...";

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
            ErrorText = $"Cycle load failed: {ex.Message}";
            HistoryStatusText = "Cycle load failed";
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                IsHistoryLoading = false;
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
        HistoryStatusText = $"{powerSamples.Count} power samples, {FormatCycleSummary(displayCycle)}";
        await AnalyzeSelectedRangeAsync();
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
        HistoryStatusText = $"{powerSamples.Count} power samples, {FormatRange(_historyFromUtc, _historyToUtc)}";
        await AnalyzeSelectedRangeAsync();
    }

    private async Task AnalyzeSelectedRangeAsync()
    {
        if (!HasSelectedRange())
        {
            AnalysisStatusText = "No range selected";
            ProcessRows.Clear();
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
            IsAnalyzing = true;
            AnalysisStatusText = $"Analyzing {FormatRange(fromUtc, toUtc)}...";

            var powerTask = _database.GetSystemPowerSamplesAsync(fromUtc, toUtc);
            var processTask = _database.GetProcessSamplesForAnalysisAsync(fromUtc, toUtc);
            var gpuTask = _database.GetGpuProcessSamplesAsync(fromUtc, toUtc);
            await Task.WhenAll(powerTask, processTask, gpuTask);
            token.ThrowIfCancellationRequested();

            var powerSamples = await powerTask;
            var processSamples = await processTask;
            var gpuSamples = await gpuTask;
            var intervals = GetSelectedAnalysisIntervals(fromUtc, toUtc);
            if (intervals.Count == 0)
            {
                ProcessRows.Clear();
                AnalysisStatusText = "No offline cycle segment in selected range";
                return;
            }

            powerSamples = FilterByIntervals(powerSamples, s => s.TimestampUtc, intervals)
                .Where(s => !_isCycleMode || !s.IsAcOnline)
                .ToList();
            processSamples = FilterByIntervals(processSamples, s => s.TimestampUtc, intervals);
            gpuSamples = FilterByIntervals(gpuSamples, s => s.TimestampUtc, intervals);

            var windowStart = intervals.Min(i => i.FromUtc);
            var windowEnd = intervals.Max(i => i.ToUtc);
            var window = windowEnd - windowStart;

            var results = await Task.Run(
                () => _analyzer.Analyze(
                    window,
                    DisplayProcessLimit,
                    powerSamples,
                    processSamples,
                    gpuSamples,
                    ProcessGroupingMode.ProcessName),
                token);
            token.ThrowIfCancellationRequested();

            ApplyAnalysisResults(results);
            AnalysisStatusText = $"{results.Count} processes, {processSamples.Count} process samples, {gpuSamples.Count} GPU samples";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Historical analysis failed");
            ErrorText = $"Analysis failed: {ex.Message}";
            AnalysisStatusText = "Analysis failed";
        }
        finally
        {
            if (ReferenceEquals(_analysisCts, cts))
            {
                IsAnalyzing = false;
                _analysisCts = null;
            }
            cts.Dispose();
        }
    }

    private void OnMonitorRunningChanged(bool running)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsRunning = running;
            SamplingStatusText = running ? "Running" : "Stopped";
        });
    }

    private void OnTimeAxisPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressAxisSelection)
            return;

        if (e.PropertyName != nameof(Axis.MinLimit) && e.PropertyName != nameof(Axis.MaxLimit))
            return;

        if (!_timeAxis.MinLimit.HasValue || !_timeAxis.MaxLimit.HasValue)
            return;

        var fromUtc = ChartTicksToUtc(_timeAxis.MinLimit.Value);
        var toUtc = ChartTicksToUtc(_timeAxis.MaxLimit.Value);
        if (toUtc <= fromUtc)
            return;

        var clamped = ClampRange(fromUtc, toUtc);
        var updateAxis = clamped.FromUtc != fromUtc || clamped.ToUtc != toUtc;
        SetSelectedRange(clamped.FromUtc, clamped.ToUtc, updateAxis);
        ScheduleAnalyzeSelectedRange();
    }

    private void ScheduleAnalyzeSelectedRange()
    {
        _selectionDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _selectionDebounceCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SelectionDebounce, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (!cts.IsCancellationRequested)
                    _ = AnalyzeSelectedRangeAsync();
            });
        });
    }

    private void ReplaceChartData(IReadOnlyList<SystemPowerSample> powerSamples)
    {
        BatteryPercentSeries.Clear();
        DischargeRateSeries.Clear();

        foreach (var sample in powerSamples)
        {
            var localTime = sample.TimestampUtc.ToLocalTime();
            if (sample.BatteryPercent.HasValue)
                BatteryPercentSeries.Add(new DateTimePoint(localTime, sample.BatteryPercent.Value));

            var dischargeW = GetDischargeWatts(sample);
            if (dischargeW.HasValue)
                DischargeRateSeries.Add(new DateTimePoint(localTime, dischargeW.Value));
        }
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

        BatteryPercentSeries.Clear();
        DischargeRateSeries.Clear();
        CycleRows.Clear();
        ProcessRows.Clear();
        SourceStatusRows.Clear();

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

        _suppressAxisSelection = true;
        try
        {
            _timeAxis.MinLimit = null;
            _timeAxis.MaxLimit = null;
        }
        finally
        {
            _suppressAxisSelection = false;
        }
    }

    private void UpdateStatusBar(SystemPowerSample? latest)
    {
        AcStatusText = "--";
        BatteryPercentText = "--";
        DischargeRateText = "--";

        if (latest is null)
            return;

        AcStatusText = latest.IsAcOnline ? "AC" : "Battery";
        if (latest.BatteryPercent.HasValue)
            BatteryPercentText = $"{latest.BatteryPercent.Value:F1}%";

        var dischargeW = GetDischargeWatts(latest);
        if (dischargeW.HasValue)
            DischargeRateText = $"{dischargeW.Value:F1} W";
    }

    private void ApplySourceStatuses(IReadOnlyList<SourceStatus> statuses)
    {
        SourceStatusRows.Clear();
        foreach (var ss in statuses)
        {
            SourceStatusRows.Add(new SourceStatusRow
            {
                SourceName = ss.SourceName,
                Status = ss.Status,
                Details = ss.Details ?? ""
            });
        }
    }

    private void ApplyCycleRows(IReadOnlyList<BatteryDisplayCycle> cycles, long? selectedCycleId)
    {
        CycleRows.Clear();
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

            CycleRows.Add(row);
            if (selectedCycleId.HasValue && cycle.Id == selectedCycleId.Value)
                selectedRow = row;
        }

        _suppressCycleSelection = true;
        try
        {
            SelectedCycleRow = selectedRow;
        }
        finally
        {
            _suppressCycleSelection = false;
        }
    }

    private void ApplyAnalysisResults(IReadOnlyList<CulpritReportItem> results)
    {
        ProcessRows.Clear();
        foreach (var item in results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.AvgCpuPercent ?? 0.0)
            .ThenByDescending(r => r.MaxGpuPercent ?? 0.0)
            .ThenBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(DisplayProcessLimit))
        {
            ProcessRows.Add(new HistoricalProcessRow
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
            });
        }
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
        SelectedRangeText = FormatRange(fromUtc, toUtc);

        if (!updateAxis)
            return;

        _suppressAxisSelection = true;
        try
        {
            _timeAxis.MinLimit = UtcToChartTicks(fromUtc);
            _timeAxis.MaxLimit = UtcToChartTicks(toUtc);
        }
        finally
        {
            _suppressAxisSelection = false;
        }
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
    {
        if (sample.ChargeRateMilliwatts.HasValue)
        {
            var chargeRateW = sample.ChargeRateMilliwatts.Value / 1000.0;
            return chargeRateW < 0 ? Math.Abs(chargeRateW) : 0.0;
        }

        return sample.EstimatedDischargeWatts;
    }

    private static double UtcToChartTicks(DateTime utc)
        => utc.ToLocalTime().Ticks;

    private static DateTime ChartTicksToUtc(double value)
    {
        var ticks = (long)Math.Clamp(value, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
        var local = DateTime.SpecifyKind(new DateTime(ticks), DateTimeKind.Local);
        return local.ToUniversalTime();
    }

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
            await writer.WriteLineAsync(
                $"{s.TimestampUtc:O},{s.IsAcOnline},{s.BatteryPercent},{s.ChargeRateMilliwatts},{s.EstimatedDischargeWatts},{s.PowerMode}");
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
