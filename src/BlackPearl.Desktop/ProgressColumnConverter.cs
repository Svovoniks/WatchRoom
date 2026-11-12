using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlackPearl.Desktop;

// Splits a row between watched and unwatched: a progress of 0 to 100 becomes the watched column's star width, or with the
// parameter "rest" the unwatched column's. The pebble button lights the watched column. Bound to a Visibility, it hides
// the light when nothing has been watched, so its soft edge doesn't show as a glow at the button's start.
public sealed class ProgressColumnConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : 0;
        if (targetType == typeof(Visibility)) return progress > 0 ? Visibility.Visible : Visibility.Collapsed;
        return new GridLength(parameter as string == "rest" ? 100 - progress : progress, GridUnitType.Star);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
