using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BlackPearl.Desktop;

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
        // Focus moves into an opened menu. A menu dismissed by a click (outside or on a track) hands it back
        // to the player, or Space and the other shortcuts stop working.
        // Keyboard focus only reaches the menu while the window holding the player overlay is active.
        Window.GetWindow(overlay)?.Activate(); overlay.Focus();
        audioMenu.IsOpen = true; PumpUntil(Settled);
        var audioTracks = (ListBox)window.FindName("AudioTracks");
        if (!audioTracks.IsKeyboardFocusWithin) Keyboard.Focus(audioTracks);
        var focusedInMenu = audioMenu.Child.IsKeyboardFocusWithin;
        audioMenu.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        PumpUntil(() => !audioMenu.IsOpen);
        var stranded = Keyboard.FocusedElement is DependencyObject focused && audioMenu.Child.IsAncestorOf(focused);
        // Popups take keyboard focus only while the app is in the foreground, which a background check run is not.
        if (focusedInMenu) check(!stranded, "a track menu closed by a click gives keyboard focus back to the player, so Space still pauses");
        else Console.WriteLine("SKIP: menu focus hand-back (the app is not in the foreground, so focus cannot enter the menu)");
        var subtitleMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("SubtitleMenu");
        var queueMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("PlayerQueueMenu");
        audioMenu.IsOpen = true; PumpUntil(Settled);
        audioMenu.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false); // An outside click starts its fade.
        // A popup only takes the mouse when nothing else holds it. The fading menu must let go (StaysOpen)
        // so the new one can, or the new one never closes on an outside click.
        // Without Windows animation effects (as on build servers) the menu closes at once instead of fading.
        var releasedWhileFading = SystemParameters.ClientAreaAnimation ? audioMenu.IsOpen && audioMenu.StaysOpen : !audioMenu.IsOpen;
        subtitleMenu.IsOpen = true;
        var newMenuDismissable = !subtitleMenu.StaysOpen;
        PumpUntil(() => !audioMenu.IsOpen);
        check(releasedWhileFading && newMenuDismissable && subtitleMenu.IsOpen && !audioMenu.StaysOpen,
            $"a fading menu lets go of the mouse, so one opened meanwhile still closes on an outside click (animations {(SystemParameters.ClientAreaAnimation ? "on" : "off")})");
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
