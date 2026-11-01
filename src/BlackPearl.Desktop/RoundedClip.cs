using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlackPearl.Desktop;

// ClipToBounds clips to a rectangle; artwork frames use this to clip their image to rounded corners.
// A Border clips only its child, inside the stroke, so the outline itself stays whole and crisp.
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, Changed));
    private static readonly DependencyProperty ClippedChildProperty = DependencyProperty.RegisterAttached(
        "ClippedChild", typeof(FrameworkElement), typeof(RoundedClip));
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frame) return;
        frame.SizeChanged -= Resized; frame.Loaded -= Resized;
        if ((double)args.NewValue > 0) { frame.SizeChanged += Resized; frame.Loaded += Resized; Apply(frame); }
        else
        {
            frame.Clip = null;
            if (frame.GetValue(ClippedChildProperty) is FrameworkElement child) { child.Clip = null; child.SizeChanged -= ChildResized; frame.ClearValue(ClippedChildProperty); }
        }
    }
    private static void Resized(object sender, RoutedEventArgs args) => Apply((FrameworkElement)sender);
    private static void ChildResized(object sender, SizeChangedEventArgs args)
    {
        if (((FrameworkElement)sender).Parent is Border border) Apply(border);
    }
    private static void Apply(FrameworkElement frame)
    {
        var radius = GetRadius(frame);
        if (radius <= 0) return;
        if (frame is Border { Child: FrameworkElement child } border)
        {
            if (frame.GetValue(ClippedChildProperty) is FrameworkElement previous && !ReferenceEquals(previous, child)) { previous.Clip = null; previous.SizeChanged -= ChildResized; }
            if (!ReferenceEquals(frame.GetValue(ClippedChildProperty), child)) { child.SizeChanged += ChildResized; frame.SetValue(ClippedChildProperty, child); }
            frame.Clip = null;
            var stroke = border.BorderThickness;
            var inner = Math.Max(0, radius - Math.Max(stroke.Left, stroke.Top));
            var childClip = new RectangleGeometry(new Rect(child.RenderSize), inner, inner); childClip.Freeze();
            child.Clip = childClip;
            return;
        }
        var clip = new RectangleGeometry(new Rect(frame.RenderSize), radius, radius); clip.Freeze();
        frame.Clip = clip;
    }
}
