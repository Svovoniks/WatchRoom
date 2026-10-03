using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Watchroom.Desktop;

// Pixel scrolling with only the visible rows (and one spare row) realized.
public sealed class VirtualizingLibraryPanel : VirtualizingPanel, IScrollInfo
{
    private Size extent, viewport;
    private double offset, cellWidth = 194, cellHeight = 330;
    private int columns = 1;
    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new();
        var episode = owner.Items.Count > 0 && owner.Items[0] is LibraryCardView { IsEpisode: true };
        cellWidth = episode ? 314 : 194; cellHeight = episode ? 296 : 330;
        viewport = new(double.IsFinite(available.Width) ? available.Width : ActualWidth,
            double.IsFinite(available.Height) ? available.Height : ActualHeight);
        columns = Math.Max(1, (int)(viewport.Width / cellWidth));
        extent = new(viewport.Width, Math.Ceiling(owner.Items.Count / (double)columns) * cellHeight);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent.Height - viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Max(0, ((int)(offset / cellHeight) - 1) * columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((offset + viewport.Height) / cellHeight) + 1) * columns - 1);
        // Accessing children attaches the generator when a hidden grid is shown again.
        _ = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return viewport;
        // Remove offscreen containers before generating the new viewport.
        for (var index = InternalChildren.Count - 1; index >= 0; index--)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new(index, 0));
            if (itemIndex < first || itemIndex > last)
            {
                generator.Remove(new(index, 0), 1); RemoveInternalChildRange(index, 1);
            }
        }
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var created);
                    if (created)
                    {
                        InsertInternalChild(childIndex, child); generator.PrepareItemContainer(child);
                    }
                    child.Measure(new(cellWidth, cellHeight));
                }
        }
        return viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            var item = ItemContainerGenerator.IndexFromGeneratorPosition(new(index, 0));
            InternalChildren[index].Arrange(new Rect(item % columns * cellWidth, item / columns * cellHeight - offset, cellWidth, cellHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The generator has already discarded containers for removed/replaced items.
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove or
            System.Collections.Specialized.NotifyCollectionChangedAction.Replace or System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        var y = index / columns * cellHeight;
        if (y < offset) SetVerticalOffset(y);
        else if (y + cellHeight > offset + viewport.Height) SetVerticalOffset(y + cellHeight - viewport.Height);
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double value) { }
    public void SetVerticalOffset(double value)
    {
        var next = Math.Clamp(value, 0, Math.Max(0, extent.Height - viewport.Height));
        if (next == offset) return;
        offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(offset - 24);
    public void LineDown() => SetVerticalOffset(offset + 24);
    public void PageUp() => SetVerticalOffset(offset - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset + viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(offset - 72);
    public void MouseWheelDown() => SetVerticalOffset(offset + 72);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual;
        while (VisualTreeHelper.GetParent(child) is Visual parent && parent != this) child = parent;
        var index = InternalChildren.IndexOf(child as UIElement);
        if (index >= 0) BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new(index, 0)));
        return rectangle;
    }
}
