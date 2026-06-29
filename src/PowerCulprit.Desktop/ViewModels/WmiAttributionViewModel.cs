using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PowerCulprit.Core.Models;
using PowerCulprit.Storage;

namespace PowerCulprit.Desktop.ViewModels;

public partial class WmiAttributionViewModel : ObservableObject
{
    private static readonly TimeSpan DefaultHistoryWindow = TimeSpan.FromHours(6);
    private const int DisplayLimit = 100;

    private readonly DatabaseManager _database;
    private readonly ILogger<WmiAttributionViewModel> _logger;
    private CancellationTokenSource? _loadCts;
    private DateTime _selectedFromUtc = DateTime.MinValue;
    private DateTime _selectedToUtc = DateTime.MinValue;

    public WmiAttributionViewModel(
        DatabaseManager database,
        ILogger<WmiAttributionViewModel> logger)
    {
        _database = database;
        _logger = logger;
    }

    [ObservableProperty]
    public partial string TotalCallsText { get; set; } = "--";

    [ObservableProperty]
    public partial string CallerCountText { get; set; } = "--";

    [ObservableProperty]
    public partial string FailureCountText { get; set; } = "--";

    [ObservableProperty]
    public partial string TopCallerText { get; set; } = "--";

    [ObservableProperty]
    public partial string SelectedRangeText { get; set; } = "--";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "No WMI activity loaded";

    [ObservableProperty]
    public partial string ErrorText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial ObservableCollection<WmiCallerRow> CallerRows { get; set; } = new();

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

    private async Task LoadLatestCycleAsync()
    {
        try
        {
            await _database.RebuildBatteryCyclesAsync();
            var cycle = (await _database.GetLatestBatteryDisplayCyclesAsync(1)).FirstOrDefault();
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
            _logger.LogError(ex, "Failed to load latest WMI activity cycle");
            ErrorText = $"WMI activity failed: {ex.Message}";
            StatusText = "WMI activity failed";
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
            StatusText = "Loading WMI activity...";

            var aggregates = await _database.GetWmiCallerAggregatesAsync(fromUtc, toUtc, DisplayLimit);
            token.ThrowIfCancellationRequested();

            ApplyLoadedRange(fromUtc, toUtc, aggregates);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load WMI activity");
            ErrorText = $"WMI activity failed: {ex.Message}";
            StatusText = "WMI activity failed";
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

    private void ApplyLoadedRange(DateTime fromUtc, DateTime toUtc, IReadOnlyList<WmiCallerAggregate> aggregates)
    {
        _selectedFromUtc = fromUtc;
        _selectedToUtc = toUtc;
        SelectedRangeText = FormatRangeWithDuration(fromUtc, toUtc);

        var ordered = aggregates
            .OrderByDescending(a => a.CallCount)
            .ThenByDescending(a => a.FailureCount)
            .ThenBy(a => a.ProcessName ?? $"PID {a.ClientProcessId}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        TotalCallsText = ordered.Sum(a => a.CallCount).ToString();
        CallerCountText = ordered.Count.ToString();
        FailureCountText = ordered.Sum(a => a.FailureCount).ToString();
        TopCallerText = ordered.FirstOrDefault() is { } top
            ? $"{DisplayProcess(top)} ({top.CallCount})"
            : "--";

        CallerRows = new ObservableCollection<WmiCallerRow>(ordered.Select((aggregate, index) => new WmiCallerRow
        {
            Rank = index + 1,
            ProcessText = DisplayProcess(aggregate),
            PidText = aggregate.ClientProcessId.ToString(),
            CallCountText = aggregate.CallCount.ToString(),
            FailureCountText = aggregate.FailureCount.ToString(),
            UniqueOperationCountText = aggregate.UniqueOperationCount.ToString(),
            FirstSeenText = aggregate.FirstSeenUtc.ToLocalTime().ToString("HH:mm:ss"),
            LastSeenText = aggregate.LastSeenUtc.ToLocalTime().ToString("HH:mm:ss"),
            LastOperationText = Shorten(aggregate.LastOperation, 180),
            LastResultText = FormatResult(aggregate.LastResultCode, aggregate.LastPossibleCause)
        }));

        StatusText = $"Loaded {ordered.Sum(a => a.CallCount)} parsed WMI Activity events from {ordered.Count} callers; event-log attribution by ClientProcessId";
    }

    private (DateTime FromUtc, DateTime ToUtc) GetSelectedOrDefaultRange()
    {
        if (_selectedFromUtc != DateTime.MinValue && _selectedToUtc > _selectedFromUtc)
            return (_selectedFromUtc, _selectedToUtc);

        var toUtc = DateTime.UtcNow;
        return (toUtc - DefaultHistoryWindow, toUtc);
    }

    private static string DisplayProcess(WmiCallerAggregate aggregate)
        => string.IsNullOrWhiteSpace(aggregate.ProcessName)
            ? $"Unknown or exited (PID {aggregate.ClientProcessId})"
            : aggregate.ProcessName!;

    private static string FormatResult(string? resultCode, string? possibleCause)
    {
        if (string.IsNullOrWhiteSpace(resultCode) && string.IsNullOrWhiteSpace(possibleCause))
            return "--";

        if (string.IsNullOrWhiteSpace(possibleCause))
            return resultCode ?? "--";

        if (string.IsNullOrWhiteSpace(resultCode))
            return possibleCause;

        return $"{resultCode}: {possibleCause}";
    }

    private static string Shorten(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "--";

        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string FormatRange(DateTime fromUtc, DateTime toUtc)
        => $"{fromUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {toUtc.ToLocalTime():HH:mm}";

    private static string FormatRangeWithDuration(DateTime fromUtc, DateTime toUtc)
    {
        var minutes = Math.Max(0, (toUtc - fromUtc).TotalMinutes);
        return $"{FormatRange(fromUtc, toUtc)} ({minutes:F0} min)";
    }
}
