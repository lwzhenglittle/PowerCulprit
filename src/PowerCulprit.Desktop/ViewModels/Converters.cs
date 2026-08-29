using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace PowerCulprit.Desktop.ViewModels;

/// <summary>
/// Converts true→false and false→true. Null/nullable/reference types → false.
/// </summary>
public class BoolToInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b) return !b;
        return false;
    }
}

/// <summary>
/// Converts a Boolean value to WinUI Visibility. Pass "Invert" as the
/// converter parameter when the visible state should represent false.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is bool b && b;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps a SourceStatus.Status string to a SolidColorBrush. Runtime Binding does
/// not auto-convert "Green" → Brush like XAML markup does, so we do it here.
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            "Available" => GetThemeBrush("SystemFillColorSuccessBrush", Colors.LimeGreen),
            "Partial" => GetThemeBrush("SystemFillColorCautionBrush", Colors.Orange),
            "Unavailable" => GetThemeBrush("SystemFillColorCriticalBrush", Colors.IndianRed),
            "Requires admin" => GetThemeBrush("SystemFillColorCautionBrush", Colors.DarkOrange),
            "Disabled" => GetThemeBrush("SystemFillColorNeutralBrush", Colors.Gray),
            _ => GetThemeBrush("SystemFillColorNeutralBrush", Colors.Gray)
        };
    }

    private static SolidColorBrush GetThemeBrush(string resourceKey, Windows.UI.Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true &&
            value is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visible when the value is a non-null, non-empty string; otherwise Collapsed.
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
