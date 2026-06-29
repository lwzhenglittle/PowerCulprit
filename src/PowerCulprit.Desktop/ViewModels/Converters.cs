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
/// Maps a SourceStatus.Status string to a SolidColorBrush. Runtime Binding does
/// not auto-convert "Green" → Brush like XAML markup does, so we do it here.
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AvailableBrush = new(Colors.LimeGreen);
    private static readonly SolidColorBrush PartialBrush = new(Colors.Orange);
    private static readonly SolidColorBrush UnavailableBrush = new(Colors.IndianRed);
    private static readonly SolidColorBrush RequiresAdminBrush = new(Colors.DarkOrange);
    private static readonly SolidColorBrush DefaultBrush = new(Colors.Gray);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value as string) switch
        {
            "Available" => AvailableBrush,
            "Partial" => PartialBrush,
            "Unavailable" => UnavailableBrush,
            "Requires admin" => RequiresAdminBrush,
            "Disabled" => DefaultBrush,
            _ => DefaultBrush
        };
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
