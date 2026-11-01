using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// Hosts a player menu card. When the card's content changes height while the menu is open (switching
// between episodes and a queue), the card eases to its new height with its bottom edge fixed above the
// pill. Content often settles over a few layout passes, so the motion follows the latest height instead
// of finishing a stale one first. While the menu is open its popup keeps the tallest height it has
// needed (the unused part is transparent): resizing a popup window draws one misplaced frame, so the
// window only ever grows, never every frame and never back down until the menu closes.
public sealed class MenuSizer : Decorator
{
    private static readonly Duration ResizeTime = new(TimeSpan.FromMilliseconds(260));
    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private double settled = double.NaN, target, chrome, reserve;
    private bool resizing, starting;
    private int generation;
    private Size measured;

    public MenuSizer() => IsVisibleChanged += (_, _) => { if (!IsVisible) Reset(); };

    public bool IsResizing => resizing;

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not FrameworkElement card) return default;
        measured = constraint;
        var margin = card.Margin.Top + card.Margin.Bottom;
        if (resizing)
        {
            var natural = NaturalHeight(card, constraint);
            if (Math.Abs(natural - target) > 1) Retarget(card, natural, margin);
            card.Measure(constraint);
        }
        else
        {
            card.Measure(constraint);
            var natural = card.DesiredSize.Height - margin;
            if (!double.IsNaN(settled) && Math.Abs(natural - settled) > 1 && IsVisible && SystemParameters.ClientAreaAnimation)
            {
                BeginResize(card, settled, natural, margin);
                card.Measure(constraint);
            }
            else if (IsVisible) settled = natural; // Measuring a closed menu (to prepare it) sets no baseline.
        }
        var size = card.DesiredSize;
        return new Size(size.Width, Math.Max(size.Height, reserve));
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is UIElement card) { var height = card.DesiredSize.Height; card.Arrange(new Rect(0, arrangeSize.Height - height, arrangeSize.Width, height)); }
        return arrangeSize;
    }

    // The card's own height is held by the motion, so its content is measured on its own, unconstrained.
    private double NaturalHeight(FrameworkElement card, Size constraint)
    {
        if ((card as ContentControl)?.Content is not FrameworkElement content) return target;
        var width = LayoutInformation.GetLayoutSlot(content).Width;
        content.Measure(new Size(width > 0 ? width : constraint.Width, double.PositiveInfinity));
        return content.DesiredSize.Height + chrome;
    }

    private void BeginResize(FrameworkElement card, double from, double to, double margin)
    {
        resizing = starting = true; target = to; reserve = Math.Max(reserve, Math.Max(from, to) + margin);
        chrome = (card as ContentControl)?.Content is FrameworkElement content ? to - content.DesiredSize.Height : 0;
        card.Height = from;
        var current = ++generation;
        // Switching modes updates the content over a few layout passes, and while the card's height is
        // held those passes don't reach this panel. Give them two frames, then read the settled height,
        // so the card doesn't head for an in-between size and turn back, and a slow frame doesn't skip
        // the motion.
        void Start()
        {
            if (current != generation || !resizing || !starting) return;
            target = NaturalHeight(card, measured); reserve = Math.Max(reserve, target + margin);
            starting = false; Animate(card); InvalidateMeasure();
        }
        PlayerMenuPopup.OnNextFrame(() => PlayerMenuPopup.OnNextFrame(Start));
    }

    private void Retarget(FrameworkElement card, double natural, double margin)
    {
        target = natural; reserve = Math.Max(reserve, natural + margin);
        if (!starting) Animate(card);
    }

    private void Animate(FrameworkElement card)
    {
        var current = ++generation;
        var motion = new DoubleAnimation(card.Height, target, ResizeTime) { EasingFunction = Ease };
        motion.Completed += (_, _) => { if (current != generation || !resizing) return; settled = target; EndResize(card); };
        card.BeginAnimation(HeightProperty, motion);
    }

    private void EndResize(FrameworkElement card)
    {
        resizing = starting = false; generation++;
        card.BeginAnimation(HeightProperty, null); card.ClearValue(HeightProperty);
        InvalidateMeasure();
    }

    private void Reset()
    {
        settled = double.NaN; reserve = 0;
        if (resizing && Child is FrameworkElement card) EndResize(card);
    }

    private static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }
}
