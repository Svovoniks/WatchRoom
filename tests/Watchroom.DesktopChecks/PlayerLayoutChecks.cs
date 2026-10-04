using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Desktop;

static class PlayerLayoutChecks
{
    public static void Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var window = new MainWindow();
        Invoke(window, "ShowPage", "Room");
        var options = (Border)window.FindName("PlayerOptions");
        var scroll = (ScrollViewer)window.FindName("PlayerOptionsScroll");
        var page = (Grid)window.FindName("RoomPage");
        var video = (FrameworkElement)window.FindName("Video");
        // Enough queue rows to exercise the expanded panel's worst case.
        ((ListBox)window.FindName("QueueList")).ItemsSource = Enumerable.Range(1, 40).Select(i => "Fixture video " + i).ToArray();
        var root = (FrameworkElement)window.Content;
        var volume = (Slider)window.FindName("Volume");
        System.Windows.Input.MouseWheelEventArgs Wheel(UIElement element, int delta, bool preview = true)
        {
            var args = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta)
                { RoutedEvent = preview ? System.Windows.Input.Mouse.PreviewMouseWheelEvent : System.Windows.Input.Mouse.MouseWheelEvent };
            element.RaiseEvent(args); return args;
        }
        volume.Value = 50;
        Check(Wheel(volume, 120).Handled && volume.Value == 55, "wheel over volume increases volume and consumes the event");
        Wheel(volume, -60);
        Check(volume.Value == 52.5, "volume wheel preserves partial trackpad deltas");
        var videoOverlay = (Grid)window.FindName("PlayerOverlay");
        Check(Wheel(videoOverlay, -120, false).Handled && volume.Value == 47.5, "wheel over video adjusts volume");
        volume.Value = 99; Wheel(volume, 120);
        Check(volume.Value == 100, "wheel volume stays within its allowed range");
        var timeline = (Slider)window.FindName("Timeline");
        Check(Wheel(timeline, 120).Handled && volume.Value == 100 && timeline.Value == 0,
            "timeline wheel is consumed independently and ignores an unloaded video");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var episode = new Watchroom.Core.MediaItem("episode-one", Path.Combine(App.DataDirectory, "episode-one.mp4"), "Example", "Show",
            Series: "Example", Season: 1, Episode: 1, ShowId: "fixture-show");
        var following = episode with { Id = "episode-two", Path = Path.Combine(App.DataDirectory, "episode-two.mp4"), Season = 2 };
        File.WriteAllBytes(following.Path, []);
        typeof(MainWindow).GetField("playingItem", flags)!.SetValue(window, episode);
        typeof(MainWindow).GetField("items", flags)!.SetValue(window, new List<Watchroom.Core.MediaItem> { episode, following });
        Invoke(window, "UpdateSessionControls");
        var episodeButton = (Button)window.FindName("EpisodeEndButton");
        Check(episodeButton.Visibility == Visibility.Visible && episodeButton.Content.ToString() == "Next episode →",
            "next episode remains in the player bar before the video nears its end and crosses seasons");
        var previousButton = (Button)window.FindName("PreviousButton"); var nextButton = (Button)window.FindName("NextButton");
        Check(previousButton.Visibility == Visibility.Collapsed && nextButton.Visibility == Visibility.Collapsed,
            "queue navigation is hidden when there is no queue");
        var queue = (List<Watchroom.Core.MediaItem>)typeof(MainWindow).GetField("queue", flags)!.GetValue(window)!;
        queue.Add(episode); Invoke(window, "UpdateSessionControls");
        Check(previousButton.Visibility == Visibility.Visible && nextButton.Visibility == Visibility.Visible && episodeButton.Visibility == Visibility.Visible,
            "an active queue shows queue navigation while preserving the episode action");
        queue.Clear(); typeof(MainWindow).GetField("items", flags)!.SetValue(window, new List<Watchroom.Core.MediaItem> { episode });
        Invoke(window, "UpdateSessionControls");
        Check(episodeButton.Visibility == Visibility.Visible && episodeButton.Content.ToString() == "Finish ✓",
            "the final episode has a persistent Finish action");
        Check(IsDescendant(episodeButton, (Border)window.FindName("PlaybackControls")), "episode action belongs to the bottom player bar");
        void Layout(int width, int height)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        foreach (var size in new[] { (900, 600), (1280, 810) })
        {
            options.Visibility = Visibility.Collapsed; Layout(size.Item1, size.Item2);
            var closedHeight = video.ActualHeight;
            Invoke(window, "TogglePlayerOptions", window, new RoutedEventArgs()); Layout(size.Item1, size.Item2);
            Check(options.Visibility == Visibility.Visible && scroll.ViewportHeight > 0, "playback options open at " + size);
            Check(video.ActualHeight >= 180 && video.ActualHeight < closedHeight, "expanded options preserve a usable video area at " + size);
            Check(scroll.ScrollableHeight > 0, "long playback options scroll at " + size);
            var controls = (FrameworkElement)window.FindName("PlaybackControls");
            var bounds = controls.TransformToAncestor(page).TransformBounds(new Rect(new Point(), controls.RenderSize));
            Check(bounds.Bottom <= page.ActualHeight + 1, "transport controls stay inside the player at " + size);
            if (size.Item1 == 900) Save(root, "player-options.png");
            Invoke(window, "TogglePlayerOptions", window, new RoutedEventArgs()); Layout(size.Item1, size.Item2);
            Check(options.Visibility == Visibility.Collapsed && Math.Abs(video.ActualHeight - closedHeight) < 1, "closing options restores video size at " + size);
            var overlay = (Grid)window.FindName("PlayerOverlay");
            ((Border)window.FindName("PlayerEmpty")).Visibility = Visibility.Collapsed;
            var episodeAction = (Button)window.FindName("EpisodeEndButton"); episodeAction.Visibility = Visibility.Visible;
            var overlaySize = new Size(video.ActualWidth, video.ActualHeight);
            overlay.Measure(overlaySize); overlay.Arrange(new Rect(new Point(), overlaySize)); overlay.UpdateLayout();
            var actionBounds = episodeAction.TransformToAncestor(controls).TransformBounds(new Rect(new Point(), episodeAction.RenderSize));
            Check(actionBounds.Left >= 0 && actionBounds.Right <= controls.ActualWidth && actionBounds.Bottom <= controls.ActualHeight,
                "episode action fits inside the bottom player bar at " + size);
            var timelineBounds = timeline.TransformToAncestor(controls).TransformBounds(new Rect(new Point(), timeline.RenderSize));
            Check(timeline.ActualWidth > 100 && timelineBounds.Right <= actionBounds.Left,
                "episode action leaves the playback slider usable at " + size);
            if (size.Item1 == 900) Save(root, "episode-end-action.png");
            episodeAction.Visibility = Visibility.Collapsed;
        }
        var library = (Button)window.FindName("RoomLibraryButton");
        Check(!IsDescendant(library, options), "library access is outside collapsed playback options");
        Check(window.FindName("SavedRoomLibraryButton") is Button, "Rooms settings provide a library access entry");
        library.Visibility = Visibility.Visible; library.Content = "Library & guest access";
        ((Button)window.FindName("RoomToggle")).Visibility = Visibility.Visible;
        ((Button)window.FindName("InviteButton")).Visibility = Visibility.Visible;
        Layout(900, 600);
        var header = (FrameworkElement)window.FindName("RoomHeader");
        foreach (var name in new[] { "RoomLibraryButton", "RoomToggle", "InviteButton", "LeaveButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(header).TransformBounds(new Rect(new Point(), button.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= header.ActualWidth + 1, name + " fits in the smallest room header");
        }
        Save(root, "player-library-access.png");
        var movie = episode with { Id = "movie-finish", Series = null, Season = null, Episode = null, ShowId = null, Kind = "Movie" };
        typeof(MainWindow).GetField("playingItem", flags)!.SetValue(window, movie);
        Invoke(window, "UpdateSessionControls");
        Check(episodeButton.Visibility == Visibility.Visible && episodeButton.Content.ToString() == "Finish ✓" &&
            System.Windows.Automation.AutomationProperties.GetName(episodeButton) == "Finish movie",
            "movies expose a Finish action in the player bar");
        var transport = (Border)window.FindName("PlaybackControls");
        var docked = (Grid)window.FindName("DockedControlsHost"); var fullscreenHost = (Grid)window.FindName("FullscreenControlsHost");
        docked.Children.Remove(transport); fullscreenHost.Children.Add(transport);
        window.WindowStyle = WindowStyle.None;
        typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, true);
        var disconnect = (Task)typeof(MainWindow).GetMethod("Disconnect", flags)!.Invoke(window, [true, false])!;
        Check(disconnect.IsCompletedSuccessfully && (bool)typeof(MainWindow).GetField("fullscreen", flags)!.GetValue(window)! &&
            window.WindowStyle == WindowStyle.None && fullscreenHost.Children.Contains(transport),
            "local media cleanup preserves fullscreen and its transport controls during episode changes");
        typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, false);
        fullscreenHost.Children.Remove(transport); docked.Children.Add(transport); window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.Close();
        Console.WriteLine("32 player layout and control checks passed.");
    }
    static bool IsDescendant(DependencyObject child, DependencyObject parent)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current)) if (current == parent) return true;
        return false;
    }
    static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    static void Save(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/library-ui"); using var output = File.Create(Path.Combine("artifacts/library-ui", name)); encoder.Save(output);
    }
}
