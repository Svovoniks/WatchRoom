using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Watchroom.Desktop;

static class PlayerInteractionChecks
{
    public static void Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        var fullscreen = typeof(MainWindow).GetField("fullscreen", flags)!;
        var controls = (Border)window.FindName("PlaybackControls");
        var header = (Border)window.FindName("RoomHeader");
        var overlay = (Grid)window.FindName("PlayerOverlay");
        Call("ShowPage", "Room"); fullscreen.SetValue(window, true);
        check(window.FindName("PlaybackTitleOverlay") is null, "player has no duplicate translucent title bar");
        Call("ShowPlayerControls"); controls.Visibility = Visibility.Collapsed;
        check(header.Visibility == Visibility.Collapsed, "top bar hides together with bottom controls");
        Call("ApplyPlayerLayout");
        check(header.Visibility == Visibility.Collapsed, "layout refresh preserves hidden player bars");
        Call("SetStatus", "Background buffer status");
        check(controls.Visibility == Visibility.Collapsed, "background status changes do not reveal idle player controls");
        Call("ControlsMouseMoved", controls, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        check(controls.Visibility == Visibility.Collapsed, "synthetic mouse events at the same position do not reveal controls");
        Call("ControlsMouseMoved", header, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        check(controls.Visibility == Visibility.Collapsed && header.Visibility == Visibility.Collapsed,
            "stationary top bar events keep both bars hidden");
        Call("PlayerMouseMoved", overlay, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        check(controls.Visibility == Visibility.Collapsed, "overlay layout changes with a stationary cursor keep controls hidden");
        Call("ShowPlayerControls");
        check(controls.Visibility == Visibility.Visible && header.Visibility == Visibility.Visible,
            "explicit player interaction reveals both player bars");
        var audioMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("AudioMenu");
        var audioPill = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("AudioPill");
        audioPill.IsChecked = true;
        check(audioMenu.IsOpen && !audioPill.IsHitTestVisible, "the audio pill opens its menu and ignores clicks until it closes");
        void PumpUntil(Func<bool> done)
        {
            var until = Environment.TickCount64 + 2000;
            while (!done() && Environment.TickCount64 < until)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame); Thread.Sleep(10);
            }
        }
        var menuContent = (UIElement)((ContentControl)((Decorator)audioMenu.Child).Child).Content;
        bool Settled() => audioMenu.Child.Opacity > 0.999 && audioMenu.Child.Clip is null && menuContent.Opacity > 0.999 && menuContent.RenderTransform.Value.IsIdentity;
        PumpUntil(Settled);
        check(Settled(), "an opened track menu finishes growing out of its pill fully opaque and unclipped");
        typeof(MainWindow).GetField("lastPlayerInteraction", flags)!.SetValue(window, Environment.TickCount64 - 3000);
        Call("PlaybackTick");
        check(controls.Visibility == Visibility.Visible, "an open track menu keeps fullscreen controls visible");
        Call("ClosePlayerMenus");
        PumpUntil(() => !audioMenu.IsOpen); // The menu fades out before its popup closes.
        check(!audioMenu.IsOpen && audioPill.IsChecked == false && audioPill.IsHitTestVisible,
            "closing player menus shrinks the track menu into its pill, then closes it and resets its pill");
        var subtitleMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("SubtitleMenu");
        var queueMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("PlayerQueueMenu");
        IInputElement? Root(System.Windows.Controls.Primitives.Popup menu) => PresentationSource.FromVisual(menu.Child)?.RootVisual as IInputElement;
        audioMenu.IsOpen = true; PumpUntil(Settled);
        audioMenu.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false); // An outside click starts its fade.
        subtitleMenu.IsOpen = true;
        var tookMouse = audioMenu.IsOpen && Root(subtitleMenu) is { } subtitleRoot && Mouse.Captured == subtitleRoot;
        PumpUntil(() => !audioMenu.IsOpen);
        check(tookMouse && subtitleMenu.IsOpen && Mouse.Captured == Root(subtitleMenu),
            "a menu opened while another fades out takes the mouse at once, so an outside click still closes it");
        queueMenu.IsOpen = true;
        PumpUntil(() => !subtitleMenu.IsOpen);
        check(queueMenu.IsOpen && !subtitleMenu.IsOpen && !audioMenu.IsOpen, "opening a player menu folds away the one that was open");
        Call("ClosePlayerMenus"); PumpUntil(() => !queueMenu.IsOpen);
        typeof(MainWindow).GetField("lastPlayerInteraction", flags)!.SetValue(window, Environment.TickCount64 - 3000);
        Call("PlaybackTick");
        check(controls.Visibility == Visibility.Collapsed && header.Visibility == Visibility.Collapsed,
            "fullscreen inactivity hides both player bars");
        Call("ShowPlayerControls");
        fullscreen.SetValue(window, false); Call("ShowPage", "Library");
    }
}
