using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerCulprit.Desktop.ViewModels;

/// <summary>
/// One row in the process table. Holds raw numeric values (for sort / diff)
/// and exposes derived display strings via partial OnXxxChanged hooks so the
/// table can update in place rather than via Clear/Add.
/// </summary>
public partial class ProcessRow : ObservableObject
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";

    // ── Numeric / raw values used for sorting and diffing ────

    [ObservableProperty]
    public partial double? CpuPercentValue { get; set; }

    [ObservableProperty]
    public partial double? GpuPercentValue { get; set; }

    [ObservableProperty]
    public partial double? MemoryMbValue { get; set; }

    [ObservableProperty]
    public partial double? DiskReadValue { get; set; }

    [ObservableProperty]
    public partial double? DiskWriteValue { get; set; }

    [ObservableProperty]
    public partial double? ScoreValue { get; set; }

    [ObservableProperty]
    public partial bool IsForegroundValue { get; set; }

    [ObservableProperty]
    public partial string ReasonText { get; set; } = "";

    // ── Derived display strings (kept in sync via partial hooks) ─

    [ObservableProperty]
    public partial string CpuPercentText { get; set; } = "--";

    [ObservableProperty]
    public partial string GpuPercentText { get; set; } = "--";

    [ObservableProperty]
    public partial string MemoryMbText { get; set; } = "--";

    [ObservableProperty]
    public partial string DiskReadText { get; set; } = "--";

    [ObservableProperty]
    public partial string DiskWriteText { get; set; } = "--";

    [ObservableProperty]
    public partial string ScoreText { get; set; } = "--";

    [ObservableProperty]
    public partial string IsForegroundText { get; set; } = "";

    partial void OnCpuPercentValueChanged(double? value)
        => CpuPercentText = value.HasValue ? $"{value.Value:F1}" : "--";

    partial void OnGpuPercentValueChanged(double? value)
        => GpuPercentText = value.HasValue ? $"{value.Value:F1}" : "--";

    partial void OnMemoryMbValueChanged(double? value)
        => MemoryMbText = value.HasValue ? $"{value.Value:F0}" : "--";

    partial void OnDiskReadValueChanged(double? value)
        => DiskReadText = value.HasValue ? FormatBytesPerSec(value.Value) : "--";

    partial void OnDiskWriteValueChanged(double? value)
        => DiskWriteText = value.HasValue ? FormatBytesPerSec(value.Value) : "--";

    partial void OnScoreValueChanged(double? value)
        => ScoreText = value.HasValue ? $"{value.Value:F1}" : "--";

    partial void OnIsForegroundValueChanged(bool value)
        => IsForegroundText = value ? "✓" : "";

    private static string FormatBytesPerSec(double bytesPerSec)
    {
        if (bytesPerSec >= 1_000_000)
            return $"{bytesPerSec / 1_000_000:F1} MB/s";
        if (bytesPerSec >= 1_000)
            return $"{bytesPerSec / 1_000:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}
