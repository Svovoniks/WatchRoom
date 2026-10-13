using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Watchroom.Desktop;

// Dialogs keep the same native caption colors and app identity as the main window.
public static class WindowTheme
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Window window || window is MainWindow) return;
        if ((bool)args.NewValue)
        {
            window.SourceInitialized += Refresh; window.Activated += Refresh; window.Deactivated += Refresh;
            Apply(window);
        }
        else
        {
            window.SourceInitialized -= Refresh; window.Activated -= Refresh; window.Deactivated -= Refresh;
        }
    }
    private static void Refresh(object? sender, EventArgs args) { if (sender is Window window) Apply(window); }
    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || Application.Current?.Resources["Canvas"] is not SolidColorBrush canvas) return;
        var color = canvas.Color;
        uint dark = !SystemParameters.HighContrast && color.R + color.G + color.B < 384 ? 1u : 0u;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(uint));
        uint ColorRef(string key)
        {
            if (SystemParameters.HighContrast || Application.Current.Resources[key] is not SolidColorBrush brush) return uint.MaxValue;
            return (uint)(brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16);
        }
        var caption = ColorRef("Canvas"); var text = ColorRef(window.IsActive ? "Ink" : "Muted"); var border = ColorRef("Line");
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(uint));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(uint));
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(uint));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);
}
