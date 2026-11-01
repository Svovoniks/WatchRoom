using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// A horizontal row without a scrollbar: arrow buttons on either side page through it,
// and each appears only while there is more content in its direction.
[TemplatePart(Name = "PART_Scroll", Type = typeof(ScrollViewer))]
[TemplatePart(Name = "PART_Left", Type = typeof(Button))]
[TemplatePart(Name = "PART_Right", Type = typeof(Button))]
public sealed class HorizontalScroller : ContentControl
{
    // Vertical centre of the arrows from the top of the row; NaN centres them on the whole row.
    public static readonly DependencyProperty ArrowCenterProperty = DependencyProperty.Register(
        nameof(ArrowCenter), typeof(double), typeof(HorizontalScroller), new PropertyMetadata(double.NaN, (value, _) => ((HorizontalScroller)value).PlaceArrows()));
    public double ArrowCenter { get => (double)GetValue(ArrowCenterProperty); set => SetValue(ArrowCenterProperty, value); }
    private ScrollViewer? scroll;
    private Button? left, right;
    private FrameworkElement? fadeLeft, fadeRight;
    private double target, start;
    private TimeSpan began;
    private bool animating;
    public ScrollViewer? Viewer => scroll;
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (scroll is not null) scroll.ScrollChanged -= ScrollChanged;
        if (left is not null) left.Click -= PageLeft;
        if (right is not null) right.Click -= PageRight;
        scroll = GetTemplateChild("PART_Scroll") as ScrollViewer;
        left = GetTemplateChild("PART_Left") as Button;
        right = GetTemplateChild("PART_Right") as Button;
        fadeLeft = GetTemplateChild("PART_FadeLeft") as FrameworkElement;
        fadeRight = GetTemplateChild("PART_FadeRight") as FrameworkElement;
        edges = (false, false);
        if (scroll is not null) { scroll.ScrollChanged += ScrollChanged; scroll.SizeChanged += (_, _) => UpdateArrows(); }
        if (left is not null) left.Click += PageLeft;
        if (right is not null) right.Click += PageRight;
        PlaceArrows(); UpdateArrows();
    }
    private void PlaceArrows()
    {
        foreach (var arrow in new[] { left, right }.OfType<Button>())
        {
            arrow.VerticalAlignment = double.IsNaN(ArrowCenter) ? VerticalAlignment.Center : VerticalAlignment.Top;
            var margin = arrow.Margin;
            arrow.Margin = new(margin.Left, double.IsNaN(ArrowCenter) ? 0 : Math.Max(0, ArrowCenter - arrow.Height / 2), margin.Right, 0);
        }
    }
    private void ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateArrows();
    private void UpdateArrows()
    {
        if (scroll is null) return;
        var offset = animating ? target : scroll.HorizontalOffset;
        var before = offset > 1; var after = offset < scroll.ScrollableWidth - 1;
        if (left is not null) Show(left, before);
        if (right is not null) Show(right, after);
        FadeEdges(before, after);
    }
    // Content running past an edge fades into the page there instead of ending on a hard line.
    // A canvas-coloured gradient strip is used rather than an OpacityMask, which would make
    // software rendering composite the whole row again on every frame of page scrolling.
    private (bool, bool) edges = (false, false);
    private void FadeEdges(bool before, bool after)
    {
        if (edges == (before, after)) return;
        edges = (before, after);
        foreach (var (strip, visible) in new[] { (fadeLeft, before), (fadeRight, after) })
        {
            if (strip is null) continue;
            if (!SystemParameters.ClientAreaAnimation || !strip.IsLoaded) { strip.BeginAnimation(OpacityProperty, null); strip.Opacity = visible ? 1 : 0; continue; }
            strip.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(200)));
        }
    }
    // Arrows fade rather than pop; a fading-out arrow stops taking clicks straight away.
    private static void Show(Button arrow, bool visible)
    {
        // IsHitTestVisible records the intended state; the template starts both arrows hidden.
        if (arrow.IsHitTestVisible == visible) return;
        arrow.IsHitTestVisible = visible;
        if (!SystemParameters.ClientAreaAnimation || !arrow.IsLoaded)
        {
            arrow.BeginAnimation(OpacityProperty, null); arrow.Opacity = 1;
            arrow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return;
        }
        if (visible) arrow.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 160 : 200));
        if (!visible) fade.Completed += (_, _) => { if (!arrow.IsHitTestVisible) arrow.Visibility = Visibility.Collapsed; };
        arrow.BeginAnimation(OpacityProperty, fade);
    }
    private void PageLeft(object sender, RoutedEventArgs e) { e.Handled = true; Page(-1); }
    private void PageRight(object sender, RoutedEventArgs e) { e.Handled = true; Page(1); }
    public void Page(int direction)
    {
        if (scroll is null) return;
        var from = animating ? target : scroll.HorizontalOffset;
        target = Math.Clamp(from + direction * Math.Max(200, scroll.ViewportWidth * 0.8), 0, scroll.ScrollableWidth);
        if (!SystemParameters.ClientAreaAnimation) { scroll.ScrollToHorizontalOffset(target); return; }
        start = scroll.HorizontalOffset; began = TimeSpan.MinValue;
        if (!animating) { animating = true; CompositionTarget.Rendering += Step; }
        UpdateArrows();
    }
    private void Step(object? sender, EventArgs e)
    {
        if (scroll is null || e is not RenderingEventArgs frame) { Stop(); return; }
        if (began == TimeSpan.MinValue) began = frame.RenderingTime;
        // Ease in and out so long jumps start gently and settle softly.
        var progress = Math.Clamp((frame.RenderingTime - began).TotalMilliseconds / 460, 0, 1);
        var eased = progress < 0.5 ? 4 * progress * progress * progress : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
        scroll.ScrollToHorizontalOffset(start + (target - start) * eased);
        if (progress >= 1) Stop();
    }
    private void Stop()
    {
        animating = false; CompositionTarget.Rendering -= Step;
        UpdateArrows();
    }
}
