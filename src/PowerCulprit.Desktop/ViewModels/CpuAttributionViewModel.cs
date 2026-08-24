using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;
using PowerCulprit.Storage;

namespace PowerCulprit.Desktop.ViewModels;

public partial class CpuAttributionViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(400);

    private readonly DatabaseManager _database;
    private readonly ILogger<CpuAttributionViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _loadCts;
    private DispatcherQueueTimer? _rangeDebounceTimer;
    private int _powerSampleCount;
    private int _hardwareSampleCount;

    private DateTime _historyFromUtc = DateTime.MinValue;
    private DateTime _historyToUtc = DateTime.MinValue;
    private DateTime _selectedFromUtc = DateTime.MinValue;
    private DateTime _selectedToUtc = DateTime.MinValue;

    public CpuAttributionViewModel(
        DatabaseManager database,
        ILogger<CpuAttributionViewModel> logger,
        DispatcherQueue dispatcher)
    {
        _database = database;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    [ObservableProperty]
    public partial string BatteryDropText { get; set; } = "--";

    [ObservableProperty]
    public partial string EnergyUsedText { get; set; } = "--";

    [ObservableProperty]
    public partial string CpuClockText { get; set; } = "--";

    [ObservableProperty]
    public partial string CpuLoadText { get; set; } = "--";

    [ObservableProperty]
    public partial string CpuPowerText { get; set; } = "--";

    [ObservableProperty]
    public partial string CpuEnergyText { get; set; } = "--";

    [ObservableProperty]
    public partial string CorrelationText { get; set; } = "--";

    [ObservableProperty]
    public partial string AttributionSummaryText { get; set; } = "No range loaded.";

    [ObservableProperty]
    public partial string HistoryStatusText { get; set; } = "No CPU history loaded";

    [ObservableProperty]
    public partial string SelectedRangeText { get; set; } = "--";

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<CpuTimelineSample> ChartSamples { get; set; } = Array.Empty<CpuTimelineSample>();

    [ObservableProperty]
    public partial DateTime ChartHistoryFromUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartHistoryToUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartVisibleFromUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial DateTime ChartVisibleToUtc { get; set; } = DateTime.MinValue;

    [ObservableProperty]
    public partial double CpuPowerAxisMax { get; set; } = 30;

    [ObservableProperty]
    public partial double CpuClockAxisMax { get; set; } = 5000;

    [ObservableProperty]
    public partial double EnergyAxisMax { get; set; } = 5;

    [RelayCommand]
    private Task Refresh()
        => LoadRangeAsync(GetSelectedOrDefaultRange().FromUtc, GetSelectedOrDefaultRange().ToUtc);

    [RelayCommand]
    private Task LatestCycle()
        => LoadLatestCycleAsync();

    [RelayCommand]
    private Task Last6Hours()
    {
        var toUtc = DateTime.UtcNow;
        return LoadRangeAsync(toUtc - DefaultHistoryWindow, toUtc);
    }

    public Task InitializeAsync()
        => LoadLatestCycleAsync();

    public void SetChartVisibleRangeFromUserInteraction(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            return;

        var clamped = ClampRange(fromUtc, toUtc);

        // Cheap feedback (selected-range text + axis sync) applies immediately;
        // the O(n) attribution recompute is debounced so wheel/drag interaction
        // does not run a full analysis per tick.
        _selectedFromUtc = clamped.FromUtc;
        _selectedToUtc = clamped.ToUtc;
        ChartVisibleFromUtc = clamped.FromUtc;
        ChartVisibleToUtc = clamped.ToUtc;
        SelectedRangeText = FormatRangeWithDuration(clamped.FromUtc, clamped.ToUtc);

        ScheduleRangeAttribution();
    }

    private void ScheduleRangeAttribution()
    {
        _rangeDebounceTimer ??= CreateRangeDebounceTimer();
        _rangeDebounceTimer.Stop();
        _rangeDebounceTimer.Start();
    }

    private DispatcherQueueTimer CreateRangeDebounceTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = SelectionDebounce;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => UpdateRangeAttribution(_selectedFromUtc, _selectedToUtc);
        return timer;
    }

    private async Task LoadLatestCycleAsync()
    {
        try
        {
            await Task.Run(() => _database.RebuildBatteryCyclesAsync());
            var cycles = await Task.Run(() => _database.GetLatestBatteryDisplayCyclesAsync(1));
            var cycle = cycles.FirstOrDefault();
            if (cycle is null)
            {
                var toUtc = DateTime.UtcNow;
                await LoadRangeAsync(toUtc - DefaultHistoryWindow, toUtc);
                return;
            }

            await LoadRangeAsync(cycle.StartUtc, cycle.EffectiveEndUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load latest CPU attribution cycle");
            ErrorText = $"CPU history failed: {ex.Message}";
        }
    }

    private async Task LoadRangeAsync(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
            return;

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        try
        {
            IsLoading = true;
            ErrorText = "";
            HistoryStatusText = "Loading CPU attribution...";

            var powerTask = Task.Run(() => _database.GetSystemPowerSamplesAsync(fromUtc, toUtc));
            var hardwareTask = Task.Run(() => _database.GetHardwareSensorSamplesAsync(fromUtc, toUtc));
            await Task.WhenAll(powerTask, hardwareTask);
            token.ThrowIfCancellationRequested();

            var powerSamples = await powerTask;
            var hardwareSamples = await hardwareTask;
            var timeline = await Task.Run(
                () => CpuTimelineBuilder.Build(powerSamples, hardwareSamples),
                token);

            ApplyLoadedRange(fromUtc, toUtc, timeline, powerSamples.Count, hardwareSamples.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load CPU attribution data");
            ErrorText = $"CPU attribution failed: {ex.Message}";
            HistoryStatusText = "CPU attribution failed";
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
            }
            cts.Dispose();
        }
    }

    private void ApplyLoadedRange(
        DateTime fromUtc,
        DateTime toUtc,
        IReadOnlyList<CpuTimelineSample> timeline,
        int powerSampleCount,
        int hardwareSampleCount)
    {
        _historyFromUtc = fromUtc;
        _historyToUtc = toUtc;
        _powerSampleCount = powerSampleCount;
        _hardwareSampleCount = hardwareSampleCount;

        ChartSamples = timeline;
        ChartHistoryFromUtc = fromUtc;
        ChartHistoryToUtc = toUtc;

        CpuPowerAxisMax = AxisMax(timeline.Select(s => s.CpuPackagePowerWatts), 30);
        CpuClockAxisMax = AxisMax(timeline.Select(s => s.CpuAverageClockMhz), 5000);
        EnergyAxisMax = AxisMax(timeline.Select(s => s.CumulativeEnergyWh), 5);

        ApplySelectedRange(fromUtc, toUtc);
    }

    private void ApplySelectedRange(DateTime fromUtc, DateTime toUtc)
    {
        _selectedFromUtc = fromUtc;
        _selectedToUtc = toUtc;

        ChartVisibleFromUtc = fromUtc;
        ChartVisibleToUtc = toUtc;
        SelectedRangeText = FormatRangeWithDuration(fromUtc, toUtc);

        UpdateRangeAttribution(fromUtc, toUtc);
    }

    private void UpdateRangeAttribution(DateTime fromUtc, DateTime toUtc)
    {
        var selectedTimeline = ChartSamples
            .Where(sample => sample.TimestampUtc >= fromUtc && sample.TimestampUtc <= toUtc)
            .ToList();
        var attribution = CpuAttributionAnalyzer.Analyze(selectedTimeline);

        BatteryDropText = attribution.BatteryDropPercent.HasValue ? $"{attribution.BatteryDropPercent.Value:F1}%" : "--";
        EnergyUsedText = attribution.EnergyUsedWh.HasValue ? $"{attribution.EnergyUsedWh.Value:F2} Wh" : "--";
        CpuClockText = FormatClockRange(attribution.AvgCpuClockMhz, attribution.MaxCpuClockMhz);
        CpuLoadText = FormatRangePercent(attribution.AvgCpuLoadPercent, attribution.MaxCpuLoadPercent);
        CpuPowerText = FormatRangeWatts(attribution.AvgCpuPackagePowerWatts, attribution.MaxCpuPackagePowerWatts);
        CpuEnergyText = attribution.CpuPackageEnergyWh.HasValue ? $"{attribution.CpuPackageEnergyWh.Value:F2} Wh" : "--";
        CorrelationText = attribution.CpuPowerDischargeCorrelation.HasValue ? attribution.CpuPowerDischargeCorrelation.Value.ToString("F2") : "--";
        AttributionSummaryText = attribution.Summary;

        HistoryStatusText = $"Loaded {ChartSamples.Count} CPU points from {_powerSampleCount} power and {_hardwareSampleCount} sensor samples; 24h raw + 7d aggregate history";
    }

    private (DateTime FromUtc, DateTime ToUtc) ClampRange(DateTime fromUtc, DateTime toUtc)
    {
        if (_historyFromUtc == DateTime.MinValue || _historyToUtc <= _historyFromUtc)
            return (fromUtc, toUtc);

        var from = fromUtc < _historyFromUtc ? _historyFromUtc : fromUtc;
        var to = toUtc > _historyToUtc ? _historyToUtc : toUtc;
        if (to <= from)
            return (_historyFromUtc, _historyToUtc);

        return (from, to);
    }

    private (DateTime FromUtc, DateTime ToUtc) GetSelectedOrDefaultRange()
    {
        if (_selectedFromUtc != DateTime.MinValue && _selectedToUtc > _selectedFromUtc)
            return (_selectedFromUtc, _selectedToUtc);

        var toUtc = DateTime.UtcNow;
        return (toUtc - DefaultHistoryWindow, toUtc);
    }

    private static double AxisMax(IEnumerable<double?> values, double fallback)
    {
        var max = values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        if (max <= 0)
            return fallback;

        return Math.Max(fallback, Math.Ceiling(max * 1.15));
    }

    private static string FormatClockRange(double? avgMhz, double? maxMhz)
    {
        if (!avgMhz.HasValue && !maxMhz.HasValue)
            return "--";

        var avg = avgMhz.HasValue ? $"Avg {avgMhz.Value / 1000.0:F2} GHz" : "Avg --";
        var max = maxMhz.HasValue ? $"Max {maxMhz.Value / 1000.0:F2}" : "Max --";
        return $"{avg} / {max}";
    }

    private static string FormatRangePercent(double? avg, double? max)
    {
        if (!avg.HasValue && !max.HasValue)
            return "--";
        return $"Avg {(avg.HasValue ? avg.Value.ToString("F1") : "--")}% / Max {(max.HasValue ? max.Value.ToString("F1") : "--")}%";
    }

    private static string FormatRangeWatts(double? avg, double? max)
    {
        if (!avg.HasValue && !max.HasValue)
            return "--";
        return $"Avg {(avg.HasValue ? avg.Value.ToString("F1") : "--")} W / Max {(max.HasValue ? max.Value.ToString("F1") : "--")} W";
    }

    private static string FormatRange(DateTime fromUtc, DateTime toUtc)
        => $"{fromUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {toUtc.ToLocalTime():HH:mm}";

    private static string FormatRangeWithDuration(DateTime fromUtc, DateTime toUtc)
    {
        var minutes = Math.Max(0, (toUtc - fromUtc).TotalMinutes);
        return $"{FormatRange(fromUtc, toUtc)} ({minutes:F0} min)";
    }
}
