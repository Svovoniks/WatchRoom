using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Watchroom.Desktop;

static class LibraryScrollingChecks
{
    public static async Task Run(ItemsControl grid, ScrollViewer scroll, Action<bool, string> check)
    {
        var panel = Descendants(grid).OfType<VirtualizingLibraryPanel>().Single();
        async Task Flush() => await grid.Dispatcher.InvokeAsync(() => grid.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Dictionary<int, FrameworkElement> Containers() => Enumerable.Range(0, grid.Items.Count)
            .Select(index => (index, child: grid.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement))
            .Where(pair => pair.child is not null).ToDictionary(pair => pair.index, pair => pair.child!);
        (int, int) Rows(double offset) => ((int)(offset / 330), (int)Math.Ceiling((offset + scroll.ViewportHeight) / 330));

        // Choose a run of small deltas that stays within the same cached rows.
        var start = Enumerable.Range(1, 28).Select(value => value * 10d).First(value => Rows(value) == Rows(value + 40));
        scroll.ScrollToVerticalOffset(start); await Flush();
        var initial = Containers();
        var columns = Math.Max(1, (int)(panel.ViewportWidth / 194));
        var sample = initial.First(pair => pair.Key / columns > 0).Value;
        var y = sample.TransformToAncestor(panel).Transform(new Point()).Y;
        var skipsLayout = true;
        for (var delta = 1; delta <= 40; delta++)
        {
            panel.SetVerticalOffset(start + delta);
            skipsLayout &= panel.IsMeasureValid && panel.IsArrangeValid;
            await Flush();
        }
        check(skipsLayout, "small library scroll deltas move visuals without remeasuring or rearranging cards");
        check(Math.Abs(sample.TransformToAncestor(panel).Transform(new Point()).Y - (y - 40)) < .1,
            "translated library cards follow every pixel of trackpad movement");
        check(Containers().All(pair => ReferenceEquals(initial[pair.Key], pair.Value)),
            "sub-row scrolling retains the same card controls");
        var sampleBounds = sample.TransformToAncestor(panel).TransformBounds(new Rect(sample.RenderSize));
        var translatedHit = panel.InputHitTest(new Point(sampleBounds.Left + 30, sampleBounds.Top + 30)) as FrameworkElement;
        check(translatedHit?.DataContext == sample.DataContext,
            "hit testing follows cards translated between layout passes");

        var controls = initial.Values.ToHashSet();
        for (var row = 1; row <= 30; row++)
        {
            scroll.ScrollToVerticalOffset(row * 330); await Flush();
            foreach (var container in Containers().Values) controls.Add(container);
        }
        check(controls.Count <= initial.Count + 2 * (int)(panel.ViewportWidth / 194),
            "scrolling thirty rows reuses a bounded pool of card controls");
        Console.WriteLine($"Library scrolling: {controls.Count} distinct controls across 30 rows; {initial.Count} initially realized.");
        var current = Containers();
        check(current.All(pair => ReferenceEquals(pair.Value.DataContext, grid.Items[pair.Key]) &&
            Descendants(pair.Value).OfType<TextBlock>().Any(text => text.Text == ((LibraryCardView)grid.Items[pair.Key]).DisplayTitle) &&
            Descendants(pair.Value).OfType<Button>().Any(button => ReferenceEquals(button.Tag, ((LibraryCardView)grid.Items[pair.Key]).Card))),
            "recycled cards bind their text and click targets to the new library item");
        check(current.Count < 50, "recycling keeps offscreen library rows unrealized");
        var visibleIndex = (int)(scroll.VerticalOffset / 330) * columns;
        var hit = panel.InputHitTest(new Point(30, 30)) as DependencyObject;
        while (hit is not null && hit is not FrameworkElement { DataContext: LibraryCardView }) hit = VisualTreeHelper.GetParent(hit);
        check(hit is FrameworkElement element && ReferenceEquals(element.DataContext, grid.Items[visibleIndex]),
            "hit testing follows scrolled card positions after recycling");
        scroll.ScrollToTop(); await Flush();
        check(grid.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first && ReferenceEquals(first.DataContext, grid.Items[0]),
            "scrolling back restores the first card with the correct binding");
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
