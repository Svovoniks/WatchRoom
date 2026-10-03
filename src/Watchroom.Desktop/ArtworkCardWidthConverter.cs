using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Watchroom.Desktop;

public sealed class ArtworkCardWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ImageSource image || !double.IsFinite(image.Width) || !double.IsFinite(image.Height) || image.Width <= 0 || image.Height <= 0)
            return 180d;
        // Aim for the existing poster height, letting landscape stills occupy wider cards.
        // The image determines its own height once the card's width is constrained.
        return Math.Clamp(248 * image.Width / image.Height + 14, 128, 360);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
