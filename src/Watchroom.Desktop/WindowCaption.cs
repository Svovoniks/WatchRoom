using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Watchroom.Desktop;

// The app draws its own caption row. WindowChrome keeps native dragging, resizing and
// the system menu; the maximize button reports itself to Windows so snap layouts appear.
public partial class MainWindow
{
    private const double CaptionHeight = 40;
    private void InitializeCaption(IntPtr handle)
    {
        HwndSource.FromHwnd(handle)?.AddHook(CaptionWindowMessage);
        uint round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(uint));
        StateChanged += (_, _) => UpdateCaption();
        DpiChanged += (_, _) => UpdateCaption();
        UpdateCaption();
    }
    private void UpdateCaption()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
        // A maximized window extends past the monitor by its resize frame; keep content on screen.
        var handle = new WindowInteropHelper(this).Handle;
        if (maximized && !fullscreen && handle != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(handle);
            var frame = (GetSystemMetricsForDpi(32 /* SM_CXSIZEFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi)) * 96d / dpi;
            WindowRoot.Margin = new Thickness(frame);
        }
        else WindowRoot.Margin = new Thickness(0);
    }
    // Fullscreen and picture-in-picture video have no caption; the player gets the dark caption that matches its header.
    private void ApplyCaptionLayout(bool inPlayer)
    {
        var captionless = fullscreen;
        TitleRow.Height = new GridLength(captionless ? 0 : CaptionHeight);
        CaptionBar.Visibility = captionless ? Visibility.Collapsed : Visibility.Visible;
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = captionless ? 0 : CaptionHeight;
        CaptionIdentity.Visibility = inPlayer ? Visibility.Visible : Visibility.Collapsed;
        CaptionBar.Background = inPlayer ? (System.Windows.Media.Brush)FindResource("PlayerSurface") : null;
        if (inPlayer) { CaptionBar.Resources["Ink"] = FindResource("PlayerInk"); CaptionBar.Resources["Muted"] = FindResource("PlayerMuted"); }
        else { CaptionBar.Resources.Remove("Ink"); CaptionBar.Resources.Remove("Muted"); }
        UpdateCaption();
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void ToggleMaximizeWindow(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private bool OverMaximizeButton(IntPtr lParam)
    {
        if (fullscreen || !MaximizeButton.IsVisible || PresentationSource.FromVisual(MaximizeButton) is null) return false;
        var point = MaximizeButton.PointFromScreen(new Point((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff)));
        return point.X >= 0 && point.Y >= 0 && point.X < MaximizeButton.ActualWidth && point.Y < MaximizeButton.ActualHeight;
    }
    private IntPtr CaptionWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int HtMaxButton = 9;
        switch (message)
        {
            case 0x0084: // WM_NCHITTEST
                var over = OverMaximizeButton(lParam);
                if (!over) { if (MaximizeButton.Tag is not null) MaximizeButton.Tag = null; break; }
                if (MaximizeButton.Tag is null) MaximizeButton.Tag = "hover";
                handled = true; return new IntPtr(HtMaxButton);
            case 0x00A1 when wParam.ToInt32() == HtMaxButton: // WM_NCLBUTTONDOWN
                MaximizeButton.Tag = "pressed"; handled = true; break;
            case 0x00A2 when wParam.ToInt32() == HtMaxButton: // WM_NCLBUTTONUP
                if (MaximizeButton.Tag as string == "pressed") ToggleMaximizeWindow(MaximizeButton, new RoutedEventArgs());
                MaximizeButton.Tag = null; handled = true; break;
            case 0x00A0 when wParam.ToInt32() != HtMaxButton: // WM_NCMOUSEMOVE elsewhere
            case 0x02A2: // WM_NCMOUSELEAVE
                MaximizeButton.Tag = null; break;
        }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
