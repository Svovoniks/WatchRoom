using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace BlackPearl.Desktop;

// How high a stone stands off the page, which sets the softness, drop and darkness of its shadow.
public enum StoneDepth { None, Disc, Chip, Pebble, Pressed, Poster, Lifted, Slab, Popup, Dialog }

// The app's one material (see the pebble in App.xaml): a Border that stands off the page. Below itself it draws a soft
// shadow, and along its top edge a thin highlight that fades down the sides, so its edge comes from light and shadow
// rather than an outline. A stone with no fill (Transparent or null Background) draws neither.
// The window can render in software, where an effect is recomputed on every repaint, so the shadow is never an effect:
// it is blurred once into a bitmap and drawn in nine slices, so one bitmap serves a stone of any width or height.
public class Stone : Border
{
    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(nameof(Depth), typeof(StoneDepth), typeof(Stone),
        new FrameworkPropertyMetadata(StoneDepth.Pebble, FrameworkPropertyMetadataOptions.AffectsRender));
    public StoneDepth Depth { get => (StoneDepth)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

    public static readonly DependencyProperty SheenProperty = DependencyProperty.Register(nameof(Sheen), typeof(bool), typeof(Stone),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Whether the highlight along the top edge is drawn.</summary>
    public bool Sheen { get => (bool)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }

    public static readonly DependencyProperty AlwaysDarkProperty = DependencyProperty.Register(nameof(AlwaysDark), typeof(bool), typeof(Stone),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>A stone over video or artwork, which stay dark in either mode: the player's menus and the badges on posters.</summary>
    public bool AlwaysDark { get => (bool)GetValue(AlwaysDarkProperty); set => SetValue(AlwaysDarkProperty, value); }

    // Light mode: a lighter shadow and a brighter top highlight. ApplyTheme sets it on each window; the value is inherited.
    public static readonly DependencyProperty LightProperty = DependencyProperty.RegisterAttached("Light", typeof(bool), typeof(Stone),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static bool GetLight(DependencyObject element) => (bool)element.GetValue(LightProperty);
    public static void SetLight(DependencyObject element, bool value) => element.SetValue(LightProperty, value);

    // The theme's mode, for stones whose window has not been told (popups, tooltips and dialogs opened before the switch).
    public static bool LightTheme { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize; var filled = Background is not null and not SolidColorBrush { Color.A: 0 };
        _ = GetLight(this); var light = LightTheme && !AlwaysDark; // the inherited value only makes a theme switch redraw the stone
        var radius = Math.Max(0, Math.Min(CornerRadius.TopLeft, Math.Min(size.Width, size.Height) / 2));
        if (filled && Depth != StoneDepth.None && size.Width >= 1 && size.Height >= 1)
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            StoneShadow.Draw(dc, size, radius, Depth, scale, light);
        }
        base.OnRender(dc);
        if (filled && Sheen && size.Height >= 4) dc.DrawRoundedRectangle(null, SheenPen(radius, light), new Rect(0.5, 0.5, Math.Max(0, size.Width - 1), size.Height - 1), Math.Max(0, radius - 0.5), Math.Max(0, radius - 0.5));
    }

    // A stone draws its shadow outside its bounds, so a clip to its layout slot would cut the shadow off. At fractional
    // display scalings layout rounding can leave an element a fraction of a pixel larger than its slot, and WPF then clips it
    // (a pebble at 175% was clipped to 77.5 of its 78 DIP, a hard edge through its shadow). Only real overflow is clipped.
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : Loose(base.GetLayoutClip(layoutSlotSize), RenderSize);
    internal static Geometry? Loose(Geometry? clip, Size size) =>
        clip is not null && clip.Bounds.Width >= size.Width - 1 && clip.Bounds.Height >= size.Height - 1 ? null : clip;

    private static readonly Dictionary<(int, bool), Pen> sheens = [];
    // A one-pixel line in the highlight colour at the top, fading out where the corners turn down into the sides.
    private static Pen SheenPen(double radius, bool light)
    {
        var key = ((int)Math.Round(radius), light);
        if (sheens.TryGetValue(key, out var pen)) return pen;
        var edge = light ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);
        var reach = Math.Max(3, key.Item1 * 0.8);
        var brush = new LinearGradientBrush(edge, Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0, reach)) { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze(); pen = new Pen(brush, 1); pen.Freeze();
        return sheens[key] = pen;
    }
}

// The root of a template that holds a stone (the pebble's): like Stone, it is not clipped to its layout slot over a rounding
// fraction, so the stone's shadow is not cut off at fractional display scalings.
public class StoneHost : Grid
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : Stone.Loose(base.GetLayoutClip(layoutSlotSize), RenderSize);
}

// Shadow bitmaps for stones, blurred once per shape and kept. A shape wider (or taller) than its corners plus the blur
// is drawn from a short source whose middle is stretched, so the cache holds a handful of bitmaps per depth.
public static class StoneShadow
{
    private readonly record struct Spec(double Blur, double Drop, double Opacity);
    private static Spec SpecOf(StoneDepth depth) => depth switch
    {
        StoneDepth.Disc => new(5, 1.5, 0.35),
        StoneDepth.Chip => new(10, 3, 0.26),
        StoneDepth.Pressed => new(6, 1, 0.3),
        StoneDepth.Poster => new(16, 7, 0.4),
        StoneDepth.Lifted => new(24, 12, 0.46),
        StoneDepth.Slab => new(30, 10, 0.3),
        StoneDepth.Popup => new(16, 5, 0.42),
        StoneDepth.Dialog => new(40, 16, 0.5),
        _ => new(16, 4, 0.28), // the pebble's own shadow, as its template drew it with an effect
    };

    private sealed record Slices(int Pad, int Left, int Right, int Top, int Bottom, int Width, int Height, BitmapSource?[] Parts, Int32Rect[] Sources);
    private static readonly Dictionary<(StoneDepth, bool, int, int, int, int), Slices> cache = [];

    // In light mode the shadow is half as dark. That is baked into its bitmap: an opacity pushed while drawing would make
    // software rendering draw every stone through a layer of its own, which more than tripled a library frame's cost.
    public static void Draw(DrawingContext dc, Size size, double radius, StoneDepth depth, double scale, bool light = false)
    {
        var spec = SpecOf(depth);
        if (light) spec = spec with { Opacity = spec.Opacity * 0.5 };
        // All in device pixels, so slices meet on whole pixels with no seam.
        int px(double dip) => (int)Math.Ceiling(dip * scale - 0.001);
        int w = Math.Max(1, (int)Math.Round(size.Width * scale)), h = Math.Max(1, (int)Math.Round(size.Height * scale)), r = (int)Math.Round(radius * scale);
        var corner = px(radius + spec.Blur + spec.Drop + 2); // reach of a corner's influence into the shape
        bool stretchX = w >= 2 * corner + 2, stretchY = h >= 2 * corner + 2;
        int sw = stretchX ? 2 * corner + 2 : w, sh = stretchY ? 2 * corner + 2 : h;
        var key = (depth, light, sw, sh, r, (int)Math.Round(scale * 100));
        if (!cache.TryGetValue(key, out var slices))
        {
            if (cache.Count > 400) cache.Clear();
            slices = cache[key] = Build(spec, sw, sh, r, scale, stretchX ? corner : sw, stretchY ? corner : sh);
        }
        var pad = slices.Pad;
        // A part maps onto the stone pixel for pixel, shifted to the far side for the last column or row; the middle column
        // or row stretches across whatever the stone's size leaves between them. All in pixels from the stone's origin.
        (int, int) Span(int start, int length, int index, bool stretch, int near, int far, int whole, int size) =>
            !stretch || index == 0 ? (start - pad, start + length - pad)
            : index == 2 ? (start - pad + size - whole, start + length - pad + size - whole)
            : (near - pad, size - (whole + 2 * pad - far) + pad);
        // Stretched parts repeat one column or row of identical pixels, so the nearest pixel is exact and much cheaper to draw
        // than a filtered scale. Set on a group, not the stone, whose own content (artwork) must keep smooth scaling.
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var context = group.Open())
        for (var index = 0; index < 9; index++)
        {
            if (slices.Parts[index] is not { } part) continue;
            var source = slices.Sources[index];
            var (x0, x1) = Span(source.X, source.Width, index % 3, stretchX, slices.Left, slices.Right, sw, w);
            var (y0, y1) = Span(source.Y, source.Height, index / 3, stretchY, slices.Top, slices.Bottom, sh, h);
            context.DrawImage(part, new Rect(x0 / scale, y0 / scale, (x1 - x0) / scale, (y1 - y0) / scale));
        }
        group.Freeze();
        dc.DrawDrawing(group);
    }

    // Blurs a black rounded shape sw by sh pixels, takes the shape itself back out (a see-through stone shows no shadow
    // inside it), and cuts the result at the given corner reach into up to nine parts.
    private static Slices Build(Spec spec, int sw, int sh, int r, double scale, int cornerX, int cornerY)
    {
        var pad = (int)Math.Ceiling((spec.Blur + spec.Drop + 2) * scale);
        int width = sw + 2 * pad, height = sh + 2 * pad;
        double dip(int pixels) => pixels / scale;
        var host = new Grid { Width = dip(width), Height = dip(height) };
        host.Children.Add(new Border
        {
            Width = dip(sw), Height = dip(sh), CornerRadius = new(dip(r)), Background = Brushes.Black, Opacity = spec.Opacity,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(dip(pad), dip(pad) + spec.Drop, 0, 0),
            Effect = new BlurEffect { Radius = spec.Blur, RenderingBias = RenderingBias.Quality }
        });
        var blurred = Render(host, width, height, scale);
        // Leave a pixel of shadow under the shape's edge, so rounding never shows a light rim between shadow and stone.
        var hole = new RectangleGeometry(new Rect(dip(pad + 1), dip(pad + 1), dip(Math.Max(0, sw - 2)), dip(Math.Max(0, sh - 2))), Math.Max(0, dip(r - 1)), Math.Max(0, dip(r - 1)));
        var cut = new Image { Source = blurred, Width = dip(width), Height = dip(height), Stretch = Stretch.Fill,
            Clip = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, dip(width), dip(height))), hole) };
        var shadow = Render(cut, width, height, scale);
        int left = pad + cornerX, right = pad + sw - cornerX, top = pad + cornerY, bottom = pad + sh - cornerY;
        bool splitX = cornerX < sw, splitY = cornerY < sh;
        int[] xs = splitX ? [0, left, right, width] : [0, width];
        int[] ys = splitY ? [0, top, bottom, height] : [0, height];
        var parts = new BitmapSource?[9]; var sources = new Int32Rect[9];
        for (var row = 0; row < ys.Length - 1; row++)
            for (var col = 0; col < xs.Length - 1; col++)
            {
                var rect = new Int32Rect(xs[col], ys[row], xs[col + 1] - xs[col], ys[row + 1] - ys[row]);
                // Under the stone its shadow was taken out, so the middle of a stone cut nine ways is left out, and each edge
                // between the corners keeps only a strip just inside the edge, rather than blending clear pixels.
                if (splitX && splitY)
                {
                    if (row == 1 && col == 1) continue;
                    if (col == 1 && row == 0) rect.Height = Math.Min(rect.Height, pad + 2);
                    if (col == 1 && row == 2) { var keep = Math.Min(rect.Height, pad + 2); rect.Y += rect.Height - keep; rect.Height = keep; }
                    if (row == 1 && col == 0) rect.Width = Math.Min(rect.Width, pad + 2);
                    if (row == 1 && col == 2) { var keep = Math.Min(rect.Width, pad + 2); rect.X += rect.Width - keep; rect.Width = keep; }
                }
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                // Copied out rather than cropped: a CroppedBitmap can be cut again from its source each time it is drawn.
                var pixels = new byte[rect.Width * rect.Height * 4];
                shadow.CopyPixels(rect, pixels, rect.Width * 4, 0);
                var part = BitmapSource.Create(rect.Width, rect.Height, shadow.DpiX, shadow.DpiY, PixelFormats.Pbgra32, null, pixels, rect.Width * 4);
                part.Freeze(); parts[row * 3 + col] = part; sources[row * 3 + col] = rect;
            }
        return new Slices(pad, left, right, top, bottom, width, height, parts, sources);
    }

    private static BitmapSource Render(FrameworkElement element, int width, int height, double scale)
    {
        var size = new Size(width / scale, height / scale);
        element.Measure(size); element.Arrange(new Rect(size));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element); bitmap.Freeze();
        return bitmap;
    }
}
