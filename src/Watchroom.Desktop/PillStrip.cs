using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Watchroom.Desktop;

// Lays the player bar's pills out in a row and folds the trailing pills that don't fit into the overflow
// pill at the end. Folded pills stay measured but hidden, so the strip knows when they fit again.
// Popups among the children take no room.
public sealed class PillStrip : Panel
{
    public static readonly DependencyProperty OverflowProperty = DependencyProperty.Register(nameof(Overflow), typeof(UIElement), typeof(PillStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ShowsOverflowProperty = DependencyProperty.Register(nameof(ShowsOverflow), typeof(bool), typeof(PillStrip),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // A pill that is not shown is collapsed and never folds; set it rather than the pill's Visibility, which the strip manages.
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(bool), typeof(PillStrip),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static bool GetShown(UIElement element) => (bool)element.GetValue(ShownProperty);
    public static void SetShown(UIElement element, bool value) => element.SetValue(ShownProperty, value);

    // The pill that holds folded pills; it is one of the children.
    public UIElement? Overflow { get => (UIElement?)GetValue(OverflowProperty); set => SetValue(OverflowProperty, value); }
    // Keeps the overflow pill showing even when every pill fits, for other controls folded into its menu.
    public bool ShowsOverflow { get => (bool)GetValue(ShowsOverflowProperty); set => SetValue(ShowsOverflowProperty, value); }

    private readonly List<UIElement> folded = [];
    public IReadOnlyList<UIElement> Folded => folded;
    // Raised after layout when the set of folded pills changes.
    public event EventHandler? FoldedChanged;
    private int shown = int.MaxValue;
    private bool overflowing;

    private IEnumerable<UIElement> Pills => InternalChildren.Cast<UIElement>().Where(child => child is not Popup && child != Overflow && GetShown(child));

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        double height = 0;
        foreach (UIElement child in InternalChildren) { child.Measure(unbounded); height = Math.Max(height, child.DesiredSize.Height); }
        var pills = Pills.ToList();
        var overflowWidth = Overflow?.DesiredSize.Width ?? 0;
        var total = pills.Sum(pill => pill.DesiredSize.Width);
        var count = pills.Count;
        if (ShowsOverflow || total > availableSize.Width)
        {
            var room = availableSize.Width - overflowWidth;
            double used = 0;
            count = 0;
            while (count < pills.Count && used + pills[count].DesiredSize.Width <= room) used += pills[count++].DesiredSize.Width;
        }
        shown = count;
        overflowing = Overflow is not null && (count < pills.Count || ShowsOverflow);
        var width = pills.Take(count).Sum(pill => pill.DesiredSize.Width) + (overflowing ? overflowWidth : 0);
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var index = 0;
        var changed = false;
        foreach (var pill in Pills)
        {
            var fits = index++ < shown;
            if (fits) { pill.Arrange(new Rect(x, (finalSize.Height - pill.DesiredSize.Height) / 2, pill.DesiredSize.Width, pill.DesiredSize.Height)); x += pill.DesiredSize.Width; }
            else pill.Arrange(new Rect());
            changed |= Show(pill, fits);
            if (fits) changed |= folded.Remove(pill);
            else if (!folded.Contains(pill)) { folded.Add(pill); changed = true; }
        }
        if (Overflow is { } overflow)
        {
            overflow.Arrange(overflowing ? new Rect(x, (finalSize.Height - overflow.DesiredSize.Height) / 2, overflow.DesiredSize.Width, overflow.DesiredSize.Height) : new Rect());
            Show(overflow, overflowing);
        }
        foreach (UIElement child in InternalChildren)
        {
            if (child is Popup) child.Arrange(new Rect());
            else if (child != Overflow && !GetShown(child))
            {
                child.Arrange(new Rect());
                if (child.Visibility != Visibility.Collapsed) child.Visibility = Visibility.Collapsed;
                changed |= folded.Remove(child);
            }
        }
        if (changed)
        {
            folded.Sort((a, b) => InternalChildren.IndexOf(a).CompareTo(InternalChildren.IndexOf(b)));
            Dispatcher.BeginInvoke(() => FoldedChanged?.Invoke(this, EventArgs.Empty));
        }
        return finalSize;
    }

    // Hidden rather than collapsed, so a folded pill keeps its measured width.
    private static bool Show(UIElement element, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Hidden;
        if (element.Visibility == visibility) return false;
        element.Visibility = visibility;
        return true;
    }
}
