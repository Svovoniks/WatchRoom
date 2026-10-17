using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Watchroom.Desktop;

// The window renders in software, where a live BlurEffect re-runs on every hover or
// scroll repaint beneath it. Blur a small copy of the artwork once instead; the
// low-frequency result scales up smoothly and draws as a plain image.
public sealed class DetailBackdropConverter : IValueConverter
{
    private const int BackdropWidth = 160;
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> cache = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BitmapSource source || source.PixelWidth == 0 || source.PixelHeight == 0) return null;
        return cache.GetValue(source, Blur);
    }
    private static BitmapSource Blur(BitmapSource source)
    {
        var height = Math.Max(1, (int)Math.Round(BackdropWidth * source.PixelHeight / (double)source.PixelWidth));
        // Radius 90 at display size is roughly radius 11 at this scale.
        // A host element ensures the image's own opacity and effect are rendered.
        var host = new Border { Child = new Image { Source = source, Stretch = Stretch.Fill, Opacity = 0.32,
            Effect = new BlurEffect { Radius = 11, RenderingBias = RenderingBias.Quality } } };
        host.Measure(new(BackdropWidth, height)); host.Arrange(new(0, 0, BackdropWidth, height));
        var bitmap = new RenderTargetBitmap(BackdropWidth, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host); bitmap.Freeze();
        return bitmap;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
