using System.Windows;
using System.Windows.Media;

namespace Watchroom.Desktop;

// ClipToBounds clips to a rectangle; artwork frames use this to clip their image to rounded corners.
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0.0, Changed));
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frame) return;
        frame.SizeChanged -= Resized;
        if ((double)args.NewValue > 0) { frame.SizeChanged += Resized; Apply(frame); }
        else frame.Clip = null;
    }
    private static void Resized(object sender, SizeChangedEventArgs args) => Apply((FrameworkElement)sender);
    private static void Apply(FrameworkElement frame)
    {
        var radius = GetRadius(frame);
        var clip = new RectangleGeometry(new Rect(frame.RenderSize), radius, radius); clip.Freeze();
        frame.Clip = clip;
    }
}
