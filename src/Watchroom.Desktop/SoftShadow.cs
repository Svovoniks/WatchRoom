using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Watchroom.Desktop;

// The window renders in software, where a DropShadowEffect is recomputed on every repaint,
// including each frame of scrolling. These shadows are blurred once and drawn as images.
// Each bitmap is its shape plus Padding on every side, so place it with a Margin of -Padding.
public static class SoftShadow
{
    public const double Padding = 30;
    public static BitmapSource Card { get; } = Render(128, 190, 11, 24, 6, 0.35);
    public static BitmapSource Disc { get; } = Render(40, 40, 20, 18, 4, 0.4);
    private static BitmapSource Render(double width, double height, double radius, double blur, double depth, double opacity)
    {
        var size = new Size(width + Padding * 2, height + Padding * 2);
        var host = new Grid { Width = size.Width, Height = size.Height };
        host.Children.Add(new Border
        {
            Width = width, Height = height, CornerRadius = new(radius), Background = Brushes.Black, Opacity = opacity,
            Margin = new(Padding, Padding + depth, Padding, Padding - depth),
            Effect = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Quality }
        });
        host.Measure(size); host.Arrange(new(size));
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host); bitmap.Freeze();
        return bitmap;
    }
}
