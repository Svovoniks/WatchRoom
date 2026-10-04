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
        var overlay = (Grid)window.FindName("PlayerOverlay");
        Call("ShowPage", "Room"); fullscreen.SetValue(window, true);
        Call("ShowPlayerControls"); controls.Visibility = Visibility.Collapsed;
        Call("SetStatus", "Background buffer status");
        check(controls.Visibility == Visibility.Collapsed, "background status changes do not reveal idle player controls");
        Call("ControlsMouseMoved", controls, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        check(controls.Visibility == Visibility.Collapsed, "synthetic mouse events at the same position do not reveal controls");
        Call("PlayerMouseMoved", overlay, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
        check(controls.Visibility == Visibility.Collapsed, "overlay layout changes with a stationary cursor keep controls hidden");
        Call("ShowPlayerControls");
        check(controls.Visibility == Visibility.Visible, "explicit player interaction still reveals controls");
        fullscreen.SetValue(window, false); Call("ShowPage", "Library");
    }
}
