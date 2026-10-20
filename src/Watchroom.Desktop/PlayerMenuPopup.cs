using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Watchroom.Desktop;

// A player bar menu that grows out of its pill: a pill-sized shape just above the pill expands into
// the card, then the card's content drifts in. Closing reverses it back into the pill. A plain popup
// window closes instantly, so every close request (outside click, Escape, choosing a track) is held
// open by coercion until the motion finishes.
public sealed class PlayerMenuPopup : Popup
{
    private static readonly TimeSpan GrowTime = TimeSpan.FromMilliseconds(340), ShrinkTime = TimeSpan.FromMilliseconds(230), ContentDelay = TimeSpan.FromMilliseconds(110);
    private static readonly IEasingFunction Grow = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut }),
        Settle = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut }), Shrink = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private const double ShadowRoom = 14, CardRadius = 10;
    private readonly TranslateTransform drift = new();
    private RectangleGeometry? shape;
    private int generation;
    private bool fading, releasing, openOnLoad, growPending;

    private static readonly List<PlayerMenuPopup> menus = [];

    static PlayerMenuPopup() => IsOpenProperty.OverrideMetadata(typeof(PlayerMenuPopup), new FrameworkPropertyMetadata(false, null, CoerceIsOpen));

    public PlayerMenuPopup() => menus.Add(this);

    private static object CoerceIsOpen(DependencyObject d, object value)
    {
        var menu = (PlayerMenuPopup)d;
        if ((bool)value)
        {
            // As Popup does, a menu asked to open before it is loaded opens once it is.
            if (!menu.IsLoaded && VisualTreeHelper.GetParent(menu) is not null) { menu.OpenWhenLoaded(); return false; }
            if (!menu.IsOpen) menu.PrepareOpen();
            else if (menu.fading) menu.Reopen();
            return true;
        }
        if (!menu.IsOpen || menu.releasing) return false;
        if (!menu.fading) menu.Dismissing?.Invoke(menu, EventArgs.Empty);
        if (menu.Child is null || !Animated) return false;
        if (!menu.fading) menu.BeginShrink();
        return true;
    }

    // Raised when something asks the menu to close (an outside click, Escape, a chosen track), before it folds away.
    public event EventHandler? Dismissing;

    private static bool Animated => SystemParameters.ClientAreaAnimation;
    private UIElement? Content => (Child as Decorator)?.Child is ContentControl { Content: UIElement content } ? content : null;

    // Closes without the motion, for when the bar itself disappears or moves.
    public void CloseNow()
    {
        if (!IsOpen) return;
        generation++; growPending = false;
        ClearMotion();
        Release();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Menus open over the native video; keep them on the software surface like the video overlay.
        if (Child is { } child) App.UseSoftwareSurface(child);
        // The window and its layout exist now, so the pill's position inside it is known. Start on the
        // next frame: building a large menu can block a frame, and a motion begun before that stall
        // would jump straight to its end.
        if (!growPending) return;
        growPending = false;
        var current = generation;
        OnNextFrame(() => { if (current == generation && IsOpen && !fading) BeginGrow(); });
    }

    internal static void OnNextFrame(Action action)
    {
        void Frame(object? sender, EventArgs e) { CompositionTarget.Rendering -= Frame; action(); }
        CompositionTarget.Rendering += Frame;
    }

    private void OpenWhenLoaded()
    {
        if (openOnLoad) return;
        openOnLoad = true;
        void Opened(object sender, RoutedEventArgs e) { Loaded -= Opened; openOnLoad = false; CoerceValue(IsOpenProperty); }
        Loaded += Opened;
    }

    private void PrepareOpen()
    {
        // One player menu at a time: another that is still fully open folds away as this one opens.
        foreach (var other in menus)
            if (other != this && other.IsOpen && !other.fading) other.SetCurrentValue(IsOpenProperty, false);
        generation++; fading = false;
        ClearMotion();
        if (!Animated || Child is null) return;
        Child.Opacity = 0; // Hidden until the first frame can start at the pill.
        growPending = true;
    }

    private void BeginGrow()
    {
        if (Child is not FrameworkElement host) return;
        host.UpdateLayout();
        var (pill, full) = Shapes(host);
        shape = new RectangleGeometry(pill, pill.Height / 2, pill.Height / 2);
        host.Clip = shape; host.Opacity = 1;
        var current = generation;
        var grow = new RectAnimation(pill, full, GrowTime) { EasingFunction = Grow };
        grow.Completed += (_, _) => { if (current == generation && !fading) host.Clip = null; };
        AnimateShape(grow, CardRadius + ShadowRoom, GrowTime, Grow);
        if (Content is { } content)
        {
            // The content waits for the shape to open up, then drifts in.
            content.RenderTransform = drift;
            content.BeginAnimation(OpacityProperty, After(ContentDelay, 0, 1, TimeSpan.FromMilliseconds(200)));
            drift.BeginAnimation(TranslateTransform.YProperty, After(ContentDelay, 10, 0, TimeSpan.FromMilliseconds(260)));
        }
    }

    // Holds the start value through the delay; a plain BeginTime would show the base value meanwhile.
    private static DoubleAnimationUsingKeyFrames After(TimeSpan delay, double from, double to, TimeSpan length) => new()
    {
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)),
            new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + length), Settle),
        },
    };

    private void Reopen()
    {
        generation++; fading = false;
        SetCurrentValue(StaysOpenProperty, false); // Take the mouse back so an outside click closes it again.
        if (Child is not FrameworkElement host) return;
        var (_, full) = Shapes(host);
        var current = generation;
        var grow = new RectAnimation(full, GrowTime) { EasingFunction = Grow };
        grow.Completed += (_, _) => { if (current == generation && !fading) host.Clip = null; };
        AnimateShape(grow, CardRadius + ShadowRoom, GrowTime, Grow);
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        Content?.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Settle });
        drift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = Settle });
    }

    private void BeginShrink()
    {
        var current = ++generation; fading = true; growPending = false;
        // Let go of the mouse while folding away. A popup only takes the mouse when nothing else holds it,
        // so a menu opened during this fade would otherwise open without it and never close on an
        // outside click.
        SetCurrentValue(StaysOpenProperty, true);
        if (Child is not FrameworkElement host) { Release(); return; }
        var (pill, full) = Shapes(host);
        if (shape is null || host.Clip != shape) { shape = new RectangleGeometry(full, CardRadius + ShadowRoom, CardRadius + ShadowRoom); host.Clip = shape; }
        AnimateShape(new RectAnimation(pill, ShrinkTime) { EasingFunction = Shrink }, pill.Height / 2, ShrinkTime, Shrink);
        // The content leaves first so the shape folds back into the pill empty.
        Content?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)));
        var vanish = new DoubleAnimation(0, TimeSpan.FromMilliseconds(60)) { BeginTime = ShrinkTime - TimeSpan.FromMilliseconds(60) };
        vanish.Completed += (_, _) => { if (current == generation) Release(); };
        host.BeginAnimation(OpacityProperty, vanish);
    }

    // The pill's outline as it would sit at the card's bottom-left corner, and the card plus its shadow.
    private (Rect Pill, Rect Full) Shapes(FrameworkElement host)
    {
        var card = (host as Decorator)?.Child as FrameworkElement ?? host;
        var origin = card == host ? new Point() : card.TranslatePoint(new Point(), host);
        var bounds = new Rect(origin, card.RenderSize);
        var target = PlacementTarget as FrameworkElement;
        var width = Math.Min(target?.ActualWidth is > 0 and var w ? w : 120, bounds.Width);
        var height = Math.Min(target?.ActualHeight is > 0 and var h ? h : 32, bounds.Height);
        // The popup sits HorizontalOffset from the pill's left edge, so the pill starts at -HorizontalOffset.
        var left = Math.Clamp(-HorizontalOffset, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        var pill = new Rect(left, bounds.Bottom - height, width, height);
        var full = bounds; full.Inflate(ShadowRoom, ShadowRoom);
        return (pill, full);
    }

    private void AnimateShape(RectAnimation rect, double radius, TimeSpan time, IEasingFunction ease)
    {
        if (shape is null) return;
        shape.BeginAnimation(RectangleGeometry.RectProperty, rect);
        var corner = new DoubleAnimation(radius, time) { EasingFunction = ease };
        shape.BeginAnimation(RectangleGeometry.RadiusXProperty, corner);
        shape.BeginAnimation(RectangleGeometry.RadiusYProperty, corner);
    }

    private void ClearMotion()
    {
        if (Child is not UIElement host) return;
        host.BeginAnimation(OpacityProperty, null); host.ClearValue(OpacityProperty);
        host.Clip = null; shape = null;
        if (Content is { } content) content.BeginAnimation(OpacityProperty, null);
        drift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void Release()
    {
        fading = false; releasing = true;
        try { SetCurrentValue(IsOpenProperty, false); }
        finally { releasing = false; }
        SetCurrentValue(StaysOpenProperty, false);
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
