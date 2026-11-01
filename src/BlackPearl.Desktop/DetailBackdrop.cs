using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace BlackPearl.Desktop;

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

// Home row glow: the blurred artwork with its edges already faded to transparent, baked once
// into one small image so scrolling draws a single bitmap instead of stacked gradient layers.
public sealed class RowBackdropConverter : IValueConverter
{
    // Baked at the size the row draws it, so each scroll frame copies pixels without rescaling them.
    private const int Width = 720, Height = 380;
    private static readonly DetailBackdropConverter blur = new();
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> cache = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        blur.Convert(value, targetType, parameter, culture) is BitmapSource blurred ? cache.GetValue(blurred, Fade) : null;
    private static BitmapSource Fade(BitmapSource source)
    {
        static LinearGradientBrush Mask(double angle, params (double Alpha, double Offset)[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = angle == 0 ? new(1, 0) : new(0, 1) };
            foreach (var (alpha, offset) in stops) brush.GradientStops.Add(new(Color.FromArgb((byte)(alpha * 255), 0, 0, 0), offset));
            brush.Freeze(); return brush;
        }
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Strongest behind the spotlight at the left, fading across the row and at its top and bottom.
            context.PushOpacityMask(Mask(0, (0, 0), (1, 0.14), (0.55, 0.6), (0, 1)));
            context.PushOpacityMask(Mask(90, (0, 0), (1, 0.32), (1, 0.66), (0, 1)));
            context.DrawImage(source, new Rect(0, 0, Width, Height));
            context.Pop(); context.Pop();
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
