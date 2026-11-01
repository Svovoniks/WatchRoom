using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

// Finds outlines that an ancestor's clip cuts off: a visible border or stroked shape whose edge
// falls outside a clipping ancestor while the rest of it shows. Content scrolled past a viewport
// edge is expected and skipped; outlines that start outside an unscrolled viewport are not.
static class BorderAudit
{
    public static List<string> Find(Visual root)
    {
        var found = new List<string>(); var all = new List<FrameworkElement>(); var state = "";
        // Hidden (not collapsed) outlines keep their layout: hover rings wait that way until the pointer arrives.
        void Collect(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is FrameworkElement element) all.Add(element);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i));
        }
        void Check()
        {
            foreach (var element in all)
                if (Outline(element) is { } stroke && Clipped(root, element) is { } finding) found.Add($"{Describe(element)} ({stroke}) {finding}{state}");
        }
        Collect(root); Check();
        // Hovered Home artwork rises 6px; check every card in its raised position too.
        var lifts = all.Where(x => x.Style is { } style && ReferenceEquals(style, x.TryFindResource("HomeArtwork")) && x.RenderTransform is TranslateTransform { IsFrozen: false })
            .Select(x => (TranslateTransform)x.RenderTransform).ToList();
        if (lifts.Count > 0)
        {
            foreach (var lift in lifts) lift.Y = -6;
            state = " while hovered"; Check();
            foreach (var lift in lifts) lift.Y = 0;
        }
        return found.Distinct().ToList();
    }

    // Borders count even when transparent now: hover, selection and focus triggers colour them later.
    private static string? Outline(FrameworkElement element) => element switch
    {
        { ActualWidth: < 1 } or { ActualHeight: < 1 } => null,
        Border border when border.BorderThickness is { Left: > 0 } or { Top: > 0 } or { Right: > 0 } or { Bottom: > 0 } => "border " + border.BorderThickness,
        Shape { Stroke: not null, StrokeThickness: > 0 } shape => "stroke " + shape.StrokeThickness,
        _ => null
    };

    private static string? Clipped(Visual root, FrameworkElement element)
    {
        // Work in root coordinates; clips that scroll trim the visible part before outer clips judge it.
        Rect visible;
        try { visible = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight)); }
        catch (InvalidOperationException) { return null; }
        for (var parent = VisualTreeHelper.GetParent(element) as Visual; parent is not null; parent = VisualTreeHelper.GetParent(parent) as Visual)
        {
            if (VisualTreeHelper.GetClip(parent) is { } clip && !clip.Bounds.IsEmpty)
            {
                Rect view;
                try { view = parent.TransformToAncestor(root).TransformBounds(clip.Bounds); } catch (InvalidOperationException) { return null; }
                if (!visible.IntersectsWith(view)) return null; // scrolled or virtualized away entirely
                double left = view.Left - visible.Left, top = view.Top - visible.Top, right = visible.Right - view.Right, bottom = visible.Bottom - view.Bottom;
                // Scrolled content: the far side overflows while scrollable, the near side once scrolled.
                if (parent is ScrollContentPresenter { TemplatedParent: ScrollViewer scroll })
                {
                    // Panels that scroll themselves (IScrollInfo) report their extent to the owner, not the presenter.
                    if (scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) { right = 0; if (scroll.HorizontalOffset > 0) left = 0; }
                    if (scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) { bottom = 0; if (scroll.VerticalOffset > 0) top = 0; }
                }
                var sides = new[] { ("left", left), ("top", top), ("right", right), ("bottom", bottom) }.Where(x => x.Item2 > 0.5).ToArray();
                if (sides.Length > 0)
                    return "clipped " + string.Join(", ", sides.Select(x => $"{x.Item1} {x.Item2:0.#}px")) + " by " + Describe(parent);
                visible.Intersect(view);
            }
            if (ReferenceEquals(parent, root)) break;
        }
        return null;
    }

    private static string Describe(DependencyObject node)
    {
        var parts = new List<string>();
        for (DependencyObject? current = node; current is not null && parts.Count < 4; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not FrameworkElement element) continue;
            var label = element.Name is { Length: > 0 } name ? name : AutomationProperties.GetName(element) is { Length: > 0 } automation ? $"\"{automation}\"" :
                element is ContentControl { Content: string text } ? $"\"{text}\"" : null;
            if (ReferenceEquals(current, node) || label is not null) parts.Add(element.GetType().Name + (label is null ? "" : " " + label));
        }
        return string.Join(" < ", parts);
    }
}
