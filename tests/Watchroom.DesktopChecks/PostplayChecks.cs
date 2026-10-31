using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Watchroom.Core;
using Watchroom.Desktop;

// The promo after a finished video plays once over the player, can be turned off in Settings,
// and holds back room synchronization until it ends.
static class PostplayChecks
{
    public static async Task Run(MainWindow window, LibraryStore store, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        bool Active() => (bool)typeof(MainWindow).GetProperty("PostplayActive", flags)!.GetValue(window)!;
        var layer = (Grid)window.FindName("PostplayLayer");
        var video = (MediaElement)window.FindName("PostplayVideo");
        var killSwitch = (CheckBox)window.FindName("ForceDisablePostplay");
        var previousShown = store.Setting("postplayShown"); var previousDisabled = store.Setting("postplayDisabled");
        try
        {
            Call("ShowPage", "Room");
            store.Setting("postplayShown", "false"); store.Setting("postplayDisabled", "true");
            var skipped = (Task)Call("PlayPostplayOnce", false)!;
            check(skipped.IsCompleted && !Active() && layer.Visibility == Visibility.Collapsed && store.Setting("postplayShown") == "false",
                "force disable postplay skips the promo without using up its one showing");

            store.Setting("postplayDisabled", "false");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var promo = (Task)Call("PlayPostplayOnce", false)!;
            check(!promo.IsCompleted && Active() && layer.Visibility == Visibility.Visible && store.Setting("postplayShown") == "true",
                "a finished video starts the promo over the player and records that it was shown");
            check(Panel.GetZIndex(layer) > Panel.GetZIndex((UIElement)window.FindName("UpNextCard")) && layer.Parent == window.FindName("PlayerOverlay"),
                "the promo covers the player controls and travels with the player into picture in picture");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Call("KeyPressed", window, key);
            check(key.Handled && Active(), "player keys do not control the video while the promo plays");

            // Voice chat lowers the promo while people talk, as it does the video.
            var duck = typeof(MainWindow).GetField("videoDuck", flags)!; var volume = ((Slider)window.FindName("Volume")).Value;
            duck.SetValue(window, 0.5); Call("ApplyPlayerVolume");
            check(Math.Abs(video.Volume - volume / 200) < .001, "voice chat ducking lowers the promo");
            duck.SetValue(window, 1.0); Call("ApplyPlayerVolume");

            var finished = await Task.WhenAny(promo, Task.Delay(45000));
            Console.WriteLine($"Promo played for {clock.Elapsed.TotalSeconds:F1} s");
            // The bundled promo is 22 seconds long; ending much sooner means it failed to open or stalled.
            check(finished == promo && clock.Elapsed.TotalSeconds > 20 && !Active() && layer.Visibility == Visibility.Collapsed && video.Source is null,
                "the promo plays to its end and then hands the player back");

            var again = (Task)Call("PlayPostplayOnce", false)!;
            check(again.IsCompleted && !Active(), "the promo plays only once, not after the next video");

            // The kill switch saves with the other settings and stops a promo that is showing.
            store.Setting("postplayShown", "false");
            promo = (Task)Call("PlayPostplayOnce", false)!;
            Call("ShowPage", "Settings"); Call("LoadPreferences");
            await Task.WhenAny(promo, Task.Delay(2000));
            check(!Active() && promo.IsCompleted, "leaving the player ends the promo");
            killSwitch.IsChecked = true;
            check((bool)Call("TrySavePreferences")! && store.Setting("postplayDisabled") == "true", "force disable postplay is saved from Settings");
            killSwitch.IsChecked = false; Call("TrySavePreferences");
            check(store.Setting("postplayDisabled") == "false", "turning the kill switch off is saved too");

            // Play promo in Settings shows it now, even when turned off and already shown, then returns to Settings.
            store.Setting("postplayShown", "true"); store.Setting("postplayDisabled", "true");
            ((Button)window.FindName("PlayPostplayButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (var wait = 0; wait < 20 && !Active(); wait++) await Task.Delay(50);
            check(Active() && layer.IsVisible && ((Grid)window.FindName("RoomPage")).Visibility == Visibility.Visible,
                "Play promo opens the player and plays the promo even when it is turned off");
            Call("EndPostplay", "check");
            for (var wait = 0; wait < 40 && ((Grid)window.FindName("SettingsPage")).IsVisible is false; wait++) await Task.Delay(50);
            check(!Active() && ((Grid)window.FindName("SettingsPage")).IsVisible && store.Setting("postplayShown") == "true",
                "after a promo from Settings the app returns to Settings");

            // In a room, a round started by the host plays the promo here even when it was seen already; joining only notes the round.
            store.Setting("postplayDisabled", "false");
            var roomField = typeof(MainWindow).GetField("room", flags)!; var previousRoom = roomField.GetValue(window);
            var client = new RoomClient(store);
            try
            {
                roomField.SetValue(window, client); Call("ShowPage", "Room");
                RoomSnapshot Round(long round) => new([], null, null, false, Postplay: new(["guest"], round));
                Call("ObservePostplayRound", Round(4));
                check(!Active(), "joining a room notes its promo round without playing it");
                Call("ObservePostplayRound", Round(5));
                check(Active() && layer.IsVisible, "a new promo round from the host plays the promo for everyone in the room");
                Call("EndPostplay", "check"); await Task.Delay(100);
                Call("ObservePostplayRound", Round(5));
                check(!Active(), "a promo round plays only once");
                store.Setting("postplayDisabled", "true");
                Call("ObservePostplayRound", Round(6));
                check(!Active(), "force disable postplay also skips the room's promo rounds");
            }
            finally { roomField.SetValue(window, previousRoom); await client.DisposeAsync(); }
        }
        finally
        {
            Call("EndPostplay", "check");
            store.Setting("postplayShown", previousShown ?? "true"); store.Setting("postplayDisabled", previousDisabled ?? "false");
            Call("ShowPage", "Library");
        }
    }
}
