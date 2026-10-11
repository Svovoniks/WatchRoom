using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        typeof(MainWindow).GetField("lastPlayerInteraction", flags)!.SetValue(window, Environment.TickCount64 - 3000);
        ((Border)window.FindName("PlayerOptions")).Visibility = Visibility.Collapsed;
        Call("PlaybackTick");
        check(controls.Visibility == Visibility.Collapsed && header.Visibility == Visibility.Collapsed,
            "fullscreen inactivity hides both player bars");
        Call("ShowPlayerControls");
        fullscreen.SetValue(window, false); Call("ShowPage", "Library");
    }
}
