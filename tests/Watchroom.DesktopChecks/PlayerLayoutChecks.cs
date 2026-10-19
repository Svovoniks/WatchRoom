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
        var queueMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("PlayerQueueMenu");
        var queueMenuContent = (FrameworkElement)queueMenu.Child;
        var queuePill = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("QueuePill");
        var page = (Grid)window.FindName("RoomPage");
        var video = (FrameworkElement)window.FindName("Video");
        // Enough queue rows to exercise the queue menu's worst case.
        ((ListBox)window.FindName("QueueList")).ItemsSource = Enumerable.Range(1, 40).Select(i => new { QueueTitle = "Fixture video " + i }).ToArray();
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
        Check(previousButton.Visibility == Visibility.Visible && nextButton.Visibility == Visibility.Visible && !previousButton.IsEnabled && nextButton.IsEnabled,
            "without a queue, previous and next step through the playing show's episodes");
        var queue = (List<Watchroom.Core.MediaItem>)typeof(MainWindow).GetField("queue", flags)!.GetValue(window)!;
        queue.Add(episode); Invoke(window, "UpdateSessionControls");
        Check(previousButton.Visibility == Visibility.Visible && nextButton.Visibility == Visibility.Visible && episodeButton.Visibility == Visibility.Visible,
            "an active queue shows queue navigation while preserving the episode action");
        queue.Clear(); typeof(MainWindow).GetField("items", flags)!.SetValue(window, new List<Watchroom.Core.MediaItem> { episode });
        Invoke(window, "UpdateSessionControls");
        Check(episodeButton.Visibility == Visibility.Visible && episodeButton.Content.ToString() == "Finish ✓",
            "the final episode has a persistent Finish action");
        Check(previousButton.Visibility == Visibility.Visible && nextButton.Visibility == Visibility.Visible && !previousButton.IsEnabled && !nextButton.IsEnabled,
            "previous and next stay in the player bar, disabled, when there is nothing to step to");
        Check(IsDescendant(episodeButton, (Border)window.FindName("PlaybackControls")), "episode action belongs to the bottom player bar");
        void Layout(int width, int height)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        var queueList = (ListBox)window.FindName("QueueList");
        var queueActions = (FrameworkElement)window.FindName("QueueEditActions");
        var playQueue = (Button)window.FindName("PlayPlayerQueueButton");
        // The playing episode would list its show in place of the queue; these checks cover the queue itself.
        typeof(MainWindow).GetField("playingItem", flags)!.SetValue(window, null); Invoke(window, "UpdateSessionControls");
        var populatedQueue = new[] { episode, following };
        void LayoutMenu()
        {
            queueMenuContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            queueMenuContent.Arrange(new Rect(queueMenuContent.DesiredSize)); queueMenuContent.UpdateLayout();
        }
        foreach (var name in new[] { "AudioTracks", "SubtitleTracks" })
        {
            var picker = (ListBox)window.FindName(name);
            picker.ItemsSource = new[] { new { Label = "English" } }; picker.SelectedIndex = 0;
        }
        var audioPill = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("AudioPill");
        Layout(900, 600); window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Check(audioPill.Content is StackPanel { Children: [_, TextBlock { Text: "English" }, ..] }, "the audio pill shows the selected track");
        queueList.ItemsSource = Array.Empty<string>(); Invoke(window, "UpdateSessionControls"); LayoutMenu();
        Check(queueList.Visibility == Visibility.Collapsed && queueActions.Visibility == Visibility.Collapsed &&
            playQueue.Visibility == Visibility.Collapsed && queueMenuContent.ActualHeight < 200,
            "empty player queue menu hides its blank list and inactive actions");
        Save(queueMenuContent, "player-queue-menu-empty.png");
        queueList.ItemsSource = populatedQueue; Invoke(window, "UpdateSessionControls"); LayoutMenu();
        Check(queueList.Visibility == Visibility.Visible && queueActions.Visibility == Visibility.Visible &&
            playQueue.Visibility == Visibility.Visible && queueList.ActualHeight <= queueList.MaxHeight,
            "populating the player queue restores its list and controls within a bounded menu");
        Save(queueMenuContent, "player-queue-menu.png");
        // Long-running shows list hundreds of episodes; only the rows in view may be built.
        var longSeason = Enumerable.Range(1, 1500).Select(i => new { QueueTitle = $"S01 · E{i:0000} · Episode {i}" }).ToArray();
        int Realized(ItemsControl list, int count) => Enumerable.Range(0, count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) is not null);
        queueList.ItemsSource = longSeason; LayoutMenu();
        var realizedRows = Realized(queueList, longSeason.Length);
        Check(VirtualizingPanel.GetIsVirtualizing(queueList) && realizedRows is > 0 and < 40, $"a 1500-episode queue menu builds only its visible rows ({realizedRows})");
        queueList.ScrollIntoView(longSeason[^1]); LayoutMenu();
        Check(queueList.ItemContainerGenerator.ContainerFromIndex(longSeason.Length - 1) is not null && Realized(queueList, longSeason.Length) < 40,
            "the queue menu scrolls to the last of 1500 episodes while staying virtualized");
        queueList.ItemsSource = populatedQueue; Invoke(window, "UpdateSessionControls"); LayoutMenu();
        Invoke(window, "ShowPage", "Queues"); Layout(1280, 810);
        var savedItems = (ListBox)window.FindName("SavedQueueItems"); var savedSource = savedItems.ItemsSource;
        savedItems.ItemsSource = longSeason; Layout(1280, 810);
        var savedRealized = Realized(savedItems, longSeason.Length);
        Check(savedRealized is > 0 and < 60, $"the queue editor builds only its visible rows for a 1500-video queue ({savedRealized})");
        savedItems.ItemsSource = savedSource; Invoke(window, "ShowPage", "Room");
        // The playback tick refreshes the player chrome four times a second; keep that cheap with a long queue whose files are offline.
        var longQueue = Enumerable.Range(1, 1500).Select(i => episode with { Id = "long-" + i, Path = Path.Combine(App.DataDirectory, $"offline-{i}.mp4"), Episode = i }).ToList();
        var activeQueue = (List<Watchroom.Core.MediaItem>)typeof(MainWindow).GetField("queue", flags)!.GetValue(window)!;
        activeQueue.AddRange(longQueue); typeof(MainWindow).GetField("queuePosition", flags)!.SetValue(window, 700);
        typeof(MainWindow).GetField("playingItem", flags)!.SetValue(window, longQueue[700]);
        Invoke(window, "PublishQueue"); LayoutMenu();
        var refresh = typeof(MainWindow).GetMethod("UpdateSessionControls", flags)!;
        refresh.Invoke(window, []);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 200; i++) refresh.Invoke(window, []);
        var tickMs = timer.Elapsed.TotalMilliseconds / 200;
        Console.WriteLine($"Player chrome refresh with a 1500-video offline queue: {tickMs:F3} ms per tick");
        Check(tickMs < 2, $"the per-tick player refresh stays cheap with a long queue of offline videos ({tickMs:F3} ms)");
        timer.Restart(); var steps = 0;
        for (var i = 0; i < longQueue.Count; i += 50, steps++) { queueList.ScrollIntoView(queueList.Items[i]); LayoutMenu(); }
        var scrollMs = timer.Elapsed.TotalMilliseconds / steps;
        Console.WriteLine($"Queue menu scroll step across 1500 rows: {scrollMs:F2} ms");
        Check(scrollMs < 25, $"scrolling the 1500-row queue menu lays out quickly ({scrollMs:F2} ms per step)");
        activeQueue.Clear(); typeof(MainWindow).GetField("queuePosition", flags)!.SetValue(window, -1);
        typeof(MainWindow).GetField("playingItem", flags)!.SetValue(window, null);
        Invoke(window, "PublishQueue"); queueList.ItemsSource = populatedQueue; Invoke(window, "UpdateSessionControls"); LayoutMenu();
        var queuePicker = (ComboBox)window.FindName("PlayerQueuePicker");
        Check(VirtualizingPanel.GetIsVirtualizing(queuePicker) && ScrollViewer.GetCanContentScroll(queuePicker), "the player queue picker virtualizes its rows");
        foreach (var size in new[] { (900, 600), (1280, 810) })
        {
            Invoke(window, "ClosePlayerMenus"); Layout(size.Item1, size.Item2);
            var controls = (FrameworkElement)window.FindName("PlaybackControls");
            foreach (var pill in new[] { "AudioPill", "SubtitlePill", "QueuePill" })
            {
                var pillButton = (FrameworkElement)window.FindName(pill);
                var pillBounds = pillButton.TransformToAncestor(controls).TransformBounds(new Rect(new Point(), pillButton.RenderSize));
                Check(pillButton.Visibility == Visibility.Visible && pillBounds.Left >= 0 && pillBounds.Right <= controls.ActualWidth, pill + " fits in the player bar at " + size);
            }
            // Menus are popups placed above their pill, so opening one cannot resize the video or the bar.
            Check(queueMenuContent.Parent == queueMenu && queueMenu.PlacementTarget == queuePill && !IsDescendant(queueMenuContent, controls),
                "the queue menu floats above its pill outside the player layout at " + size);
            var overlay = (Grid)window.FindName("PlayerOverlay");
            ((Border)window.FindName("PlayerEmpty")).Visibility = Visibility.Collapsed;
            var episodeAction = (Button)window.FindName("EpisodeEndButton"); episodeAction.Visibility = Visibility.Visible;
            var overlaySize = new Size(video.ActualWidth, video.ActualHeight);
            overlay.Measure(overlaySize); overlay.Arrange(new Rect(new Point(), overlaySize)); overlay.UpdateLayout();
            // The bars float over the video, so they must stay within its overlay.
            var bounds = controls.TransformToAncestor(overlay).TransformBounds(new Rect(new Point(), controls.RenderSize));
            Check(bounds.Bottom <= overlay.ActualHeight + 1 && bounds.Width <= overlay.ActualWidth + 1, "transport controls stay inside the player at " + size);
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
        Check(!IsDescendant(library, queueMenuContent), "library access is outside the queue menu");
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
        var playerOverlay = (Grid)window.FindName("PlayerOverlay");
        Check(IsDescendant(header, playerOverlay) && IsDescendant(transport, playerOverlay),
            "both player bars float over the video in the window as well as in fullscreen");
        window.WindowStyle = WindowStyle.None;
        typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, true);
        Invoke(window, "ApplyPlayerLayout"); Invoke(window, "ShowPlayerControls"); Layout(900, 600);
        var fullscreenOverlay = (Grid)window.FindName("PlayerOverlay");
        fullscreenOverlay.Measure(new Size(video.ActualWidth, video.ActualHeight));
        fullscreenOverlay.Arrange(new Rect(0, 0, video.ActualWidth, video.ActualHeight)); fullscreenOverlay.UpdateLayout();
        var headerBounds = header.TransformToAncestor(fullscreenOverlay).TransformBounds(new Rect(new Point(), header.RenderSize));
        Check(header.Visibility == Visibility.Visible && headerBounds.Top == 0 && header.ActualWidth == fullscreenOverlay.ActualWidth,
            "fullscreen shows the original top bar across the video overlay");
        var caption = (Grid)window.FindName("CaptionBar");
        Check(caption.Visibility == Visibility.Collapsed && System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight == 0,
            "fullscreen video hides the caption row and leaves the top of the video clickable");
        Save(fullscreenOverlay, "player-fullscreen-bars.png");
        transport.Visibility = Visibility.Collapsed; Layout(900, 600);
        Check(header.Visibility == Visibility.Collapsed, "fullscreen header follows bottom controls after moving into the overlay");
        Invoke(window, "ShowPlayerControls");
        var disconnect = (Task)typeof(MainWindow).GetMethod("Disconnect", flags)!.Invoke(window, [true, false])!;
        Check(disconnect.IsCompletedSuccessfully && (bool)typeof(MainWindow).GetField("fullscreen", flags)!.GetValue(window)! &&
            window.WindowStyle == WindowStyle.None && IsDescendant(transport, playerOverlay) && IsDescendant(header, playerOverlay),
            "local media cleanup preserves fullscreen and both player bars during episode changes");
        typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, false);
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        Invoke(window, "ApplyPlayerLayout"); Layout(900, 600);
        Check(header.Visibility == Visibility.Visible && header.ActualHeight > 0, "leaving fullscreen restores the docked top bar");
        var updateCard = (Border)window.FindName("UpdateCard");
        Check(updateCard.Parent == window.FindName("PlayerUpdateHost"), "the update card shows in the video overlay so the video cannot cover it");
        Check(caption.Visibility == Visibility.Visible && System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight == 40 &&
            caption.Background is SolidColorBrush { Color: var captionColor } && captionColor == Color.FromRgb(0x17, 0x18, 0x1F),
            "the docked player keeps a dark caption row that matches its header");
        window.Close();
        Console.WriteLine("49 player layout and control checks passed.");
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
