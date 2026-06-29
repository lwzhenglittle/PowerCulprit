using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    public CpuAttributionChart()
    {
        InitializeComponent();
    }

    public event EventHandler<VisibleRangeChangedEventArgs>? VisibleRangeChanged;

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
        if (width <= PlotLeft + PlotRight || height <= PlotTop + PlotBottom + BandGap * 3)
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

        DrawPolyline(ds, BuildDisplayPoints(samples, fromUtc, toUtc, bands[0], s => s.BatteryPercent, v => MapRangeY(v, 0, 100, bands[0])), colors.BatteryLine, 2);
        DrawPolyline(ds, BuildDisplayPoints(samples, fromUtc, toUtc, bands[0], s => s.CumulativeEnergyWh, v => MapRangeY(v, 0, Math.Max(1, EnergyAxisMax), bands[0])), colors.EnergyLine, 2);
        DrawPolyline(ds, BuildDisplayPoints(samples, fromUtc, toUtc, bands[1], s => s.CpuPackagePowerWatts, v => MapRangeY(v, 0, Math.Max(1, CpuPowerAxisMax), bands[1])), colors.PowerLine, 2);
        DrawPolyline(ds, BuildDisplayPoints(samples, fromUtc, toUtc, bands[2], s => s.CpuLoadPercent, v => MapRangeY(v, 0, 100, bands[2])), colors.LoadLine, 2);
        DrawPolyline(ds, BuildDisplayPoints(samples, fromUtc, toUtc, bands[3], s => s.CpuAverageClockMhz, v => MapRangeY(v, 0, Math.Max(1000, CpuClockAxisMax), bands[3])), colors.ClockLine, 2);

        if (_isPointerOver && _hoverPosition.HasValue)
            DrawTooltip(ds, plot, bands, fromUtc, toUtc, samples, _hoverPosition.Value, colors);
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
        foreach (var band in bands)
        {
            for (var i = 0; i <= 2; i++)
            {
                var y = (float)(band.Bottom - band.Height * i / 2.0);
                ds.DrawLine((float)band.Left, y, (float)band.Right, y, colors.Grid, 1);
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
        DrawLegend(ds, bands[0], "Battery % / Energy Wh", colors.BatteryLine, colors);
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

    private IReadOnlyList<Vector2> BuildDisplayPoints(
        IReadOnlyList<CpuTimelineSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        Rect plot,
        Func<CpuTimelineSample, double?> getValue,
        Func<double, float> mapY)
    {
        var targetBuckets = Math.Max(1, (int)plot.Width);
        var bucketTicks = Math.Max(1, (toUtc - fromUtc).Ticks / targetBuckets);
        var reduced = new List<(DateTime TimestampUtc, double Value)>(Math.Min(samples.Count, targetBuckets * 4));

        var index = 0;
        while (index < samples.Count && samples[index].TimestampUtc < fromUtc)
            index++;

        while (index < samples.Count && samples[index].TimestampUtc <= toUtc)
        {
            var sample = samples[index];
            var value = getValue(sample);
            if (!value.HasValue)
            {
                index++;
                continue;
            }

            var bucketStartTicks = ((sample.TimestampUtc - fromUtc).Ticks / bucketTicks) * bucketTicks;
            var bucketEnd = fromUtc.AddTicks(bucketStartTicks + bucketTicks);
            var first = (sample.TimestampUtc, value.Value);
            var last = first;
            var min = first;
            var max = first;
            index++;

            while (index < samples.Count && samples[index].TimestampUtc <= toUtc && samples[index].TimestampUtc < bucketEnd)
            {
                sample = samples[index];
                value = getValue(sample);
                index++;
                if (!value.HasValue)
                    continue;

                var point = (sample.TimestampUtc, value.Value);
                last = point;
                if (point.Value < min.Value) min = point;
                if (point.Value > max.Value) max = point;
            }

            AddDistinct(reduced, first);
            AddDistinct(reduced, min);
            AddDistinct(reduced, max);
            AddDistinct(reduced, last);
        }

        if (reduced.Count == 0)
            return Array.Empty<Vector2>();

        var points = new List<Vector2>(reduced.Count);
        foreach (var point in reduced)
            points.Add(new Vector2(MapX(point.TimestampUtc, fromUtc, toUtc, plot), mapY(point.Value)));

        return points;
    }

    private static void AddDistinct(List<(DateTime TimestampUtc, double Value)> points, (DateTime TimestampUtc, double Value) point)
    {
        if (points.Count == 0)
        {
            points.Add(point);
            return;
        }

        var previous = points[^1];
        if (previous.TimestampUtc != point.TimestampUtc || Math.Abs(previous.Value - point.Value) >= 0.0001)
            points.Add(point);
    }

    private static void DrawPolyline(CanvasDrawingSession ds, IReadOnlyList<Vector2> points, Color color, float thickness)
    {
        for (var i = 1; i < points.Count; i++)
            ds.DrawLine(points[i - 1], points[i], color, thickness);
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

        var text = $"{nearest.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nBattery: {FormatOptional(nearest.BatteryPercent, "%")}\nEnergy: {FormatOptional(nearest.CumulativeEnergyWh, " Wh")}\nCPU W: {FormatOptional(nearest.CpuPackagePowerWatts, " W")}\nCPU load: {FormatOptional(nearest.CpuLoadPercent, "%")}\nClock: {FormatOptional(nearest.CpuAverageClockMhz, " MHz")}";
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
        ChartCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ChartCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ChartCanvas);
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
        var width = Math.Max(PlotLeft + PlotRight + 1, ChartCanvas.ActualWidth);
        var height = Math.Max(PlotTop + PlotBottom + 1, ChartCanvas.ActualHeight);
        return new Rect(PlotLeft, PlotTop, width - PlotLeft - PlotRight, height - PlotTop - PlotBottom);
    }

    private void ChartCanvas_Unloaded(object sender, RoutedEventArgs e)
    {
        ChartCanvas.RemoveFromVisualTree();
    }

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
