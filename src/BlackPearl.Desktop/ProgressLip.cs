using System.Windows;
using System.Windows.Media;

namespace BlackPearl.Desktop;

// How far a title has been watched, as light along the bottom edge of its artwork: a glow rising from the edge up to where
// playback stopped and fading out there, over a thin bright thread that marks the point exactly. It takes the place of a
// progress bar, so the artwork itself shows progress the way a pebble button does (App.xaml).
public sealed class ProgressLip : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ProgressLip),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>0 to 100.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(ProgressLip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public ProgressLip() { IsHitTestVisible = false; Height = 40; VerticalAlignment = VerticalAlignment.Bottom; }

    protected override void OnRender(DrawingContext dc)
    {
        var progress = double.IsFinite(Progress) ? Math.Clamp(Progress, 0, 100) : 0;
        if (progress <= 0 || Fill is not SolidColorBrush { Color: var color }) return;
        double width = ActualWidth, height = ActualHeight, reach = width * progress / 100;
        if (reach < 1 || height < 4) return;
        static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        var glow = new LinearGradientBrush([new(Alpha(color, 0), 0), new(Alpha(color, 0x2E), 0.55), new(Alpha(color, 0xA6), 1)], 90);
        glow.Freeze();
        // A finished title is lit to the end; otherwise the light fades out over the last stretch before the point reached.
        var fade = progress >= 100 ? 0 : Math.Min(28, reach);
        if (fade > 0)
        {
            var mask = new LinearGradientBrush(Colors.Black, Color.FromArgb(0, 0, 0, 0), new Point(reach - fade, 0), new Point(reach, 0)) { MappingMode = BrushMappingMode.Absolute };
            mask.Freeze(); dc.PushOpacityMask(mask);
        }
        dc.DrawRectangle(glow, null, new Rect(0, 0, reach, height));
        if (fade > 0) dc.Pop();
        var thread = new SolidColorBrush(Color.FromRgb((byte)(color.R + (255 - color.R) * 0.35), (byte)(color.G + (255 - color.G) * 0.35), (byte)(color.B + (255 - color.B) * 0.35)));
        thread.Freeze();
        dc.DrawRectangle(thread, null, new Rect(0, height - 2, reach, 2));
    }
}
