using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BlackPearl.Desktop;

// Pressing anywhere on a slider moves its knob there, and the knob keeps following the pointer until the button is released.
public static class SliderFollow
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SliderFollow), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider) return;
        slider.PreviewMouseLeftButtonDown -= Pressed; slider.PreviewMouseMove -= Moved; slider.PreviewMouseLeftButtonUp -= Released;
        if (e.NewValue is not true) return;
        slider.PreviewMouseLeftButtonDown += Pressed; slider.PreviewMouseMove += Moved; slider.PreviewMouseLeftButtonUp += Released;
    }
    private static void Pressed(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || !Follow(slider, e)) return;
        slider.Focus();
        slider.CaptureMouse();
        e.Handled = true;
    }
    private static void Moved(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Follow(slider, e);
    }
    private static void Released(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsMouseCaptured) return;
        slider.ReleaseMouseCapture();
        e.Handled = true;
    }
    private static bool Follow(Slider slider, MouseEventArgs e)
    {
        if (slider.Template?.FindName("PART_Track", slider) is not Track track || ValueAt(slider, track, e.GetPosition(track)) is not { } value) return false;
        slider.Value = value;
        return true;
    }
    // The value under a point on the track, worked out from the track's size alone. Track.ValueFromPoint measures from where the
    // knob was last laid out, so several pointer moves between layout passes would add up and throw the knob to an end.
    internal static double? ValueAt(Slider slider, Track track, Point point)
    {
        var horizontal = track.Orientation == Orientation.Horizontal;
        var knob = track.Thumb is { } thumb ? horizontal ? thumb.ActualWidth : thumb.ActualHeight : 0;
        var length = (horizontal ? track.ActualWidth : track.ActualHeight) - knob;
        if (length <= 0) return null;
        var fraction = Math.Clamp(((horizontal ? point.X : point.Y) - knob / 2) / length, 0, 1);
        if (!horizontal) fraction = 1 - fraction; // Upright sliders grow from the bottom.
        if (track.IsDirectionReversed) fraction = 1 - fraction;
        var value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
            value = slider.Minimum + Math.Round((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
        return Math.Clamp(value, slider.Minimum, slider.Maximum);
    }
}
