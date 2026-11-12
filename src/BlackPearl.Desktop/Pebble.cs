using System.Windows;

namespace BlackPearl.Desktop;

// A pebble button (App.xaml) that is lit: its whole body glows softly in the accent, as a main action such as Play or Connect
// does, and the icons inside it take the accent. The value is inherited, so an icon in the button's content can see it.
public static class Pebble
{
    public static readonly DependencyProperty LitProperty = DependencyProperty.RegisterAttached(
        "Lit", typeof(bool), typeof(Pebble), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetLit(DependencyObject element) => (bool)element.GetValue(LitProperty);
    public static void SetLit(DependencyObject element, bool value) => element.SetValue(LitProperty, value);
}
