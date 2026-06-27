using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PowerCulprit.Desktop.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace PowerCulprit.Desktop.Controls;

public sealed partial class PowerHistoryChart : UserControl
{
    private const float PlotLeft = 54;
    private const float PlotTop = 18;
    private const float PlotRight = 64;
    private const float PlotBottom = 34;
    private const double MinVisibleSeconds = 60;

    private static readonly CanvasTextFormat AxisTextFormat = new() { FontSize = 11 };
    private static readonly CanvasTextFormat TooltipTextFormat = new() { FontSize = 12 };

    private bool _isDragging;
    private bool _isPointerOver;
    private Point _dragStartPosition;
    private DateTime _dragStartFromUtc;
    private DateTime _dragStartToUtc;
    private DateTime? _previewFromUtc;
    private DateTime? _previewToUtc;
    private Point? _hoverPosition;

    public PowerHistoryChart()
    {
        InitializeComponent();
    }

    public event EventHandler<VisibleRangeChangedEventArgs>? VisibleRangeChanged;

    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples),
            typeof(IReadOnlyList<PowerChartSample>),
            typeof(PowerHistoryChart),
            new PropertyMetadata(Array.Empty<PowerChartSample>(), OnChartPropertyChanged));

    public IReadOnlyList<PowerChartSample> Samples
    {
        get => (IReadOnlyList<PowerChartSample>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public static readonly DependencyProperty HistoryFromUtcProperty =
        DependencyProperty.Register(
            nameof(HistoryFromUtc),
            typeof(DateTime),
            typeof(PowerHistoryChart),
            new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime HistoryFromUtc
    {
        get => (DateTime)GetValue(HistoryFromUtcProperty);
        set => SetValue(HistoryFromUtcProperty, value);
    }

    public static readonly DependencyProperty HistoryToUtcProperty =
        DependencyProperty.Register(
            nameof(HistoryToUtc),
            typeof(DateTime),
            typeof(PowerHistoryChart),
            new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime HistoryToUtc
    {
        get => (DateTime)GetValue(HistoryToUtcProperty);
        set => SetValue(HistoryToUtcProperty, value);
    }

    public static readonly DependencyProperty VisibleFromUtcProperty =
        DependencyProperty.Register(
            nameof(VisibleFromUtc),
            typeof(DateTime),
            typeof(PowerHistoryChart),
            new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime VisibleFromUtc
    {
        get => (DateTime)GetValue(VisibleFromUtcProperty);
        set => SetValue(VisibleFromUtcProperty, value);
    }

    public static readonly DependencyProperty VisibleToUtcProperty =
        DependencyProperty.Register(
            nameof(VisibleToUtc),
            typeof(DateTime),
            typeof(PowerHistoryChart),
            new PropertyMetadata(DateTime.MinValue, OnChartPropertyChanged));

    public DateTime VisibleToUtc
    {
        get => (DateTime)GetValue(VisibleToUtcProperty);
        set => SetValue(VisibleToUtcProperty, value);
    }

    public static readonly DependencyProperty DischargeAxisMaxProperty =
        DependencyProperty.Register(
            nameof(DischargeAxisMax),
            typeof(double),
            typeof(PowerHistoryChart),
            new PropertyMetadata(10.0, OnChartPropertyChanged));

    public double DischargeAxisMax
    {
        get => (double)GetValue(DischargeAxisMaxProperty);
        set => SetValue(DischargeAxisMaxProperty, value);
    }

    private static void OnChartPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PowerHistoryChart chart)
        {
            if (e.Property == SamplesProperty ||
                e.Property == HistoryFromUtcProperty ||
                e.Property == HistoryToUtcProperty ||
                e.Property == VisibleFromUtcProperty ||
                e.Property == VisibleToUtcProperty)
            {
                chart.ClearPreviewRange();
            }

            chart.ChartCanvas.Invalidate();
        }
    }

    private void ChartCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= PlotLeft + PlotRight || height <= PlotTop + PlotBottom)
            return;

        var colors = GetPalette();
        ds.Clear(colors.Background);

        var plot = new Rect(PlotLeft, PlotTop, width - PlotLeft - PlotRight, height - PlotTop - PlotBottom);
        DrawFrame(ds, plot, colors);

        var samples = Samples ?? Array.Empty<PowerChartSample>();
        var (fromUtc, toUtc) = GetEffectiveVisibleRange(samples);
        if (samples.Count == 0 || toUtc <= fromUtc)
        {
            DrawEmptyState(ds, plot, colors);
            return;
        }

        DrawAxes(ds, plot, fromUtc, toUtc, colors);

        var batteryPoints = BuildDisplayPoints(samples, fromUtc, toUtc, plot, (sample) => sample.BatteryPercent, MapBatteryY);
        var dischargePoints = BuildDisplayPoints(samples, fromUtc, toUtc, plot, (sample) => sample.DischargeWatts, MapDischargeY);

        DrawPolyline(ds, batteryPoints, colors.BatteryLine, 2);
        DrawPolyline(ds, dischargePoints, colors.DischargeLine, 2);

        if (_isPointerOver && _hoverPosition.HasValue)
            DrawTooltip(ds, plot, fromUtc, toUtc, samples, _hoverPosition.Value, colors);
    }

    private void DrawFrame(CanvasDrawingSession ds, Rect plot, ChartPalette colors)
    {
        ds.FillRectangle(plot, colors.PlotBackground);
        ds.DrawRectangle(plot, colors.Axis, 1);
    }

    private void DrawEmptyState(CanvasDrawingSession ds, Rect plot, ChartPalette colors)
    {
        ds.DrawText("No chart data", (float)plot.Left + 16, (float)plot.Top + 16, colors.SecondaryText, AxisTextFormat);
    }

    private void DrawAxes(CanvasDrawingSession ds, Rect plot, DateTime fromUtc, DateTime toUtc, ChartPalette colors)
    {
        for (var i = 0; i <= 4; i++)
        {
            var y = (float)(plot.Bottom - plot.Height * i / 4.0);
            ds.DrawLine((float)plot.Left, y, (float)plot.Right, y, colors.Grid, 1);

            var battery = i * 25;
            ds.DrawText($"{battery}%", 6, y - 8, colors.SecondaryText, AxisTextFormat);

            var watts = DischargeAxisMax * i / 4.0;
            ds.DrawText($"{watts:F0}W", (float)plot.Right + 8, y - 8, colors.SecondaryText, AxisTextFormat);
        }

        for (var i = 0; i <= 4; i++)
        {
            var x = (float)(plot.Left + plot.Width * i / 4.0);
            ds.DrawLine(x, (float)plot.Top, x, (float)plot.Bottom, colors.Grid, 1);

            var timestamp = fromUtc + TimeSpan.FromTicks((long)((toUtc - fromUtc).Ticks * i / 4.0));
            ds.DrawText(timestamp.ToLocalTime().ToString("HH:mm"), x - 18, (float)plot.Bottom + 8, colors.SecondaryText, AxisTextFormat);
        }

        var legendY = (float)plot.Top + 8;
        ds.FillCircle(new Vector2((float)plot.Left + 12, legendY + 6), 4, colors.BatteryLine);
        ds.DrawText("Battery %", (float)plot.Left + 22, legendY, colors.BatteryLine, AxisTextFormat);

        ds.FillCircle(new Vector2((float)plot.Left + 102, legendY + 6), 4, colors.DischargeLine);
        ds.DrawText("Discharge W", (float)plot.Left + 112, legendY, colors.DischargeLine, AxisTextFormat);
    }

    private IReadOnlyList<Vector2> BuildDisplayPoints(
        IReadOnlyList<PowerChartSample> samples,
        DateTime fromUtc,
        DateTime toUtc,
        Rect plot,
        Func<PowerChartSample, double?> getValue,
        Func<double, Rect, float> mapY)
    {
        var visible = samples
            .Where(sample => sample.TimestampUtc >= fromUtc && sample.TimestampUtc <= toUtc)
            .Select(sample => (sample.TimestampUtc, Value: getValue(sample)))
            .Where(point => point.Value.HasValue)
            .Select(point => (point.TimestampUtc, Value: point.Value!.Value))
            .ToList();

        if (visible.Count == 0)
            return Array.Empty<Vector2>();

        var targetBuckets = Math.Max(1, (int)plot.Width);
        var bucketTicks = Math.Max(1, (toUtc - fromUtc).Ticks / targetBuckets);
        var reduced = new List<(DateTime TimestampUtc, double Value)>(targetBuckets * 4);

        var index = 0;
        while (index < visible.Count)
        {
            var bucketStartTicks = ((visible[index].TimestampUtc - fromUtc).Ticks / bucketTicks) * bucketTicks;
            var bucketEnd = fromUtc.AddTicks(bucketStartTicks + bucketTicks);
            var first = visible[index];
            var last = first;
            var min = first;
            var max = first;

            while (index < visible.Count && visible[index].TimestampUtc < bucketEnd)
            {
                var point = visible[index];
                last = point;
                if (point.Value < min.Value) min = point;
                if (point.Value > max.Value) max = point;
                index++;
            }

            AddDistinct(reduced, first);
            AddDistinct(reduced, min);
            AddDistinct(reduced, max);
            AddDistinct(reduced, last);
        }

        return reduced
            .OrderBy(point => point.TimestampUtc)
            .Select(point => new Vector2(MapX(point.TimestampUtc, fromUtc, toUtc, plot), mapY(point.Value, plot)))
            .ToList();
    }

    private static void AddDistinct(List<(DateTime TimestampUtc, double Value)> points, (DateTime TimestampUtc, double Value) point)
    {
        if (!points.Any(existing => existing.TimestampUtc == point.TimestampUtc && Math.Abs(existing.Value - point.Value) < 0.0001))
            points.Add(point);
    }

    private static void DrawPolyline(CanvasDrawingSession ds, IReadOnlyList<Vector2> points, Color color, float thickness)
    {
        for (var i = 1; i < points.Count; i++)
            ds.DrawLine(points[i - 1], points[i], color, thickness);
    }

    private void DrawTooltip(CanvasDrawingSession ds, Rect plot, DateTime fromUtc, DateTime toUtc, IReadOnlyList<PowerChartSample> samples, Point pointer, ChartPalette colors)
    {
        if (pointer.X < plot.Left || pointer.X > plot.Right || pointer.Y < plot.Top || pointer.Y > plot.Bottom)
            return;

        var timestamp = XToUtc(pointer.X, fromUtc, toUtc, plot);
        var nearest = samples
            .Where(sample => sample.TimestampUtc >= fromUtc && sample.TimestampUtc <= toUtc)
            .OrderBy(sample => Math.Abs((sample.TimestampUtc - timestamp).Ticks))
            .FirstOrDefault();

        if (nearest is null)
            return;

        var x = MapX(nearest.TimestampUtc, fromUtc, toUtc, plot);
        ds.DrawLine(x, (float)plot.Top, x, (float)plot.Bottom, colors.HoverLine, 1);

        var text = $"{nearest.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\nBattery: {FormatOptional(nearest.BatteryPercent, "%")}\nDischarge: {FormatOptional(nearest.DischargeWatts, " W")}";
        var boxX = Math.Min((float)plot.Right - 180, Math.Max((float)plot.Left + 8, x + 10));
        var boxY = (float)plot.Top + 10;
        ds.FillRoundedRectangle(boxX, boxY, 170, 64, 6, 6, colors.TooltipBackground);
        ds.DrawRoundedRectangle(boxX, boxY, 170, 64, 6, 6, colors.Axis, 1);
        ds.DrawText(text, boxX + 8, boxY + 7, colors.PrimaryText, TooltipTextFormat);
    }

    private static string FormatOptional(double? value, string suffix)
        => value.HasValue ? $"{value.Value:F1}{suffix}" : "--";

    private (DateTime FromUtc, DateTime ToUtc) GetEffectiveVisibleRange(IReadOnlyList<PowerChartSample> samples)
    {
        var historyFrom = HistoryFromUtc;
        var historyTo = HistoryToUtc;
        if (historyFrom == DateTime.MinValue || historyTo <= historyFrom)
        {
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

    private static float MapBatteryY(double value, Rect plot)
    {
        var ratio = Math.Clamp(value, 0, 100) / 100.0;
        return (float)(plot.Bottom - plot.Height * ratio);
    }

    private float MapDischargeY(double value, Rect plot)
    {
        var max = Math.Max(1, DischargeAxisMax);
        var ratio = Math.Clamp(value, 0, max) / max;
        return (float)(plot.Bottom - plot.Height * ratio);
    }

    private void ChartCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var samples = Samples ?? Array.Empty<PowerChartSample>();
        if (samples.Count == 0)
            return;

        var point = e.GetCurrentPoint(ChartCanvas);
        var plot = GetPlotRect();
        var (fromUtc, toUtc) = GetEffectiveVisibleRange(samples);
        var center = XToUtc(point.Position.X, fromUtc, toUtc, plot);
        var scale = point.Properties.MouseWheelDelta > 0 ? 0.8 : 1.25;
        var before = center - fromUtc;
        var after = toUtc - center;
        var nextFrom = center - TimeSpan.FromTicks((long)(before.Ticks * scale));
        var nextTo = center + TimeSpan.FromTicks((long)(after.Ticks * scale));
        SetVisibleRangeFromInteraction(nextFrom, nextTo);
        e.Handled = true;
    }

    private void ChartCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var samples = Samples ?? Array.Empty<PowerChartSample>();
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

        ChartCanvas.Invalidate();
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
            ChartCanvas.Invalidate();
    }

    private void SetVisibleRangeFromInteraction(DateTime fromUtc, DateTime toUtc)
    {
        if (HistoryFromUtc == DateTime.MinValue || HistoryToUtc <= HistoryFromUtc)
            return;

        var clamped = ClampRange(fromUtc, toUtc, HistoryFromUtc, HistoryToUtc);
        _previewFromUtc = clamped.FromUtc;
        _previewToUtc = clamped.ToUtc;
        VisibleRangeChanged?.Invoke(this, new VisibleRangeChangedEventArgs(clamped.FromUtc, clamped.ToUtc));
        ChartCanvas.Invalidate();
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
        ChartCanvas.Invalidate();
    }

    private void FinishDrag(bool commit)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ChartCanvas.ReleasePointerCaptures();

        if (commit && _previewFromUtc.HasValue && _previewToUtc.HasValue)
            VisibleRangeChanged?.Invoke(this, new VisibleRangeChangedEventArgs(_previewFromUtc.Value, _previewToUtc.Value));

        ChartCanvas.Invalidate();
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
                Color.FromArgb(0, 0, 0, 0),
                Color.FromArgb(255, 30, 30, 30),
                Color.FromArgb(255, 56, 56, 56),
                Color.FromArgb(255, 92, 92, 92),
                Color.FromArgb(255, 242, 242, 242),
                Color.FromArgb(255, 180, 180, 180),
                Color.FromArgb(255, 80, 170, 255),
                Color.FromArgb(255, 255, 128, 82),
                Color.FromArgb(180, 35, 35, 35),
                Color.FromArgb(150, 180, 180, 180))
            : new ChartPalette(
                Color.FromArgb(0, 0, 0, 0),
                Colors.White,
                Color.FromArgb(255, 232, 232, 232),
                Color.FromArgb(255, 190, 190, 190),
                Color.FromArgb(255, 32, 32, 32),
                Color.FromArgb(255, 110, 110, 110),
                Color.FromArgb(255, 0, 103, 192),
                Color.FromArgb(255, 216, 79, 32),
                Color.FromArgb(235, 255, 255, 255),
                Color.FromArgb(130, 80, 80, 80));
    }

    private sealed record ChartPalette(
        Color Background,
        Color PlotBackground,
        Color Grid,
        Color Axis,
        Color PrimaryText,
        Color SecondaryText,
        Color BatteryLine,
        Color DischargeLine,
        Color TooltipBackground,
        Color HoverLine);
}

public sealed class VisibleRangeChangedEventArgs : EventArgs
{
    public VisibleRangeChangedEventArgs(DateTime fromUtc, DateTime toUtc)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
    }

    public DateTime FromUtc { get; }
    public DateTime ToUtc { get; }
}
