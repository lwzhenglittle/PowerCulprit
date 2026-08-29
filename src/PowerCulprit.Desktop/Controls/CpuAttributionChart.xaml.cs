using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerCulprit.Core.Analysis;
using PowerCulprit.Core.Models;
using Windows.Foundation;
using Windows.UI;

namespace PowerCulprit.Desktop.Controls;

public sealed partial class CpuAttributionChart : UserControl
{
    private const float PlotLeft = 54;
    private const float PlotTop = 18;
    private const float PlotRight = 80;
    private const float PlotBottom = 34;
    private const float BandGap = 12;
    private const double MinVisibleSeconds = 60;
    private const int MinimumPointBudget = 128;
    private const int MaximumPointBudget = 2048;
    private const double PointsPerDip = 1.25;
    private const int SmoothingPointThreshold = 32;
    private static readonly long PanFrameTicks = Math.Max(1, Stopwatch.Frequency / 30);

    private static readonly CanvasTextFormat AxisTextFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat TooltipTextFormat = new() { FontSize = 12 };

    private bool _isDragging;
    private bool _isPointerOver;
    private bool _invalidateQueued;
    private Point _dragStartPosition;
    private DateTime _dragStartFromUtc;
    private DateTime _dragStartToUtc;
    private DateTime? _previewFromUtc;
    private DateTime? _previewToUtc;
    private Point? _hoverPosition;
    private int _lastHoverPixel = -1;
    private long _lastPanInvalidateTimestamp;
    private IReadOnlyList<CpuTimelineSample>? _cachedSamples;
    private CanvasCachedGeometry? _cachedBatteryGeometry;
    private CanvasCachedGeometry? _cachedEnergyGeometry;
    private CanvasCachedGeometry? _cachedPowerGeometry;
    private CanvasCachedGeometry? _cachedLoadGeometry;
    private CanvasCachedGeometry? _cachedClockGeometry;
    private DateTime _cachedFromUtc;
    private DateTime _cachedToUtc;
    private float _cachedWidth;
    private float _cachedHeight;
    private double _cachedCpuPowerAxisMax;
    private double _cachedCpuClockAxisMax;
    private double _cachedEnergyAxisMax;
    private ElementTheme _cachedTheme;
    private bool _geometryCacheValid;

    public CpuAttributionChart()
    {
        InitializeComponent();
    }

    public event EventHandler<VisibleRangeChangedEventArgs>? VisibleRangeChanged;

    public void ResetZoom()
        => SetVisibleRangeFromInteraction(HistoryFromUtc, HistoryToUtc);

    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples),
            typeof(IReadOnlyList<CpuTimelineSample>),
            typeof(CpuAttributionChart),
            new PropertyMetadata(Array.Empty<CpuTimelineSample>(), OnChartPropertyChanged));

    public IReadOnlyList<CpuTimelineSample> Samples
    {
        get => (IReadOnlyList<CpuTimelineSample>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public static readonly DependencyProperty HistoryFromUtcProperty =
        DependencyProperty.Register(nameof(HistoryFromUtc), typeof(DateTime), typeof(CpuAttributionChart), new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime HistoryFromUtc
    {
        get => (DateTime)GetValue(HistoryFromUtcProperty);
        set => SetValue(HistoryFromUtcProperty, value);
    }

    public static readonly DependencyProperty HistoryToUtcProperty =
        DependencyProperty.Register(nameof(HistoryToUtc), typeof(DateTime), typeof(CpuAttributionChart), new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime HistoryToUtc
    {
        get => (DateTime)GetValue(HistoryToUtcProperty);
        set => SetValue(HistoryToUtcProperty, value);
    }

    public static readonly DependencyProperty VisibleFromUtcProperty =
        DependencyProperty.Register(nameof(VisibleFromUtc), typeof(DateTime), typeof(CpuAttributionChart), new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime VisibleFromUtc
    {
        get => (DateTime)GetValue(VisibleFromUtcProperty);
        set => SetValue(VisibleFromUtcProperty, value);
    }

    public static readonly DependencyProperty VisibleToUtcProperty =
        DependencyProperty.Register(nameof(VisibleToUtc), typeof(DateTime), typeof(CpuAttributionChart), new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime VisibleToUtc
    {
        get => (DateTime)GetValue(VisibleToUtcProperty);
        set => SetValue(VisibleToUtcProperty, value);
    }

    public static readonly DependencyProperty CpuPowerAxisMaxProperty =
        DependencyProperty.Register(nameof(CpuPowerAxisMax), typeof(double), typeof(CpuAttributionChart), new PropertyMetadata(30.0, OnChartPropertyChanged));

    public double CpuPowerAxisMax
    {
        get => (double)GetValue(CpuPowerAxisMaxProperty);
        set => SetValue(CpuPowerAxisMaxProperty, value);
    }

    public static readonly DependencyProperty CpuClockAxisMaxProperty =
        DependencyProperty.Register(nameof(CpuClockAxisMax), typeof(double), typeof(CpuAttributionChart), new PropertyMetadata(5000.0, OnChartPropertyChanged));

    public double CpuClockAxisMax
    {
        get => (double)GetValue(CpuClockAxisMaxProperty);
        set => SetValue(CpuClockAxisMaxProperty, value);
    }

    public static readonly DependencyProperty EnergyAxisMaxProperty =
        DependencyProperty.Register(nameof(EnergyAxisMax), typeof(double), typeof(CpuAttributionChart), new PropertyMetadata(5.0, OnChartPropertyChanged));

    public double EnergyAxisMax
    {
        get => (double)GetValue(EnergyAxisMaxProperty);
        set => SetValue(EnergyAxisMaxProperty, value);
    }

    private static void OnChartPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CpuAttributionChart chart)
        {
            if (e.Property == SamplesProperty || e.Property == HistoryFromUtcProperty || e.Property == HistoryToUtcProperty ||
                e.Property == VisibleFromUtcProperty || e.Property == VisibleToUtcProperty)
            {
                chart.ClearPreviewRange();
            }

            chart.InvalidateGeometryCache();

            chart.QueueInvalidate();
        }
    }

    private void QueueInvalidate()
    {
        if (_invalidateQueued)
            return;

        _invalidateQueued = true;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null)
        {
            _invalidateQueued = false;
            ChartCanvas.Invalidate();
            return;
        }

        if (!dispatcher.TryEnqueue(() =>
        {
            _invalidateQueued = false;
            ChartCanvas.Invalidate();
        }))
        {
            _invalidateQueued = false;
        }
    }

    private void ChartCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            width <= PlotLeft + PlotRight || height <= PlotTop + PlotBottom + BandGap * 3)
            return;

        var colors = GetPalette();
        ds.Clear(colors.Background);

        var samples = Samples ?? Array.Empty<CpuTimelineSample>();
        var (fromUtc, toUtc) = GetEffectiveVisibleRange(samples);
        var plot = new Rect(PlotLeft, PlotTop, width - PlotLeft - PlotRight, height - PlotTop - PlotBottom);
        var bands = GetBands(plot);

        foreach (var band in bands)
            DrawFrame(ds, band, colors);

        if (samples.Count == 0 || toUtc <= fromUtc)
        {
            DrawEmptyState(ds, plot, colors);
            return;
        }

        DrawAxes(ds, bands, fromUtc, toUtc, colors);
        DrawLegends(ds, bands, colors);

        EnsureGeometryCache(sender, samples, fromUtc, toUtc, plot, bands);
        if (_cachedBatteryGeometry is not null)
            ds.DrawCachedGeometry(_cachedBatteryGeometry, colors.BatteryLine);
        if (_cachedEnergyGeometry is not null)
            ds.DrawCachedGeometry(_cachedEnergyGeometry, colors.EnergyLine);
        if (_cachedPowerGeometry is not null)
            ds.DrawCachedGeometry(_cachedPowerGeometry, colors.PowerLine);
        if (_cachedLoadGeometry is not null)
            ds.DrawCachedGeometry(_cachedLoadGeometry, colors.LoadLine);
        if (_cachedClockGeometry is not null)
            ds.DrawCachedGeometry(_cachedClockGeometry, colors.ClockLine);

        if (_isPointerOver && _hoverPosition.HasValue)
            DrawTooltip(ds, plot, bands, fromUtc, toUtc, samples, _hoverPosition.Value, colors);
    }

    private void EnsureGeometryCache(
        CanvasControl resourceCreator,
        IReadOnlyList<CpuTimelineSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        Rect plot,
        Rect[] bands)
    {
        var width = (float)plot.Width;
        var height = (float)plot.Height;
        var theme = ActualTheme;
        if (_geometryCacheValid && ReferenceEquals(_cachedSamples, samples) &&
            _cachedFromUtc == fromUtc && _cachedToUtc == toUtc &&
            Math.Abs(_cachedWidth - width) < 0.5f && Math.Abs(_cachedHeight - height) < 0.5f &&
            Math.Abs(_cachedCpuPowerAxisMax - CpuPowerAxisMax) < 0.0001 &&
            Math.Abs(_cachedCpuClockAxisMax - CpuClockAxisMax) < 0.0001 &&
            Math.Abs(_cachedEnergyAxisMax - EnergyAxisMax) < 0.0001 && _cachedTheme == theme)
        {
            return;
        }

        InvalidateGeometryCache();
        _cachedSamples = samples;
        _cachedFromUtc = fromUtc;
        _cachedToUtc = toUtc;
        _cachedWidth = width;
        _cachedHeight = height;
        _cachedCpuPowerAxisMax = CpuPowerAxisMax;
        _cachedCpuClockAxisMax = CpuClockAxisMax;
        _cachedEnergyAxisMax = EnergyAxisMax;
        _cachedTheme = theme;
        var batterySeries = BuildDisplaySeries(samples, fromUtc, toUtc, bands[0], s => s.BatteryPercent, v => MapRangeY(v, 0, 100, bands[0]), TimeSeriesReductionMode.LargestTriangleThreeBuckets);
        var energySeries = BuildDisplaySeries(samples, fromUtc, toUtc, bands[0], s => s.CumulativeEnergyWh, v => MapRangeY(v, 0, Math.Max(1, EnergyAxisMax), bands[0]), TimeSeriesReductionMode.LargestTriangleThreeBuckets);
        var powerSeries = BuildDisplaySeries(samples, fromUtc, toUtc, bands[1], s => s.CpuPackagePowerWatts, v => MapRangeY(v, 0, Math.Max(1, CpuPowerAxisMax), bands[1]), TimeSeriesReductionMode.MinMax);
        var loadSeries = BuildDisplaySeries(samples, fromUtc, toUtc, bands[2], s => s.CpuLoadPercent, v => MapRangeY(v, 0, 100, bands[2]), TimeSeriesReductionMode.MinMax);
        var clockSeries = BuildDisplaySeries(samples, fromUtc, toUtc, bands[3], s => s.CpuAverageClockMhz, v => MapRangeY(v, 0, Math.Max(1000, CpuClockAxisMax), bands[3]), TimeSeriesReductionMode.MinMax);
        _cachedBatteryGeometry = BuildCachedGeometry(resourceCreator, batterySeries.Points, batterySeries.Smooth);
        _cachedEnergyGeometry = BuildCachedGeometry(resourceCreator, energySeries.Points, energySeries.Smooth);
        _cachedPowerGeometry = BuildCachedGeometry(resourceCreator, powerSeries.Points, powerSeries.Smooth);
        _cachedLoadGeometry = BuildCachedGeometry(resourceCreator, loadSeries.Points, loadSeries.Smooth);
        _cachedClockGeometry = BuildCachedGeometry(resourceCreator, clockSeries.Points, clockSeries.Smooth);
        _geometryCacheValid = true;
    }

    private void InvalidateGeometryCache()
    {
        _geometryCacheValid = false;
        _cachedBatteryGeometry?.Dispose();
        _cachedEnergyGeometry?.Dispose();
        _cachedPowerGeometry?.Dispose();
        _cachedLoadGeometry?.Dispose();
        _cachedClockGeometry?.Dispose();
        _cachedBatteryGeometry = null;
        _cachedEnergyGeometry = null;
        _cachedPowerGeometry = null;
        _cachedLoadGeometry = null;
        _cachedClockGeometry = null;
    }

    private static Rect[] GetBands(Rect plot)
    {
        var bandHeight = (plot.Height - BandGap * 3) / 4.0;
        var result = new Rect[4];
        for (var i = 0; i < result.Length; i++)
            result[i] = new Rect(plot.Left, plot.Top + i * (bandHeight + BandGap), plot.Width, bandHeight);
        return result;
    }

    private void DrawFrame(CanvasDrawingSession ds, Rect plot, ChartPalette colors)
    {
        ds.FillRectangle(plot, colors.PlotBackground);
        ds.DrawRectangle(plot, colors.Axis, 1);
    }

    private void DrawEmptyState(CanvasDrawingSession ds, Rect plot, ChartPalette colors)
    {
        ds.DrawText("No CPU chart data", (float)plot.Left + 16, (float)plot.Top + 16, colors.SecondaryText, AxisTextFormat);
    }

    private void DrawAxes(CanvasDrawingSession ds, Rect[] bands, DateTime fromUtc, DateTime toUtc, ChartPalette colors)
    {
        for (var bandIndex = 0; bandIndex < bands.Length; bandIndex++)
        {
            var band = bands[bandIndex];
            for (var i = 0; i <= 2; i++)
            {
                var y = (float)(band.Bottom - band.Height * i / 2.0);
                ds.DrawLine((float)band.Left, y, (float)band.Right, y, colors.Grid, 1);

                var leftLabel = bandIndex switch
                {
                    0 => $"{i * 50}%",
                    2 => $"{i * 50}%",
                    _ => null
                };
                var rightLabel = bandIndex switch
                {
                    0 => $"{EnergyAxisMax * i / 2.0:F1} Wh",
                    1 => $"{CpuPowerAxisMax * i / 2.0:F0} W",
                    3 => $"{CpuClockAxisMax * i / 2.0:F0} MHz",
                    _ => null
                };

                if (leftLabel is not null)
                    ds.DrawText(leftLabel, 6, y - 8, colors.SecondaryText, AxisTextFormat);
                if (rightLabel is not null)
                    ds.DrawText(rightLabel, (float)band.Right + 6, y - 8, colors.SecondaryText, AxisTextFormat);
            }
        }

        var bottom = bands[^1];
        for (var i = 0; i <= 4; i++)
        {
            var x = (float)(bottom.Left + bottom.Width * i / 4.0);
            foreach (var band in bands)
                ds.DrawLine(x, (float)band.Top, x, (float)band.Bottom, colors.Grid, 1);

            var timestamp = fromUtc + TimeSpan.FromTicks((long)((toUtc - fromUtc).Ticks * i / 4.0));
            ds.DrawText(timestamp.ToLocalTime().ToString("HH:mm"), x - 18, (float)bottom.Bottom + 8, colors.SecondaryText, AxisTextFormat);
        }
    }

    private void DrawLegends(CanvasDrawingSession ds, Rect[] bands, ChartPalette colors)
    {
        DrawLegend(ds, bands[0], "Battery % (left) / Energy Wh (right)", colors.BatteryLine, colors);
        DrawLegend(ds, bands[1], $"CPU Package W (0-{CpuPowerAxisMax:F0})", colors.PowerLine, colors);
        DrawLegend(ds, bands[2], "CPU Load %", colors.LoadLine, colors);
        DrawLegend(ds, bands[3], $"CPU Clock MHz (0-{CpuClockAxisMax:F0})", colors.ClockLine, colors);
    }

    private static void DrawLegend(CanvasDrawingSession ds, Rect band, string text, Color color, ChartPalette colors)
    {
        var y = (float)band.Top + 6;
        ds.FillCircle(new Vector2((float)band.Left + 12, y + 6), 4, color);
        ds.DrawText(text, (float)band.Left + 22, y, color, AxisTextFormat);
    }

    private DisplaySeries BuildDisplaySeries(
        IReadOnlyList<CpuTimelineSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        Rect plot,
        Func<CpuTimelineSample, double?> getValue,
        Func<double, float> mapY,
        TimeSeriesReductionMode reductionMode)
    {
        var start = LowerBound(samples, fromUtc);
        var end = UpperBound(samples, toUtc);
        var visible = new List<TimeSeriesPoint>(Math.Max(0, end - start));
        for (var index = start; index < end; index++)
        {
            var sample = samples[index];
            var value = getValue(sample);
            if (value.HasValue && double.IsFinite(value.Value))
                visible.Add(new TimeSeriesPoint(sample.TimestampUtc, value.Value));
        }

        if (visible.Count == 0)
            return new DisplaySeries(Array.Empty<Vector2>(), false);

        var budget = CalculatePointBudget(plot.Width);
        var reduced = TimeSeriesReducer.Reduce(visible, budget, reductionMode);
        var points = new List<Vector2>(reduced.Count);
        foreach (var point in reduced)
            points.Add(new Vector2(MapX(point.TimestampUtc, fromUtc, toUtc, plot), mapY(point.Value)));

        return new DisplaySeries(points, visible.Count >= SmoothingPointThreshold && points.Count >= 4);
    }

    private static int CalculatePointBudget(double plotWidth)
    {
        var width = double.IsFinite(plotWidth) ? Math.Max(1, plotWidth) : 1;
        return (int)Math.Clamp(Math.Ceiling(width * PointsPerDip), MinimumPointBudget, MaximumPointBudget);
    }

    private static CanvasCachedGeometry? BuildCachedGeometry(
        ICanvasResourceCreator resourceCreator,
        IReadOnlyList<Vector2> points,
        bool smooth)
    {
        if (points.Count < 2)
            return null;

        using var builder = new CanvasPathBuilder(resourceCreator);
        builder.BeginFigure(points[0], CanvasFigureFill.Default);
        for (var index = 1; index < points.Count; index++)
        {
            if (!smooth)
            {
                builder.AddLine(points[index].X, points[index].Y);
                continue;
            }

            var p0 = index > 1 ? points[index - 2] : points[index - 1];
            var p1 = points[index - 1];
            var p2 = points[index];
            var p3 = index + 1 < points.Count ? points[index + 1] : p2;
            var control1 = p1 + (p2 - p0) / 8f;
            var control2 = p2 - (p3 - p1) / 8f;
            ClampControlPoint(ref control1, p1, p2);
            ClampControlPoint(ref control2, p1, p2);
            builder.AddCubicBezier(control1, control2, p2);
        }

        builder.EndFigure(CanvasFigureLoop.Open);
        using var geometry = CanvasGeometry.CreatePath(builder);
        return CanvasCachedGeometry.CreateStroke(geometry, 2);
    }

    private static void ClampControlPoint(ref Vector2 control, Vector2 start, Vector2 end)
    {
        control.X = Math.Clamp(control.X, Math.Min(start.X, end.X), Math.Max(start.X, end.X));
        control.Y = Math.Clamp(control.Y, Math.Min(start.Y, end.Y), Math.Max(start.Y, end.Y));
    }

    private void DrawTooltip(CanvasDrawingSession ds, Rect plot, Rect[] bands, DateTime fromUtc, DateTime toUtc, IReadOnlyList<CpuTimelineSample> samples, Point pointer, ChartPalette colors)
    {
        if (pointer.X < plot.Left || pointer.X > plot.Right || pointer.Y < plot.Top || pointer.Y > plot.Bottom)
            return;

        var timestamp = XToUtc(pointer.X, fromUtc, toUtc, plot);
        var nearest = FindNearestSample(samples, fromUtc, toUtc, timestamp);

        if (nearest is null)
            return;

        var x = MapX(nearest.TimestampUtc, fromUtc, toUtc, plot);
        foreach (var band in bands)
            ds.DrawLine(x, (float)band.Top, x, (float)band.Bottom, colors.HoverLine, 1);

        var text = $"{nearest.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nBattery: {FormatOptional(nearest.BatteryPercent, "%")}\nBattery energy: {FormatOptional(nearest.CumulativeEnergyWh, " Wh")}\nCPU package: {FormatOptional(nearest.CpuPackagePowerWatts, " W")}\nCPU load: {FormatOptional(nearest.CpuLoadPercent, "%")}\nClock: {FormatOptional(nearest.CpuAverageClockMhz, " MHz")}";
        var boxX = Math.Min((float)plot.Right - 220, Math.Max((float)plot.Left + 8, x + 10));
        var boxY = (float)plot.Top + 10;
        ds.FillRoundedRectangle(boxX, boxY, 210, 112, 6, 6, colors.TooltipBackground);
        ds.DrawRoundedRectangle(boxX, boxY, 210, 112, 6, 6, colors.Axis, 1);
        ds.DrawText(text, boxX + 8, boxY + 7, colors.PrimaryText, TooltipTextFormat);
    }

    private static CpuTimelineSample? FindNearestSample(
        IReadOnlyList<CpuTimelineSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime targetUtc)
    {
        if (samples.Count == 0)
            return null;

        var left = LowerBound(samples, fromUtc);
        var right = UpperBound(samples, toUtc) - 1;
        if (left > right)
            return null;

        var candidate = LowerBound(samples, targetUtc);
        if (candidate < left)
            return samples[left];
        if (candidate > right)
            return samples[right];
        if (candidate == left)
            return samples[candidate];

        var before = samples[candidate - 1];
        var after = samples[candidate];
        var beforeDelta = Math.Abs((targetUtc - before.TimestampUtc).Ticks);
        var afterDelta = Math.Abs((after.TimestampUtc - targetUtc).Ticks);
        return beforeDelta <= afterDelta ? before : after;
    }

    private static int LowerBound(IReadOnlyList<CpuTimelineSample> samples, DateTime timestampUtc)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (samples[mid].TimestampUtc < timestampUtc)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static int UpperBound(IReadOnlyList<CpuTimelineSample> samples, DateTime timestampUtc)
    {
        var low = 0;
        var high = samples.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (samples[mid].TimestampUtc <= timestampUtc)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static string FormatOptional(double? value, string suffix)
        => value.HasValue ? $"{value.Value:F1}{suffix}" : "--";

    private (DateTime FromUtc, DateTime ToUtc) GetEffectiveVisibleRange(IReadOnlyList<CpuTimelineSample> samples)
    {
        var historyFrom = HistoryFromUtc;
        var historyTo = HistoryToUtc;
        if (historyFrom == DateTime.MinValue || historyTo <= historyFrom)
        {
            if (samples.Count == 0)
                return (DateTime.MinValue, DateTime.MinValue);
            historyFrom = samples.Min(s => s.TimestampUtc);
            historyTo = samples.Max(s => s.TimestampUtc);
        }

        if (_previewFromUtc.HasValue && _previewToUtc.HasValue)
            return ClampRange(_previewFromUtc.Value, _previewToUtc.Value, historyFrom, historyTo);

        var from = VisibleFromUtc == DateTime.MinValue ? historyFrom : VisibleFromUtc;
        var to = VisibleToUtc == DateTime.MinValue ? historyTo : VisibleToUtc;
        return ClampRange(from, to, historyFrom, historyTo);
    }

    private (DateTime FromUtc, DateTime ToUtc) ClampRange(DateTime fromUtc, DateTime toUtc, DateTime historyFromUtc, DateTime historyToUtc)
    {
        var minWindow = TimeSpan.FromSeconds(MinVisibleSeconds);
        if (toUtc <= fromUtc)
            return (historyFromUtc, historyToUtc);

        var window = toUtc - fromUtc;
        if (window < minWindow)
        {
            var center = fromUtc + TimeSpan.FromTicks(window.Ticks / 2);
            fromUtc = center - TimeSpan.FromTicks(minWindow.Ticks / 2);
            toUtc = center + TimeSpan.FromTicks(minWindow.Ticks / 2);
        }

        if (fromUtc < historyFromUtc)
        {
            toUtc += historyFromUtc - fromUtc;
            fromUtc = historyFromUtc;
        }

        if (toUtc > historyToUtc)
        {
            fromUtc -= toUtc - historyToUtc;
            toUtc = historyToUtc;
        }

        if (fromUtc < historyFromUtc)
            fromUtc = historyFromUtc;

        if (toUtc <= fromUtc)
            toUtc = historyToUtc;

        return (fromUtc, toUtc);
    }

    private static float MapX(DateTime timestampUtc, DateTime fromUtc, DateTime toUtc, Rect plot)
    {
        var ratio = (timestampUtc - fromUtc).Ticks / (double)Math.Max(1, (toUtc - fromUtc).Ticks);
        return (float)(plot.Left + plot.Width * ratio);
    }

    private static DateTime XToUtc(double x, DateTime fromUtc, DateTime toUtc, Rect plot)
    {
        var ratio = Math.Clamp((x - plot.Left) / plot.Width, 0, 1);
        return fromUtc + TimeSpan.FromTicks((long)((toUtc - fromUtc).Ticks * ratio));
    }

    private static float MapRangeY(double value, double min, double max, Rect plot)
    {
        var ratio = Math.Clamp((value - min) / Math.Max(1e-9, max - min), 0, 1);
        return (float)(plot.Bottom - plot.Height * ratio);
    }

    private void ChartCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var samples = Samples ?? Array.Empty<CpuTimelineSample>();
        if (samples.Count == 0)
            return;

        var point = e.GetCurrentPoint(ChartCanvas);
        var plot = GetPlotRect();
        var (fromUtc, toUtc) = GetEffectiveVisibleRange(samples);
        var center = XToUtc(point.Position.X, fromUtc, toUtc, plot);
        var scale = point.Properties.MouseWheelDelta > 0 ? 0.8 : 1.25;
        var before = center - fromUtc;
        var after = toUtc - center;
        SetVisibleRangeFromInteraction(
            center - TimeSpan.FromTicks((long)(before.Ticks * scale)),
            center + TimeSpan.FromTicks((long)(after.Ticks * scale)));
        e.Handled = true;
    }

    private void ChartCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var samples = Samples ?? Array.Empty<CpuTimelineSample>();
        if (samples.Count == 0)
            return;

        var point = e.GetCurrentPoint(ChartCanvas);
        var plot = GetPlotRect();
        if (!point.Properties.IsLeftButtonPressed || !IsInsidePlot(point.Position, plot))
            return;

        var (fromUtc, toUtc) = GetEffectiveVisibleRange(samples);
        _isDragging = true;
        _dragStartPosition = point.Position;
        _dragStartFromUtc = fromUtc;
        _dragStartToUtc = toUtc;
        _previewFromUtc = fromUtc;
        _previewToUtc = toUtc;
        _lastPanInvalidateTimestamp = 0;
        ChartCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ChartCanvas);
        var hoverPixel = (int)Math.Round(point.Position.X);
        if (!_isDragging && hoverPixel == _lastHoverPixel)
            return;

        _lastHoverPixel = hoverPixel;
        _hoverPosition = point.Position;

        if (_isDragging)
        {
            if (!point.Properties.IsLeftButtonPressed)
            {
                FinishDrag(commit: true);
                e.Handled = true;
                return;
            }

            PreviewPan(point.Position);
            e.Handled = true;
            return;
        }

        QueueInvalidate();
    }

    private void ChartCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag(commit: true);
        e.Handled = true;
    }

    private void ChartCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SetVisibleRangeFromInteraction(HistoryFromUtc, HistoryToUtc);
        e.Handled = true;
    }

    private void ChartCanvas_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
    }

    private void ChartCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        _hoverPosition = null;
        _lastHoverPixel = -1;
        if (!_isDragging)
            QueueInvalidate();
    }

    private void SetVisibleRangeFromInteraction(DateTime fromUtc, DateTime toUtc)
    {
        if (HistoryFromUtc == DateTime.MinValue || HistoryToUtc <= HistoryFromUtc)
            return;

        var clamped = ClampRange(fromUtc, toUtc, HistoryFromUtc, HistoryToUtc);
        _previewFromUtc = clamped.FromUtc;
        _previewToUtc = clamped.ToUtc;
        VisibleRangeChanged?.Invoke(this, new VisibleRangeChangedEventArgs(clamped.FromUtc, clamped.ToUtc));
        QueueInvalidate();
    }

    private void ChartCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag(commit: false);
    }

    private void ChartCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag(commit: true);
    }

    private void PreviewPan(Point currentPosition)
    {
        if (HistoryFromUtc == DateTime.MinValue || HistoryToUtc <= HistoryFromUtc)
            return;

        var plot = GetPlotRect();
        var dx = currentPosition.X - _dragStartPosition.X;
        var visibleTicks = Math.Max(1, (_dragStartToUtc - _dragStartFromUtc).Ticks);
        var ticksPerPixel = visibleTicks / Math.Max(1, plot.Width);
        var shift = TimeSpan.FromTicks((long)(-dx * ticksPerPixel));
        var clamped = ClampRange(_dragStartFromUtc + shift, _dragStartToUtc + shift, HistoryFromUtc, HistoryToUtc);
        _previewFromUtc = clamped.FromUtc;
        _previewToUtc = clamped.ToUtc;
        var timestamp = Stopwatch.GetTimestamp();
        if (timestamp - _lastPanInvalidateTimestamp < PanFrameTicks)
            return;

        _lastPanInvalidateTimestamp = timestamp;
        QueueInvalidate();
    }

    private void FinishDrag(bool commit)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ChartCanvas.ReleasePointerCaptures();

        if (commit && _previewFromUtc.HasValue && _previewToUtc.HasValue)
            VisibleRangeChanged?.Invoke(this, new VisibleRangeChangedEventArgs(_previewFromUtc.Value, _previewToUtc.Value));

        QueueInvalidate();
    }

    private void ClearPreviewRange()
    {
        if (!_isDragging)
        {
            _previewFromUtc = null;
            _previewToUtc = null;
        }
    }

    private static bool IsInsidePlot(Point point, Rect plot)
        => point.X >= plot.Left && point.X <= plot.Right && point.Y >= plot.Top && point.Y <= plot.Bottom;

    private Rect GetPlotRect()
    {
        var width = double.IsFinite(ChartCanvas.ActualWidth)
            ? Math.Max(PlotLeft + PlotRight + 1, ChartCanvas.ActualWidth)
            : PlotLeft + PlotRight + 1;
        var height = double.IsFinite(ChartCanvas.ActualHeight)
            ? Math.Max(PlotTop + PlotBottom + 1, ChartCanvas.ActualHeight)
            : PlotTop + PlotBottom + 1;
        return new Rect(PlotLeft, PlotTop, width - PlotLeft - PlotRight, height - PlotTop - PlotBottom);
    }

    private void ChartCanvas_Unloaded(object sender, RoutedEventArgs e)
    {
        InvalidateGeometryCache();
        ChartCanvas.RemoveFromVisualTree();
    }

    private void ChartCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        => InvalidateGeometryCache();

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateGeometryCache();
        QueueInvalidate();
    }

    private sealed record DisplaySeries(IReadOnlyList<Vector2> Points, bool Smooth);

    private ChartPalette GetPalette()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        return dark
            ? new ChartPalette(
                Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 30, 30, 30), Color.FromArgb(255, 56, 56, 56), Color.FromArgb(255, 92, 92, 92),
                Color.FromArgb(255, 242, 242, 242), Color.FromArgb(255, 180, 180, 180),
                Color.FromArgb(255, 80, 170, 255), Color.FromArgb(255, 180, 220, 120), Color.FromArgb(255, 255, 128, 82), Color.FromArgb(255, 95, 210, 135), Color.FromArgb(255, 200, 140, 255),
                Color.FromArgb(180, 35, 35, 35), Color.FromArgb(150, 180, 180, 180))
            : new ChartPalette(
                Color.FromArgb(0, 0, 0, 0), Colors.White, Color.FromArgb(255, 232, 232, 232), Color.FromArgb(255, 190, 190, 190),
                Color.FromArgb(255, 32, 32, 32), Color.FromArgb(255, 110, 110, 110),
                Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 80, 130, 0), Color.FromArgb(255, 216, 79, 32), Color.FromArgb(255, 0, 130, 70), Color.FromArgb(255, 120, 70, 190),
                Color.FromArgb(235, 255, 255, 255), Color.FromArgb(130, 60, 60, 60));
    }

    private sealed record ChartPalette(
        Color Background,
        Color PlotBackground,
        Color Grid,
        Color Axis,
        Color PrimaryText,
        Color SecondaryText,
        Color BatteryLine,
        Color EnergyLine,
        Color PowerLine,
        Color LoadLine,
        Color ClockLine,
        Color TooltipBackground,
        Color HoverLine);
}
