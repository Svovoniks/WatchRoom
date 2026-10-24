using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Watchroom.Desktop;

// Picture in picture moves the video into a small borderless window that stays above every other window,
// while the main window goes back to the app. The native video and its overlay cover the whole small window,
// so the overlay hands presses on the video to Windows as caption and border hits: dragging moves the window
// and its edges resize it. The window keeps the video's shape, so resizing scales the picture instead of
// growing black bars.
public partial class MainWindow
{
    private const double PictureInPictureEdge = 8, PictureInPictureMinSide = 135;
    private bool pictureInPicture;
    private Window? pictureInPictureWindow;
    private IntPtr pictureInPictureHandle;
    // Where the small player was last left, so it reopens there during this session.
    private Rect? pictureInPictureBounds;
    // The width-to-height ratio the window currently follows; zero outside picture in picture.
    private double pictureInPictureAspect;

    private void EnterPictureInPicture(object sender, RoutedEventArgs e) => SetPictureInPicture(true);
    private void ExitPictureInPicture(object sender, RoutedEventArgs e) => SetPictureInPicture(false);
    // Leaving picture in picture opens the full player unless the video is ending anyway.
    private void SetPictureInPicture(bool value, bool openPlayer = true)
    {
        if (pictureInPicture == value || value && RoomPage.Visibility != Visibility.Visible) return;
        if (value && fullscreen) SetFullscreen(false);
        ClosePlayerMenusNow();
        if (value)
        {
            var window = PictureInPictureWindow();
            var bounds = pictureInPictureBounds ?? DefaultPictureInPictureBounds();
            pictureInPicture = true;
            window.MinWidth = window.MinHeight = PictureInPictureMinSide;
            window.Left = bounds.Left; window.Top = bounds.Top; window.Width = bounds.Width; window.Height = bounds.Height;
            // The native video window moves with its host element, so playback carries on uninterrupted.
            VideoFrameHost.Children.Remove(VideoFrame);
            window.Content = VideoFrame;
            window.Show();
            pictureInPictureAspect = 0;
            FitPictureInPictureToVideo();
            KeepPlayerOverlayAbove();
            // The main window returns to where the player was opened from.
            if (backLocations.Count == 0 || backLocations[^1].Page == "Room" || !BrowseHistory(true)) ShowPage("Home");
        }
        else
        {
            var window = pictureInPictureWindow!;
            pictureInPictureBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
            pictureInPicture = false;
            pictureInPictureAspect = 0;
            window.Content = null;
            VideoFrameHost.Children.Insert(0, VideoFrame);
            window.Hide();
            KeepPlayerOverlayAbove();
            if (openPlayer)
            {
                ShowPage("Room");
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
            // Navigation can be declined, for example with unsaved settings; the video then stays hidden behind that page.
            PlayerOverlay.Visibility = Video.Visibility = RoomPage.Visibility;
        }
        var chrome = pictureInPicture ? Visibility.Collapsed : Visibility.Visible;
        PlayerTopChrome.Visibility = PlayerBottomChrome.Visibility = chrome;
        RefreshPictureInPictureUpNext();
        PictureInPictureControls.Visibility = Visibility.Collapsed;
        RoomPlaybackNotice.Margin = pictureInPicture ? new Thickness(12) : new Thickness(24, 112, 24, 112);
        PlayerOverlay.Cursor = Cursors.Arrow;
        ShowPlayerControls();
        PlayerOverlay.Focus();
    }
    // LibVLC's overlay window takes the small window as its owner once the video has moved, but an owned window
    // that is not itself topmost stays below a topmost owner, where it can no longer receive clicks.
    // The overlay moves to its new owner as the video loads there, so wait for that before raising it.
    private void KeepPlayerOverlayAbove(int attempts = 20) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        if (Window.GetWindow(PlayerOverlay) is not { } overlay || overlay == this) return;
        if (overlay.Owner != (pictureInPicture ? pictureInPictureWindow : this) && attempts > 0) { KeepPlayerOverlayAbove(attempts - 1); return; }
        overlay.Topmost = pictureInPicture;
        RaisePlayerOverlay();
    });
    // Activating or reordering the small window can still lift it over its topmost overlay, so the overlay is put back on top.
    private void RaisePlayerOverlay()
    {
        if (!pictureInPicture || Window.GetWindow(PlayerOverlay) is not { } overlay || overlay == this) return;
        SetWindowPos(new WindowInteropHelper(overlay).Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 /* SWP_NOSIZE */ | 0x0002 /* SWP_NOMOVE */ | 0x0010 /* SWP_NOACTIVATE */);
    }
    private Grid VideoFrameHost => videoFrameHost ??= (Grid)VideoFrame.Parent;
    private Grid? videoFrameHost;
    private Window PictureInPictureWindow()
    {
        if (pictureInPictureWindow is not null) return pictureInPictureWindow;
        // Resizable but without a visible frame or caption: the overlay provides both through hit testing.
        var window = new Window
        {
            Title = Title, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
            Topmost = true, Background = Brushes.Black, WindowStartupLocation = WindowStartupLocation.Manual, UseLayoutRounding = true,
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        // The player's styles and its always-dark palette come from the main window and the player page.
        window.Resources.MergedDictionaries.Add(Resources);
        window.Resources.MergedDictionaries.Add(RoomPage.Resources);
        TextElement.SetForeground(window, TextElement.GetForeground(RoomPage));
        TextOptions.SetTextFormattingMode(window, TextOptions.GetTextFormattingMode(this));
        window.SourceInitialized += (_, _) =>
        {
            pictureInPictureHandle = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(pictureInPictureHandle)?.AddHook(VideoHostWindowMessage);
        };
        // Closing the small window, for example with Alt+F4, returns the video to the full player.
        window.Closing += (_, e) => { if (!closing) { e.Cancel = true; SetPictureInPicture(false); } };
        return pictureInPictureWindow = window;
    }
    // A 16:9 player in the bottom-right corner of the main window's monitor; it then takes the video's shape.
    private Rect DefaultPictureInPictureBounds()
    {
        var work = MonitorWorkArea(this);
        const double width = 480, height = 270, margin = 24;
        return new Rect(work.Right - width - margin, work.Bottom - height - margin, width, height);
    }
    private static Rect MonitorWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor) || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
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
        if (!pictureInPicture || pictureInPictureWindow is not { } window) return;
        var aspect = VideoAspect();
        if (Math.Abs(aspect - pictureInPictureAspect) < 0.01) return;
        pictureInPictureAspect = aspect;
        if (aspect >= 1) { window.MinHeight = PictureInPictureMinSide; window.MinWidth = PictureInPictureMinSide * aspect; }
        else { window.MinWidth = PictureInPictureMinSide; window.MinHeight = PictureInPictureMinSide / aspect; }
        var work = MonitorWorkArea(window);
        var width = Math.Max(window.MinWidth, Math.Sqrt(window.Width * window.Height * aspect));
        var height = width / aspect;
        var left = window.Left + window.Width / 2 > work.Left + work.Width / 2 ? window.Left + window.Width - width : window.Left;
        var top = window.Top + window.Height / 2 > work.Top + work.Height / 2 ? window.Top + window.Height - height : window.Top;
        window.Left = left; window.Top = top; window.Width = width; window.Height = height;
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
        foreach (var property in new[] { ContentControl.ContentProperty, UIElement.IsEnabledProperty, FrameworkElement.ToolTipProperty })
            PictureInPicturePlayButton.SetBinding(property, new System.Windows.Data.Binding(property.Name) { Source = PlayButton });
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(UpNextCard, (_, _) => RefreshPictureInPictureUpNext());
    }
    // The up-next strip follows the full player's card. It has its own Play now button, so the centre one makes way.
    private void RefreshPictureInPictureUpNext()
    {
        var offered = pictureInPicture && UpNextCard.Visibility == Visibility.Visible;
        PictureInPictureUpNext.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        PictureInPicturePlayButton.Visibility = offered ? Visibility.Collapsed : Visibility.Visible;
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
        SendMessage(pictureInPictureHandle, 0x00A1 /* WM_NCLBUTTONDOWN */, new IntPtr(hit), IntPtr.Zero);
    }
    // LibVLC moves its video windows to a new size only when it next draws a frame, so on its own the picture trails
    // a resize and black shows at the moving edges. Sizing its windows with this one straight away lets Direct3D stretch
    // the current frame over them until LibVLC draws at the new size. The window keeps the video's shape, so the stretch
    // never distorts the picture. LibVLC's windows belong to its own thread, so their new size is posted, not awaited.
    private void FollowPictureInPictureSize(IntPtr window, IntPtr size)
    {
        int width = (int)(size.ToInt64() & 0xffff), height = (int)((size.ToInt64() >> 16) & 0xffff);
        if (width <= 0 || height <= 0) return;
        IntPtr host = IntPtr.Zero, video = IntPtr.Zero, output = IntPtr.Zero;
        var name = new System.Text.StringBuilder(64);
        EnumChildWindows(window, (child, _) =>
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
    // in the window: while the window grows, Windows paints the newly exposed part of that control in the light dialog
    // colour before the video catches up, so it is painted black instead.
    private IntPtr VideoHostWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var small = pictureInPicture && hwnd == pictureInPictureHandle;
        if (message == 0x0214 /* WM_SIZING */ && small && pictureInPictureAspect > 0) KeepPictureInPictureAspect(wParam.ToInt32(), lParam);
        if (message == 0x0005 /* WM_SIZE */ && small) FollowPictureInPictureSize(hwnd, lParam);
        // WINDOWPOS.flags follows two handles and four ints; without SWP_NOZORDER the window changed its place in the stack.
        if (message == 0x0047 /* WM_WINDOWPOSCHANGED */ && small && (Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 16) & 0x0004) == 0)
            Dispatcher.BeginInvoke(RaisePlayerOverlay);
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
