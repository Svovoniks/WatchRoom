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
        var mute = (Button)window.FindName("MuteButton"); var volumeText = (TextBlock)window.FindName("VolumeText");
        Check(mute.Content as string == "\uE995" && volumeText.Text == "100%", "a loud volume shows three speaker waves and its level under the speaker");
        volume.Value = 50; Check(mute.Content as string == "\uE994" && volumeText.Text == "50%", "a middle volume shows two speaker waves");
        volume.Value = 20; Check(mute.Content as string == "\uE993", "a quiet volume shows one speaker wave");
        volume.Value = 0; Check(mute.Content as string == "\uE992" && volumeText.Text == "0%", "zero volume shows a speaker without waves");
        Check(!IsDescendant(volume, (Border)window.FindName("PlaybackControls")) && !((System.Windows.Controls.Primitives.Popup)window.FindName("VolumeFlyout")).IsOpen,
            "the volume slider stays out of the bar in a closed flyout until it is needed");
        volume.Value = 100;
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
        var layoutQueues = (List<Watchroom.Core.SavedQueue>)typeof(MainWindow).GetField("savedQueues", flags)!.GetValue(window)!;
        layoutQueues.Add(new("layout-queue", "Layout queue", [])); Invoke(window, "OpenQueue", "layout-queue");
        Invoke(window, "ShowPage", "Queues"); Layout(1280, 810);
        // A tall poster behind the queue header must not set the header's height.
        var backdrop = (Border)window.FindName("QueueHeroBackdrop");
        var tallPoster = BitmapSource.Create(400, 1200, 96, 96, PixelFormats.Gray8, null, new byte[400 * 1200], 400);
        backdrop.Background = new ImageBrush(tallPoster) { Stretch = Stretch.UniformToFill }; Layout(1280, 810);
        var heroHeight = ((FrameworkElement)backdrop.Parent).ActualHeight;
        Check(heroHeight is > 100 and < 260, $"the queue header keeps its height with a tall poster behind it ({heroHeight:F0})");
        backdrop.ClearValue(Border.BackgroundProperty);
        var savedItems = (ListBox)window.FindName("SavedQueueItems"); var savedSource = savedItems.ItemsSource;
        savedItems.ItemsSource = longSeason; Layout(1280, 810);
        var savedRealized = Realized(savedItems, longSeason.Length);
        Check(savedRealized is > 0 and < 60, $"the queue editor builds only its visible rows for a 1500-video queue ({savedRealized})");
        var addPanel = (FrameworkElement)window.FindName("QueueAddPanel"); var addResults = (ListBox)window.FindName("QueueAddResults");
        addPanel.Visibility = Visibility.Visible; ((ColumnDefinition)window.FindName("QueueAddColumn")).Width = new GridLength(380); addResults.ItemsSource = longSeason; Layout(1280, 810);
        var addRealized = Realized(addResults, longSeason.Length);
        Check(addRealized is > 0 and < 60 && Realized(savedItems, longSeason.Length) < 60, $"Add videos builds only its visible results beside the queue ({addRealized})");
        addResults.ItemsSource = null; addPanel.Visibility = Visibility.Collapsed; ((ColumnDefinition)window.FindName("QueueAddColumn")).Width = new GridLength(0);
        savedItems.ItemsSource = savedSource; layoutQueues.RemoveAll(x => x.Id == "layout-queue"); Invoke(window, "ShowPage", "Room");
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
        // A small player folds the pills that don't fit, and the stop and picture-in-picture buttons, into the … pill.
        void Settle(int width, int height)
        {
            // The bars live in the video's overlay, which is laid out on its own at the video's size.
            var overlay = (Grid)window.FindName("PlayerOverlay");
            for (var pass = 0; pass < 3; pass++)
            {
                Layout(width, height);
                var size = new Size(video.ActualWidth, video.ActualHeight);
                overlay.Measure(size); overlay.Arrange(new Rect(size)); overlay.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        var bar = (FrameworkElement)window.FindName("PlaybackControls");
        var overflowPill = (FrameworkElement)window.FindName("OverflowPill");
        var overflowItems = (StackPanel)window.FindName("OverflowItems");
        var pills = (PillStrip)window.FindName("PlayerPills");
        bool InBar(FrameworkElement element)
        {
            var box = element.TransformToAncestor(bar).TransformBounds(new Rect(new Point(), element.RenderSize));
            return box.Left >= 0 && box.Right <= bar.ActualWidth + 0.5;
        }
        foreach (var width in new[] { 320, 400, 480, 560, 700 })
        {
            Invoke(window, "ClosePlayerMenus"); Settle(width, 360);
            var shown = new[] { "PlayButton", "PreviousButton", "NextButton", "MuteButton", "FullscreenButton", "OverflowPill", "AudioPill", "SubtitlePill", "QueuePill", "StopButton", "PictureInPictureButton", "PlaybackTime" }
                .Select(name => (FrameworkElement)window.FindName(name)).Where(element => element.Visibility == Visibility.Visible).ToList();
            Check(shown.All(InBar), $"every control shown in a {width}px player bar fits inside it");
            var folded = pills.Folded.Count; var stopHidden = ((Button)window.FindName("StopButton")).Visibility != Visibility.Visible;
            Check((folded > 0 || stopHidden) == (overflowPill.Visibility == Visibility.Visible) &&
                overflowItems.Children.OfType<Button>().Count() == folded + (stopHidden ? 1 : 0) + (((Button)window.FindName("PictureInPictureButton")).Visibility != Visibility.Visible ? 1 : 0),
                $"the … pill appears only when something is folded and lists each folded control at {width}px");
            if (width == 400) Save(bar, "player-bar-narrow.png");
        }
        Settle(320, 360);
        Check(pills.Folded.Count >= 2 && ((System.Windows.Controls.Primitives.Popup)window.FindName("SubtitleMenu")).PlacementTarget == overflowPill,
            "folded pills open their menus from the … pill in the narrowest player");
        Settle(1280, 810);
        Check(pills.Folded.Count == 0 && overflowPill.Visibility != Visibility.Visible && queueMenu.PlacementTarget == queuePill,
            "widening the player unfolds every pill and hides the … pill");
        // In a room, Participants joins the bar's pills and opens its own menu; outside a room it takes no space.
        var participantsPill = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("ParticipantsPill");
        var participantsMenu = (System.Windows.Controls.Primitives.Popup)window.FindName("ParticipantsMenu");
        // A closed popup has no visual tree yet, so membership follows the logical tree.
        bool InMenu(string name)
        {
            for (DependencyObject? node = (DependencyObject)window.FindName(name); node is not null; node = LogicalTreeHelper.GetParent(node)) if (node == participantsMenu) return true;
            return false;
        }
        Settle(1280, 810);
        Check(participantsPill.Visibility == Visibility.Collapsed && !pills.Folded.Contains(participantsPill), "outside a room the bar has no participants pill");
        PillStrip.SetShown(participantsPill, true); Settle(1280, 810);
        Check(participantsPill.Visibility == Visibility.Visible && InBar(participantsPill) && participantsMenu.PlacementTarget == participantsPill &&
            InMenu("PeopleList") && InMenu("VoiceCard") && InMenu("RoomFeedback"),
            "in a room the participants pill sits in the bar and its menu holds voice chat and the people list");
        Check(window.FindName("RoomSide") is null && window.FindName("RoomToggle") is null, "the side panel and its top bar button are gone");
        var peopleList = (ListBox)window.FindName("PeopleList");
        var rowHidden = Visibility.Collapsed; var rowShown = Visibility.Visible;
        peopleList.ItemsSource = new[]
        {
            new { Id = "a", Name = "svovoniks", Status = "Host · Ready", Voice = (object?)null, Actions = rowHidden, AdmitVisibility = rowHidden, AdmitLabel = "Admit", CanAdmit = true },
            new { Id = "b", Name = "Riley", Status = "Admitted · Ready", Voice = (object?)null, Actions = rowShown, AdmitVisibility = rowHidden, AdmitLabel = "Admit", CanAdmit = true },
            new { Id = "c", Name = "Sam", Status = "Waiting for approval", Voice = (object?)null, Actions = rowShown, AdmitVisibility = rowShown, AdmitLabel = "Admit", CanAdmit = true },
        };
        var participantsContent = (FrameworkElement)participantsMenu.Child;
        participantsContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        participantsContent.Arrange(new Rect(participantsContent.DesiredSize)); participantsContent.UpdateLayout();
        Check(participantsContent.ActualHeight is > 100 and < 600, $"the participants menu lays out as a compact card ({participantsContent.ActualHeight:F0} px)");
        Save(participantsContent, "player-participants-menu.png");
        peopleList.ItemsSource = null;
        Settle(400, 360);
        Check(pills.Folded.Contains(participantsPill) && participantsMenu.PlacementTarget == overflowPill &&
            overflowItems.Children.OfType<Button>().Any(row => System.Windows.Automation.AutomationProperties.GetName(row) == "Participants"),
            "a narrow bar folds the participants pill into the … menu");
        PillStrip.SetShown(participantsPill, false); Settle(900, 600);
        Check(window.FindName("RoomLibraryButton") is null && window.FindName("SavedRoomLibraryButton") is null, "the room library window has no buttons left");
        var invite = (Button)window.FindName("InviteButton"); invite.Visibility = Visibility.Visible;
        Layout(900, 600);
        var header = (FrameworkElement)window.FindName("RoomHeader");
        foreach (var name in new[] { "InviteButton", "LeaveButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(header).TransformBounds(new Rect(new Point(), button.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= header.ActualWidth + 1, name + " fits in the smallest room header");
        }
        Settle(320, 360);
        foreach (var name in new[] { "InviteButton", "LeaveButton", "PlayerCloseButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(header).TransformBounds(new Rect(new Point(), button.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= header.ActualWidth + 1, name + " fits in a 320px room header");
        }
        var titleBox = ((FrameworkElement)window.FindName("RoomHeading")).TransformToAncestor(header).TransformBounds(new Rect(new Point(), ((FrameworkElement)window.FindName("RoomHeading")).RenderSize));
        var inviteBox = invite.TransformToAncestor(header).TransformBounds(new Rect(new Point(), invite.RenderSize));
        Check(titleBox.Right <= inviteBox.Left, "the room buttons and window buttons leave the title its own space in a 320px header");
        Check(((Button)window.FindName("LeaveButton")).Content as string is "Close" or "Leave", "a narrow room header shortens its Leave button");
        Save(header, "player-header-narrow.png");
        // The shortest player window still keeps the top and bottom bars apart.
        Settle(700, 220);
        var shortOverlay = (Grid)window.FindName("PlayerOverlay");
        var headerBox = header.TransformToAncestor(shortOverlay).TransformBounds(new Rect(new Point(), header.RenderSize));
        var barBox = bar.TransformToAncestor(shortOverlay).TransformBounds(new Rect(new Point(), bar.RenderSize));
        Check(((FrameworkElement)window.FindName("RoomHeaderDetails")).Visibility == Visibility.Collapsed && headerBox.Bottom <= barBox.Top + 24 && barBox.Bottom <= shortOverlay.ActualHeight + 1,
            $"a short player tightens its bars so they stay apart (header ends {headerBox.Bottom:F0}, controls start {barBox.Top:F0}, video {shortOverlay.ActualHeight:F0})");
        Save(shortOverlay, "player-short.png");
        Settle(900, 600);
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
        Check(((FrameworkElement)window.FindName("PlayerCaptionButtons")).Visibility == Visibility.Collapsed, "fullscreen hides the window buttons in the top bar");
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
        var captionButtons = (FrameworkElement)window.FindName("PlayerCaptionButtons");
        var dockedOverlay = (Grid)window.FindName("PlayerOverlay");
        dockedOverlay.Measure(new Size(video.ActualWidth, video.ActualHeight)); dockedOverlay.Arrange(new Rect(0, 0, video.ActualWidth, video.ActualHeight)); dockedOverlay.UpdateLayout();
        var captionBox = captionButtons.TransformToAncestor(dockedOverlay).TransformBounds(new Rect(new Point(), captionButtons.RenderSize));
        Check(caption.Visibility == Visibility.Collapsed && System.Windows.Shell.WindowChrome.GetWindowChrome(window).CaptionHeight == 0 && video.ActualHeight == 600 &&
            captionButtons.Visibility == Visibility.Visible && captionBox.Top == 0 && Math.Abs(captionBox.Right - dockedOverlay.ActualWidth) < 1,
            "the docked player reaches the top of the window, with the window buttons in the top bar's corner");
        double Middle(string name) { var element = (FrameworkElement)window.FindName(name); return element.TransformToAncestor(dockedOverlay).TransformBounds(new Rect(new Point(), element.RenderSize)) is var box ? box.Top + box.Height / 2 : double.NaN; }
        Check(Math.Abs(Middle("LeaveButton") - Middle("PlayerCloseButton")) < 1 && Math.Abs(Middle("PlayerBackButton") - Middle("PlayerCloseButton")) < 1,
            $"the top bar's buttons line up with the window buttons ({Middle("PlayerBackButton"):F0}, {Middle("LeaveButton"):F0}, {Middle("PlayerCloseButton"):F0})");
        var backButton = (FrameworkElement)window.FindName("PlayerBackButton");
        var backBox = backButton.TransformToAncestor(dockedOverlay).TransformBounds(new Rect(new Point(), backButton.RenderSize));
        Check(Math.Abs(backBox.Left - backBox.Top) < 0.5, $"the back button sits as far from the left edge as from the top ({backBox.Left:F0}, {backBox.Top:F0})");
        Save((FrameworkElement)window.FindName("RoomHeader"), "player-header-docked.png");
        window.Close();
        Console.WriteLine("Player layout and control checks passed.");
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
