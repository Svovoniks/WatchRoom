using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Watchroom.Desktop;

/// <summary>Preserves high-resolution wheel deltas instead of taking a full step per event.</summary>
public static class PrecisionScrolling
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PrecisionScrolling), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(ScrollState), typeof(PrecisionScrolling));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    private sealed class ScrollState
    {
        public double Delta;
        public bool Scheduled;
    }

    private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) viewer.PreviewMouseWheel += Wheel;
        else
        {
            viewer.PreviewMouseWheel -= Wheel;
            viewer.ClearValue(StateProperty);
        }
    }

    private static DependencyObject? Parent(DependencyObject value) => value is Visual or Visual3D
        ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);

    private static void Wheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || args.Delta == 0) return;
        // Let the innermost viewer move first, and hand scrolling to its parent at an edge.
        ScrollViewer? target = null;
        for (var node = args.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is not ScrollViewer candidate || !GetEnabled(candidate) || candidate.ScrollableHeight <= 0) continue;
            var pending = (candidate.GetValue(StateProperty) as ScrollState)?.Delta ?? 0;
            var offset = Math.Clamp(candidate.VerticalOffset + pending, 0, candidate.ScrollableHeight);
            if (args.Delta < 0 ? offset < candidate.ScrollableHeight : offset > 0) { target = candidate; break; }
        }
        if (!ReferenceEquals(target, sender)) return;
        args.Handled = true;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var state = target.GetValue(StateProperty) as ScrollState;
        if (state is null) { state = new(); target.SetValue(StateProperty, state); }
        // Pixel viewers use 16 DIP per configured line. Logical lists retain item-based scrolling.
        var distance = lines < 0 ? target.ViewportHeight : lines * (target.CanContentScroll ? 1d : 16d);
        state.Delta -= args.Delta / 120d * distance;
        if (state.Scheduled) return;
        state.Scheduled = true;
        target.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            state.Scheduled = false;
            if (!GetEnabled(target)) return;
            var movement = target.CanContentScroll ? Math.Truncate(state.Delta) : state.Delta;
            state.Delta -= movement;
            var offset = Math.Clamp(target.VerticalOffset + movement, 0, target.ScrollableHeight);
            if (offset == 0 || offset == target.ScrollableHeight) state.Delta = 0;
            target.ScrollToVerticalOffset(offset);
        });
    }
}
