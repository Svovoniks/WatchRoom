using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Watchroom.Desktop;

// Picture in picture shrinks the main window to just the video and keeps it above every other window.
// The native video and its overlay cover the whole window, so the overlay hands presses on the video
// to Windows as caption and border hits: dragging moves the window and its edges resize it.
// The window keeps the video's shape, so resizing scales the picture instead of growing black bars.
public partial class MainWindow
{
    private const double PictureInPictureEdge = 8, PictureInPictureMinSide = 135;
    private bool pictureInPicture;
    private WindowState pictureInPicturePreviousState;
    private Rect pictureInPicturePreviousBounds;
    private Size pictureInPicturePreviousMinimum;
    // Where the small player was last left, so it reopens there during this session.
    private Rect? pictureInPictureBounds;
    // The width-to-height ratio the window currently follows; zero outside picture in picture.
    private double pictureInPictureAspect;

    private void EnterPictureInPicture(object sender, RoutedEventArgs e) => SetPictureInPicture(true);
    private void ExitPictureInPicture(object sender, RoutedEventArgs e) => SetPictureInPicture(false);
    private void SetPictureInPicture(bool value)
    {
        if (pictureInPicture == value || value && RoomPage.Visibility != Visibility.Visible) return;
        if (value && fullscreen) SetFullscreen(false);
        ClosePlayerMenusNow();
        if (value)
        {
            pictureInPicturePreviousState = WindowState;
            pictureInPicturePreviousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            pictureInPicturePreviousMinimum = new Size(MinWidth, MinHeight);
            pictureInPicture = true;
            WindowState = WindowState.Normal;
            // The video's shape sets the real minimum once it is known.
            MinWidth = MinHeight = PictureInPictureMinSide;
            Topmost = true;
            // The video column's minimum would push the video and its overlay past a small window.
            VideoColumn.MinWidth = 0;
            ApplyPlayerLayout();
            var bounds = pictureInPictureBounds ?? DefaultPictureInPictureBounds();
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            pictureInPictureAspect = 0;
            FitPictureInPictureToVideo();
        }
        else
        {
            pictureInPictureBounds = new Rect(Left, Top, Width, Height);
            pictureInPicture = false;
            pictureInPictureAspect = 0;
            Topmost = false;
            VideoColumn.MinWidth = 320;
            MinWidth = pictureInPicturePreviousMinimum.Width; MinHeight = pictureInPicturePreviousMinimum.Height;
            ApplyPlayerLayout();
            Left = pictureInPicturePreviousBounds.Left; Top = pictureInPicturePreviousBounds.Top;
            Width = pictureInPicturePreviousBounds.Width; Height = pictureInPicturePreviousBounds.Height;
            WindowState = pictureInPicturePreviousState;
        }
        var chrome = pictureInPicture ? Visibility.Collapsed : Visibility.Visible;
        PlayerTopChrome.Visibility = PlayerBottomChrome.Visibility = chrome;
        PictureInPictureControls.Visibility = Visibility.Collapsed;
        RoomPlaybackNotice.Margin = pictureInPicture ? new Thickness(12) : new Thickness(24, 112, 24, 112);
        PlayerOverlay.Cursor = Cursors.Arrow;
        ShowPlayerControls();
        PlayerOverlay.Focus();
    }
    // A 16:9 player in the bottom-right corner of the monitor's work area; it then takes the video's shape.
    private Rect DefaultPictureInPictureBounds()
    {
        var work = MonitorWorkArea();
        const double width = 480, height = 270, margin = 24;
        return new Rect(work.Right - width - margin, work.Bottom - height - margin, width, height);
    }
    private Rect MonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;
        var toDips = target.TransformFromDevice;
        return new Rect(toDips.Transform(new Point(monitor.Work.Left, monitor.Work.Top)), toDips.Transform(new Point(monitor.Work.Right, monitor.Work.Bottom)));
    }
    private double VideoAspect()
    {
        uint width = 0, height = 0;
        return player is not null && player.Size(0, ref width, ref height) && width > 0 && height > 0 ? (double)width / height : 16d / 9;
    }
    // Reshapes the window when the video's shape becomes known or changes, keeping about the same area.
    // The window stays anchored to the screen edges it is nearest, so a corner player stays in its corner.
    private void FitPictureInPictureToVideo()
    {
        if (!pictureInPicture) return;
        var aspect = VideoAspect();
        if (Math.Abs(aspect - pictureInPictureAspect) < 0.01) return;
        pictureInPictureAspect = aspect;
        if (aspect >= 1) { MinHeight = PictureInPictureMinSide; MinWidth = PictureInPictureMinSide * aspect; }
        else { MinWidth = PictureInPictureMinSide; MinHeight = PictureInPictureMinSide / aspect; }
        var work = MonitorWorkArea();
        var width = Math.Max(MinWidth, Math.Sqrt(Width * Height * aspect));
        var height = width / aspect;
        var left = Left + Width / 2 > work.Left + work.Width / 2 ? Left + Width - width : Left;
        var top = Top + Height / 2 > work.Top + work.Height / 2 ? Top + Height - height : Top;
        Left = left; Top = top; Width = width; Height = height;
    }
    // Adjusts the rectangle Windows proposes while an edge is dragged so the window keeps the video's shape.
    // The dragged edge leads; for a corner, whichever direction grew the window more.
    private void KeepPictureInPictureAspect(int edge, IntPtr proposed)
    {
        var bounds = Marshal.PtrToStructure<NativeRect>(proposed);
        double width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top, aspect = pictureInPictureAspect;
        // WMSZ_LEFT 1, RIGHT 2, TOP 3, TOPLEFT 4, TOPRIGHT 5, BOTTOM 6, BOTTOMLEFT 7, BOTTOMRIGHT 8
        var heightLeads = edge is 3 or 6 || edge is 4 or 5 or 7 or 8 && height * aspect > width;
        if (heightLeads)
        {
            var fitted = (int)Math.Round(height * aspect);
            if (edge is 4 or 7) bounds.Left = bounds.Right - fitted; else bounds.Right = bounds.Left + fitted;
        }
        else
        {
            var fitted = (int)Math.Round(width / aspect);
            if (edge is 3 or 4 or 5) bounds.Top = bounds.Bottom - fitted; else bounds.Bottom = bounds.Top + fitted;
        }
        Marshal.StructureToPtr(bounds, proposed, false);
    }
    private void InitializePictureInPicture(IntPtr handle)
    {
        HwndSource.FromHwnd(handle)?.AddHook(VideoHostWindowMessage);
        // The overlay content lives in LibVLC's separate window, so the small play button follows the bar's button by source.
        foreach (var property in new[] { System.Windows.Controls.ContentControl.ContentProperty, UIElement.IsEnabledProperty, FrameworkElement.ToolTipProperty })
            PictureInPicturePlayButton.SetBinding(property, new System.Windows.Data.Binding(property.Name) { Source = PlayButton });
    }
    private void PlayerMouseLeft(object sender, MouseEventArgs e)
    {
        if (pictureInPicture) PictureInPictureControls.Visibility = Visibility.Collapsed;
    }
    // Matches the pointer to the window border it is near, or the caption elsewhere.
    private int PictureInPictureHit(Point position)
    {
        bool left = position.X < PictureInPictureEdge, right = position.X > PlayerOverlay.ActualWidth - PictureInPictureEdge;
        bool top = position.Y < PictureInPictureEdge, bottom = position.Y > PlayerOverlay.ActualHeight - PictureInPictureEdge;
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
            _ => 2, // HTCAPTION
        };
    }
    private void PictureInPictureMouseMoved(MouseEventArgs e)
    {
        if (e.OriginalSource != PlayerOverlay) { PlayerOverlay.Cursor = Cursors.Arrow; return; }
        PlayerOverlay.Cursor = PictureInPictureHit(e.GetPosition(PlayerOverlay)) switch
        {
            13 or 17 => Cursors.SizeNWSE,
            14 or 16 => Cursors.SizeNESW,
            12 or 15 => Cursors.SizeNS,
            10 or 11 => Cursors.SizeWE,
            _ => Cursors.Arrow,
        };
    }
    private void PictureInPicturePressed(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { SetPictureInPicture(false); return; }
        var hit = PictureInPictureHit(e.GetPosition(PlayerOverlay));
        // The hover scrim sits in LibVLC's separate overlay window, which trails a resize.
        if (hit != 2) PictureInPictureControls.Visibility = Visibility.Collapsed;
        // Windows runs its own move or size loop until the button is released.
        PlayerOverlay.ReleaseMouseCapture();
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(hit), IntPtr.Zero);
    }
    // LibVLC moves its video windows to a new size only when it next draws a frame, so on its own the picture trails
    // a resize and black shows at the moving edges. Sizing its windows with this one straight away lets Direct3D stretch
    // the current frame over them until LibVLC draws at the new size. The window keeps the video's shape, so the stretch
    // never distorts the picture. LibVLC's windows belong to its own thread, so their new size is posted, not awaited.
    private void FollowPictureInPictureSize(IntPtr size)
    {
        int width = (int)(size.ToInt64() & 0xffff), height = (int)((size.ToInt64() >> 16) & 0xffff);
        if (width <= 0 || height <= 0) return;
        IntPtr host = IntPtr.Zero, video = IntPtr.Zero, output = IntPtr.Zero;
        var name = new System.Text.StringBuilder(64);
        EnumChildWindows(new WindowInteropHelper(this).Handle, (child, _) =>
        {
            name.Clear(); GetClassName(child, name, name.Capacity);
            var className = name.ToString();
            if (host == IntPtr.Zero && className == "Static") host = child;
            else if (className.StartsWith("VLC video main", StringComparison.Ordinal)) video = child;
            else if (className.StartsWith("VLC video output", StringComparison.Ordinal)) output = child;
            return true;
        }, IntPtr.Zero);
        if (host == IntPtr.Zero) return;
        const uint place = 0x0004 /* SWP_NOZORDER */ | 0x0010 /* SWP_NOACTIVATE */;
        SetWindowPos(host, IntPtr.Zero, 0, 0, width, height, place | 0x0002 /* SWP_NOMOVE */);
        if (video != IntPtr.Zero) SetWindowPos(video, IntPtr.Zero, 0, 0, width, height, place | 0x0002 | 0x4000 /* SWP_ASYNCWINDOWPOS */);
        if (output != IntPtr.Zero) SetWindowPos(output, IntPtr.Zero, 0, 0, width, height, place | 0x4000);
    }
    // Keeps the video's shape and size while picture in picture is resized. LibVLC also draws into a native static control hosted
    // in this window: while the window grows, Windows paints the newly exposed part of that control in the light dialog
    // colour before the video catches up, so it is painted black instead.
    private IntPtr VideoHostWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0214 /* WM_SIZING */ && pictureInPicture && pictureInPictureAspect > 0) KeepPictureInPictureAspect(wParam.ToInt32(), lParam);
        if (message == 0x0005 /* WM_SIZE */ && pictureInPicture) FollowPictureInPictureSize(lParam);
        if (message != 0x0138 /* WM_CTLCOLORSTATIC */) return IntPtr.Zero;
        SetBkColor(wParam, 0);
        handled = true;
        return GetStockObject(4 /* BLACK_BRUSH */);
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll")]
    private static extern uint SetBkColor(IntPtr dc, uint color);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
