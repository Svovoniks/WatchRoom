using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BlackPearl.Desktop;

// A pebble button (App.xaml) that is lit: its whole body glows softly in the accent, as a main action such as Play or Connect
// does, and the icons inside it take the accent. The value is inherited, so an icon in the button's content can see it.
// Radius and Depth shape the pebble: a small chip is rounder for its size and stands lower than a button.
public static class Pebble
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(Pebble), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetLit(DependencyObject element) => (bool)element.GetValue(LitProperty);
    public static void SetLit(DependencyObject element, bool value) => element.SetValue(LitProperty, value);

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(Pebble), new FrameworkPropertyMetadata(16d));
    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    public static readonly DependencyProperty DepthProperty = DependencyProperty.RegisterAttached(
        "Depth", typeof(StoneDepth), typeof(Pebble), new FrameworkPropertyMetadata(StoneDepth.Pebble));
    public static StoneDepth GetDepth(DependencyObject element) => (StoneDepth)element.GetValue(DepthProperty);
    public static void SetDepth(DependencyObject element, StoneDepth value) => element.SetValue(DepthProperty, value);
}

// A radius in DIP as a uniform CornerRadius, optionally grown by the parameter (a focus ring drawn outside the shape).
public sealed class CornerRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var radius = value is double number ? number : 0;
        if (parameter is string grow && double.TryParse(grow, NumberStyles.Float, CultureInfo.InvariantCulture, out var extra)) radius += extra;
        return new CornerRadius(Math.Max(0, radius));
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
