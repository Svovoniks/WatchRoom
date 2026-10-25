using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
        ApplyWindowCorners(handle);
        StateChanged += (_, _) => UpdateCaption();
        DpiChanged += (_, _) => UpdateCaption();
        UpdateCaption();
    }
    // Fullscreen is a normal window covering the monitor rather than a maximized one, so Windows would still round it.
    private void ApplyWindowCorners(IntPtr handle)
    {
        uint corners = fullscreen ? 1u /* DWMWCP_DONOTROUND */ : 2u /* DWMWCP_ROUND */;
        DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(uint));
        RefreshPlayerCorners();
    }
    private void UpdateCaption()
    {
        var maximized = WindowState == WindowState.Maximized;
        foreach (var button in new[] { MaximizeButton, PlayerMaximizeButton })
        {
            button.Content = maximized ? "" : "";
            System.Windows.Automation.AutomationProperties.SetName(button, maximized ? "Restore" : "Maximize");
        }
        // A maximized window extends past the monitor by its resize frame; keep content on screen.
        var handle = new WindowInteropHelper(this).Handle;
        if (maximized && !fullscreen && handle != IntPtr.Zero)
        {
            var dpi = GetDpiForWindow(handle);
            var frame = (GetSystemMetricsForDpi(32 /* SM_CXSIZEFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi)) * 96d / dpi;
            WindowRoot.Margin = new Thickness(frame);
        }
        else WindowRoot.Margin = new Thickness(0);
        RefreshPlayerCorners();
    }
    // The player has no caption row: the video reaches the top of the window and the player's top bar carries the
    // window buttons, except in fullscreen, which has none.
    private void ApplyCaptionLayout(bool inPlayer)
    {
        var captionless = fullscreen || inPlayer;
        TitleRow.Height = new GridLength(captionless ? 0 : CaptionHeight);
        CaptionBar.Visibility = captionless ? Visibility.Collapsed : Visibility.Visible;
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = captionless ? 0 : CaptionHeight;
        PlayerCaptionButtons.Visibility = inPlayer && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaption();
    }

    // The video and its overlay cover the window's edges, so the overlay resizes the window from them as the frame would,
    // and the top bar moves it. Windows runs its own move or size loop until the button is released.
    private const double PlayerResizeEdge = 6;
    private int DockedPlayerHit(Point position)
    {
        if (fullscreen || pictureInPicture || WindowState != WindowState.Normal || RoomPage.Visibility != Visibility.Visible) return 0;
        return EdgeHit(position, PlayerOverlay.RenderSize, PlayerResizeEdge);
    }
    private static int EdgeHit(Point position, Size size, double edge, bool rightEdge = true)
    {
        bool left = position.X < edge, right = rightEdge && position.X > size.Width - edge;
        bool top = position.Y < edge, bottom = position.Y > size.Height - edge;
        return (top, bottom, left, right) switch
        {
            (true, _, true, _) => 13, // HTTOPLEFT
            (true, _, _, true) => 14, // HTTOPRIGHT
            (_, true, true, _) => 16, // HTBOTTOMLEFT
            (_, true, _, true) => 17, // HTBOTTOMRIGHT
            (true, _, _, _) => 12, // HTTOP
            (_, true, _, _) => 15, // HTBOTTOM
            (_, _, true, _) => 10, // HTLEFT
            (_, _, _, true) => 11, // HTRIGHT
            _ => 0,
        };
    }
    private static Cursor? EdgeCursor(int hit) => hit switch
    {
        13 or 17 => Cursors.SizeNWSE,
        14 or 16 => Cursors.SizeNESW,
        12 or 15 => Cursors.SizeNS,
        10 or 11 => Cursors.SizeWE,
        _ => null,
    };
    // Over the edges the resize cursor wins over the bars' own cursors.
    private void ShowDockedEdgeCursor(Point position)
    {
        var cursor = EdgeCursor(DockedPlayerHit(position));
        if (cursor is null && !PlayerOverlay.ForceCursor) return;
        PlayerOverlay.ForceCursor = cursor is not null;
        PlayerOverlay.Cursor = cursor ?? Cursors.Arrow;
    }
    private void PlayerOverlayPressed(object sender, MouseButtonEventArgs e)
    {
        // Presses in the bar's menus come from their own popup windows, not the overlay's edges.
        if (e.OriginalSource is not System.Windows.Media.Visual source || PresentationSource.FromVisual(source) != PresentationSource.FromVisual(PlayerOverlay)) return;
        var hit = DockedPlayerHit(e.GetPosition(PlayerOverlay));
        if (hit == 0) return;
        e.Handled = true;
        StartWindowMove(hit);
    }
    private void RoomHeaderPressed(object sender, MouseButtonEventArgs e)
    {
        if (fullscreen || pictureInPicture) return;
        e.Handled = true;
        if (e.ClickCount == 2) ToggleMaximizeWindow(sender, e);
        else StartWindowMove(2 /* HTCAPTION */);
    }
    private void StartWindowMove(int hit)
    {
        Mouse.Capture(null);
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(hit), IntPtr.Zero);
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
