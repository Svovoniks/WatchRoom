using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Watchroom.Core;
using Watchroom.Desktop;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--metadata-probe") { MetadataProbe.Run(args[1], args[2]).GetAwaiter().GetResult(); return; }
        if (args.FirstOrDefault() == "--remaining-audit") { MetadataProbe.Audit(args[1], args[2], args.Contains("--apply")).GetAwaiter().GetResult(); return; }
        var directory = Path.GetFullPath(Path.Combine("artifacts/library-responsiveness-check", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("WATCHROOM_DATA", directory);
        Environment.SetEnvironmentVariable("WATCHROOM_DISABLE_UPDATES", "1");
        var store = new LibraryStore(directory); store.Setting("artwork", "false");
        store.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(directory) }));
        var entries = Enumerable.Range(0, 750).Select(index => new MediaItem("item-" + index, Path.Combine(directory, "movie-" + index + ".mp4"), $"Movie {index:0000}", "Movie")).ToArray();
        foreach (var entry in entries) store.Save(entry);
        var app = new Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var resourceXml = System.Xml.Linq.XDocument.Load("src/Watchroom.Desktop/App.xaml");
        var resourceRoot = new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"), resourceXml.Root!.Elements().Single().Elements());
        resourceRoot.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resourceRoot.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:Watchroom.Desktop;assembly=Watchroom");
        foreach (var element in resourceRoot.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:Watchroom.Desktop"))
            element.Name = System.Xml.Linq.XName.Get(element.Name.LocalName, "clr-namespace:Watchroom.Desktop;assembly=Watchroom");
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resourceRoot.ToString());
        var window = new MainWindow { Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        try
        {
            Check(MetadataCredential.Load(directory) == "", "new profile has no saved token");
            MetadataCredential.Save(directory, "fixture-token-one");
            Check(MetadataCredential.Load(directory) == "fixture-token-one", "credential vault restores a saved token");
            MetadataCredential.Save(directory, "fixture-token-two");
            Check(MetadataCredential.Load(directory) == "fixture-token-two", "credential vault replaces a saved token");
            MetadataCredential.Save(directory, "");
            Check(MetadataCredential.Load(directory) == "", "clearing the token removes the saved credential");
        }
        finally { MetadataCredential.Save(directory, ""); }
        window.Loaded += async (_, _) =>
        {
            try
            {
                typeof(MainWindow).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Library"]);
                await Task.Delay(2500);
                await CheckScrolling(Check);
                var field = typeof(MainWindow).GetField("libraryCards", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var cards = (ObservableCollection<LibraryCardView>)field.GetValue(window)!;
                Check(cards.Count == entries.Length, "large library is displayed");
                var previous = cards.ToArray();
                await CheckSearchLayout(window, Check);
                var tracker = new LibraryCardView(new("Example", "Movie", null, entries[0], "movie"));
                var properties = new List<string>(); tracker.PropertyChanged += (_, e) => properties.Add(e.PropertyName!);
                tracker.Update(tracker.Card with { Media = entries[0] with { Overview = "New metadata" } });
                Check(properties.SequenceEqual(["Card"]), "metadata-only update keeps poster and displayed text bindings intact");
                var posterPath = Path.Combine(directory, "thumbnail.png");
                await File.WriteAllBytesAsync(posterPath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII="));
                tracker.Update(tracker.Card with { Poster = posterPath });
                await tracker.LoadPosterAsync();
                Check(tracker.Poster?.IsFrozen == true, "background thumbnail decode returns a frozen WPF image");
                var frozen = tracker.Poster; await tracker.LoadPosterAsync();
                Check(ReferenceEquals(frozen, tracker.Poster), "unchanged image is reused");
                var dead = new LibraryCardView(tracker.Card with { Poster = Path.Combine(directory, "missing.png") });
                await dead.LoadPosterAsync(); await dead.LoadPosterAsync();
                Check(dead.Poster is null, "missing poster keeps fallback without reloading forever");
                await File.WriteAllBytesAsync(dead.Card.Poster!, await File.ReadAllBytesAsync(posterPath));
                dead.Update(dead.Card); await dead.LoadPosterAsync();
                Check(dead.Poster?.IsFrozen == true, "poster created at the same path retries after an earlier load failure");
                File.SetLastWriteTimeUtc(posterPath, File.GetLastWriteTimeUtc(posterPath).AddSeconds(2));
                tracker.Update(tracker.Card); await tracker.LoadPosterAsync();
                Check(tracker.Poster is not null && !ReferenceEquals(frozen, tracker.Poster), "replaced image at the same path invalidates the WPF thumbnail cache");
                var retained = tracker.Poster;
                var replacementPath = Path.Combine(directory, "replacement.png");
                await File.WriteAllBytesAsync(replacementPath, await File.ReadAllBytesAsync(posterPath));
                var blankFrames = 0;
                tracker.PropertyChanged += (_, e) => { if (e.PropertyName == "Poster" && tracker.Poster is null) blankFrames++; };
                tracker.Update(tracker.Card with { Poster = replacementPath });
                Check(ReferenceEquals(retained, tracker.Poster), "artwork refresh retains old image while replacement decodes");
                await tracker.LoadPosterAsync();
                Check(blankFrames == 0 && tracker.Poster is not null, "successful artwork replacement never publishes a blank frame");
                retained = tracker.Poster;
                tracker.Update(tracker.Card with { Poster = Path.Combine(directory, "failed-replacement.png") }); await tracker.LoadPosterAsync();
                Check(ReferenceEquals(retained, tracker.Poster), "failed artwork replacement keeps the previous image");
                var episodeView = new LibraryCardView(new("Episode 1", "Example", posterPath, entries[0] with { Series = "Example", Season = 1, Episode = 1, RuntimeMinutes = 20 }, "episode"));
                episodeView.SetPlayback(600000, false);
                Check(episodeView.Progress == 50 && episodeView.PlaybackLabel == "Resume · 10:00", "episode playback progress uses runtime and saved resume point");
                episodeView.SetPlayback(0, true);
                Check(episodeView.Progress == 100 && episodeView.PlaybackLabel == "Watched", "completed episode retains watched progress");
                var episodeButton = new System.Windows.Controls.Button { Style = (Style)app.Resources["PosterCard"], DataContext = episodeView };
                episodeButton.Measure(new Size(600, 600)); await Dispatcher.Yield(DispatcherPriority.DataBind);
                Check(episodeButton.Width == 300 && episodeButton.Height == 278, "episode cards use landscape layout without changing movie cards");

                var gaps = new List<double>(); var clock = Stopwatch.StartNew(); var prior = clock.Elapsed.TotalMilliseconds;
                var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
                heartbeat.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; gaps.Add(now - prior); prior = now; };
                heartbeat.Start();
                var activeStore = (LibraryStore)typeof(MainWindow).GetField("library", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                await Task.Run(() => { foreach (var entry in entries) activeStore.Save(entry with { Overview = "Updated during scan" }); });
                await Task.Delay(1500); heartbeat.Stop();
                Check(cards.Count == previous.Length && cards.Zip(previous).All(pair => ReferenceEquals(pair.First, pair.Second)),
                    "scan updates preserve all existing card instances");
                Check(cards.All(card => card.Card.Media.Overview == "Updated during scan"), "incremental metadata reaches every card");
                var grid = (System.Windows.Controls.ItemsControl)window.FindName("PosterGrid");
                Check(grid.IsEnabled, "background refresh leaves the library interactive");
                typeof(MainWindow).GetMethod("FilterLibrary", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [false, false]);
                Check(grid.IsEnabled, "full refresh never dims or disables the poster grid");
                Console.WriteLine($"Observed {gaps.Count} input heartbeats; maximum gap {(gaps.Count == 0 ? 0 : gaps.Max()):F1} ms.");
                Check(gaps.Count > 50 && gaps.Max() < 250, "dispatcher input continues during 750 scan updates");
                await CheckLibraryNavigation(window, activeStore, cards, directory, posterPath, Check);
                CheckRoomSettings(window, activeStore, Check);
                CheckQueueEditing(window, activeStore, Check);
                await CheckBackNavigation(window, Check);
                Console.WriteLine($"Input heartbeat: maximum {gaps.Max():F1} ms; p95 {gaps.Order().ElementAt((int)(gaps.Count * .95)):F1} ms");
                Console.WriteLine($"{checks} desktop responsiveness checks passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
    }

    private static async Task CheckLibraryNavigation(MainWindow window, LibraryStore store, ObservableCollection<LibraryCardView> cards,
        string directory, string poster, Action<bool, string> check)
    {
        var show = new MediaItem("navigation-show", Path.Combine(directory, "Show S01E01.mkv"), "Navigation Show", "Show", Series: "Navigation Show", Season: 1, Episode: 1, EpisodeTitle: "The Beginning");
        var special = show with { Id = "navigation-special", Path = Path.Combine(directory, "Show S00E01.mkv"), Season = 0 };
        var unknown = show with { Id = "navigation-unknown", Path = Path.Combine(directory, "Unknown episode.mkv"), Season = null };
        var anime = show with { Id = "navigation-anime", Path = Path.Combine(directory, "Anime S01E01.mkv"), Kind = "Anime", Series = "Navigation Anime", Title = "Navigation Anime" };
        var animeMovie = new MediaItem("navigation-anime-film", Path.Combine(directory, "Anime film.mkv"), "Navigation Anime Film", "Anime", Poster: poster, Available: false);
        foreach (var item in new[] { show, special, unknown, anime, animeMovie }) store.Save(item);
        await Task.Delay(600);
        void Call(string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
        void Navigate(int category, string? series = null, string? kind = null, int? season = null) => Call("NavigateLibrary", category, series, kind, season);
        async Task Wait(Func<bool> ready)
        {
            var until = Stopwatch.StartNew();
            while (!ready() && until.ElapsedMilliseconds < 4000) await Task.Delay(25);
            check(ready(), "navigation finishes with the expected library cards");
        }
        LibraryBreadcrumb[] Crumbs(bool details = false) => ((System.Windows.Controls.ItemsControl)window.FindName(details ? "DetailBreadcrumbs" : "LibraryBreadcrumbs")).Items.Cast<LibraryBreadcrumb>().ToArray();
        void ClickCrumb(LibraryBreadcrumb crumb) => Call("BreadcrumbClick", window,
            new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent, new System.Windows.Controls.Button { DataContext = crumb }));
        Navigate(3); await Wait(() => cards.Count == 2 && cards.All(x => x.Card.Media.Kind == "Anime"));
        var categoryButton = (System.Windows.Controls.Button)window.FindName("ShowsLibraryNav");
        categoryButton.Focus(); Call("LibraryCategoryClick", categoryButton, new RoutedEventArgs());
        await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        check(!((System.Windows.Controls.TextBox)window.FindName("SearchBox")).IsKeyboardFocusWithin, "category navigation does not move the cursor into search");
        Navigate(3); await Wait(() => cards.Count == 2 && cards.All(x => x.Card.Media.Kind == "Anime"));
        check(Crumbs().Select(x => x.Label).SequenceEqual(["All", "Anime"]), "Anime category includes anime shows and films with a clickable category path");
        var status = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        status.SelectedIndex = 2; await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == animeMovie.Id);
        check(cards[0].Card.Media.Kind == "Anime", "availability status combines with the sidebar category");
        status.SelectedIndex = 1; await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == anime.Id);
        check(cards[0].Card.Poster is null, "artwork status combines with the sidebar category");
        Navigate(2); await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        status.SelectedIndex = 2; await Wait(() => cards.Count == 0);
        check(((FrameworkElement)window.FindName("NoResults")).Visibility == Visibility.Visible, "empty category and status combination shows a useful empty result");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show"); await Wait(() => cards.Count == 3 && cards.All(x => x.Card.Level == "season"));
        check(Crumbs().Select(x => x.Label).SequenceEqual(["All", "Shows", "Navigation Show"]), "show breadcrumb points to the season grid");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", 0); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == special.Id);
        check(Crumbs().Last().Label == "Specials", "specials have an explicit clickable breadcrumb");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", -1); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == unknown.Id);
        check(Crumbs().Last().Label == "Season unknown", "unknown seasons preserve their navigation identity");
        Navigate(2, LibraryIdentity.ShowKey(show), "Show", 1); await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == show.Id);
        Call("Select", show);
        check(Crumbs(true).Select(x => x.Label).SequenceEqual(["All", "Shows", "Navigation Show", "Season 1", "Episode 1 · The Beginning"]), "episode details display the complete hierarchy");
        ClickCrumb(Crumbs(true).Single(x => x.Level == "season"));
        await Wait(() => cards.Count == 1 && cards[0].Card.Media.Id == show.Id);
        check(((FrameworkElement)window.FindName("LibraryPage")).Visibility == Visibility.Visible && Crumbs().Last().Level == "season", "season breadcrumb returns from episode details to its grid");
        ClickCrumb(Crumbs().Single(x => x.Level == "show")); await Wait(() => cards.Count == 3 && cards.All(x => x.Card.Level == "season"));
        ClickCrumb(Crumbs().Single(x => x.Label == "Shows")); await Wait(() => cards.Count == 1 && cards[0].Card.Level == "series");
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        search.Text = "no matching title"; await Wait(() => cards.Count == 0);
        ClickCrumb(Crumbs().First()); await Wait(() => cards.Count == 753);
        check(Crumbs().Length == 1 && Crumbs()[0].IsCurrent, "All breadcrumb clears category, show, season, search, and status restrictions");
        var menu = (FrameworkElement)window.FindName("LibraryCategories");
        Call("LibraryMenuClick", window, new RoutedEventArgs()); check(menu.Visibility == Visibility.Collapsed, "Library button collapses its category menu");
        Call("LibraryMenuClick", window, new RoutedEventArgs()); check(menu.Visibility == Visibility.Visible, "Library button expands its category menu");
        Navigate(1); await Wait(() => cards.Count == 750 && cards.All(x => x.Card.Media.Kind == "Movie"));
        Call("Select", animeMovie);
        check(Crumbs(true).Select(x => x.Label).SequenceEqual(["All", "Anime", "Navigation Anime Film"]), "anime film details omit show and season levels");
        ClickCrumb(Crumbs(true).First()); await Wait(() => cards.Count == 753);
    }

    private static async Task CheckSearchLayout(MainWindow window, Action<bool, string> check)
    {
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        var scan = (System.Windows.Controls.Button)window.FindName("RescanButton");
        var status = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        var frame = (FrameworkElement)window.FindName("SearchFrame");
        var toolbar = (FrameworkElement)window.FindName("LibraryToolbar");
        var grid = (FrameworkElement)window.FindName("PosterGrid");
        var content = (FrameworkElement)window.FindName("ContentArea");
        window.UpdateLayout();
        var location = grid.TranslatePoint(new Point(0, 0), content);
        var width = frame.ActualWidth; var height = toolbar.ActualHeight;
        search.Focus(); await Dispatcher.Yield(DispatcherPriority.Input); window.UpdateLayout();
        check(grid.TranslatePoint(new Point(0, 0), content) == location && toolbar.ActualHeight == height, "focusing search does not move the poster grid");
        scan.Focus(); await Dispatcher.Yield(DispatcherPriority.Input); window.UpdateLayout();
        check(grid.TranslatePoint(new Point(0, 0), content) == location && frame.ActualWidth == width, "button focus does not resize the search toolbar or shift the grid");
        var original = scan.Content; scan.Content = "Cancel scan"; window.UpdateLayout();
        check(frame.ActualWidth == width && toolbar.ActualHeight == height, "scan button label changes keep the search field and toolbar dimensions stable");
        scan.Content = original;
        search.Text = "Movie"; await Task.Delay(300); window.UpdateLayout();
        check(frame.ActualWidth == width && toolbar.ActualHeight == height, "search text and clear button do not resize the field");
        search.Clear(); await Task.Delay(300);
        var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("LibraryScroll");
        check(scroll.VerticalScrollBarVisibility == System.Windows.Controls.ScrollBarVisibility.Visible, "poster grid reserves scrollbar space across filters");
        check(status.ActualHeight == 44 && scan.ActualHeight == 44 && frame.ActualHeight == 44, "search, status filter, and rescan align to a single toolbar height");
        status.Focus(); window.UpdateLayout();
    }

    private static async Task CheckBackNavigation(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
        var filter = (System.Windows.Controls.ComboBox)window.FindName("LibraryStatusFilter");
        Call("NavigateLibrary", 2, null!, null!, null!); search.Text = "Navigation"; filter.SelectedIndex = 2;
        Call("ShowPage", "Settings");
        Call("BrowseHistory", true); await Task.Delay(150);
        check(Field("currentPage") as string == "Library" && search.Text == "Navigation" && filter.SelectedIndex == 2, "back restores category, search, and availability filter");
        Call("BrowseHistory", false);
        check(Field("currentPage") as string == "Settings", "forward restores the page left by Back");
        Call("BrowseHistory", true); Call("ShowPage", "Queues");
        check(!(bool)Call("BrowseHistory", false)!, "a new navigation clears forward history");
        var mouse = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.XButton1) { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseUpEvent };
        Call("NavigationMouseUp", window, mouse);
        check(mouse.Handled && Field("currentPage") as string == "Library", "mouse back button uses the same browsing history");
        var media = ((IEnumerable<MediaItem>)Field("items")!).First(x => x.Series is not null);
        Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(media), media.Kind, 1);
        Call("Select", media); Call("BrowseHistory", true);
        check(Field("currentPage") as string == "Library" && (int?)Field("browseSeason") == 1, "Back from an episode returns to its previous season view");
        check((bool)Call("GestureControl", search)!, "swipe navigation ignores text entry controls");
        Call("ShowPage", "Settings");
        var hook = typeof(MainWindow).GetMethod("NavigationWindowMessage", flags)!;
        void Horizontal(int delta)
        {
            hook.Invoke(window, new object[] { IntPtr.Zero, 0x020e, new IntPtr((long)(ushort)(short)delta << 16), IntPtr.Zero, false });
        }
        Horizontal(-120); Horizontal(-120);
        check(Field("currentPage") as string == "Settings", "small horizontal movements do not accidentally navigate");
        Horizontal(-120); Horizontal(-360);
        check(Field("currentPage") as string == "Library", "horizontal swipe goes Back once and ignores its inertia");
    }

    private static void CheckQueueEditing(MainWindow window, LibraryStore store, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
        var saved = (List<SavedQueue>)Field("savedQueues");
        var media = ((IEnumerable<MediaItem>)Field("items")).Take(2).ToArray();
        var fixture = new SavedQueue("queue-edit-fixture", "Test queue", [media[0].Id, "missing-media", media[0].Id, media[1].Id]);
        saved.Add(fixture); Call("SelectPlaybackQueue", fixture); Set("queuePosition", 1);
        var list = (System.Windows.Controls.ListBox)window.FindName("SavedQueueItems");
        check(list.Items.Count == 4, "queue cards retain missing entries and duplicate occurrences");
        Call("MoveSavedEntry", fixture, 2, 0);
        var reordered = saved.Single(x => x.Id == fixture.Id);
        check(reordered.MediaIds.SequenceEqual([media[0].Id, media[0].Id, "missing-media", media[1].Id]), "drag reorder moves one duplicate occurrence to the requested position");
        check((int)Field("queuePosition") == 0, "reordering keeps the exact playing duplicate as the active entry");
        Call("ApplyQueueOrder", fixture, new[] { 0 });
        check(saved.Single(x => x.Id == fixture.Id).MediaIds.Length == 4, "stale queue edits cannot overwrite newer ordering");
        var button = new System.Windows.Controls.Button { Tag = list.Items[2] };
        Call("RemoveSavedItem", button, new RoutedEventArgs());
        var afterRemoval = saved.Single(x => x.Id == fixture.Id);
        check(afterRemoval.MediaIds.SequenceEqual([media[0].Id, media[0].Id, media[1].Id]), "card remove targets a missing entry without removing another video");
        check((int)Field("queuePosition") == 0, "removing another card preserves the playback position");
        button.Tag = list.Items[0]; Call("RemoveSavedItem", button, new RoutedEventArgs());
        check((int)Field("queuePosition") == -1, "removing the current entry leaves the following duplicate next to play");
        var persisted = Wire.Read<SavedQueue[]>(store.Setting("queues")!).Single(x => x.Id == fixture.Id);
        check(persisted.MediaIds.SequenceEqual([media[0].Id, media[1].Id]), "queue reordering and per-card deletion persist immediately");
        Call("ShowPage", "Queues"); window.UpdateLayout();
        check(list.ActualHeight > 100 && list.AllowDrop, "redesigned queue keeps a usable drag-and-drop list");
    }

    private static void CheckRoomSettings(MainWindow window, LibraryStore store, Action<bool, string> Check)
    {
        var rooms = (ObservableCollection<SavedRoom>)typeof(MainWindow).GetField("savedRooms", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var first = new SavedRoom("room-one", "Movie night", "https://room.test", new string('A', 24), new string('1', 64));
        var second = new SavedRoom("room-two", "Anime night", first.Server, new string('B', 24), new string('2', 64));
        var guest = new SavedRoom("room-guest", "Friend's room", first.Server, new string('C', 24));
        rooms.Add(first); rooms.Add(second); rooms.Add(guest);
        var list = (System.Windows.Controls.ListBox)window.FindName("SavedRoomsList");
        var controls = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("SavedRoomSharedControls");
        list.SelectedItem = first;
        Check(controls.IsEnabled && controls.IsChecked == false, "saved host room exposes its playback-control setting");
        controls.IsChecked = true;
        typeof(MainWindow).GetMethod("SavedRoomControlsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [controls, new RoutedEventArgs()]);
        Check(store.Setting("room:" + first.Server + ":" + first.Code + ":sharedControls") == "true", "offline room setting saves immediately");
        list.SelectedItem = second;
        Check(controls.IsChecked == false, "each room has an independent shared-controls setting");
        list.SelectedItem = first;
        Check(controls.IsChecked == true, "returning to a saved room restores its control setting");
        list.SelectedItem = guest;
        Check(!controls.IsEnabled, "guests cannot edit room settings");
        var rename = (System.Windows.Controls.Button)window.FindName("RenameSavedRoomButton");
        Check(!rename.IsEnabled, "guest room name is read only");
        typeof(MainWindow).GetMethod("RenameHostRoom", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [guest, "Guest override"]);
        Check(rooms.Single(x => x.Id == guest.Id).Name == guest.Name, "guest cannot rename a room through its handler");
        list.SelectedItem = first;
        Check(rename.IsEnabled, "host can rename their saved room offline");
        typeof(MainWindow).GetMethod("RenameHostRoom", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [first, "New movie night"]);
        Check(rooms.Single(x => x.Id == first.Id).Name == "New movie night" && store.Setting("room:" + first.Server + ":" + first.Code + ":name") == "New movie night", "host rename persists for reconnection");
        var roomField = typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var previousRoom = roomField.GetValue(window);
        var waitingClient = new RoomClient();
        var waitingSnapshot = new RoomSnapshot([new("host", "Host", true, true), new("guest", "Guest", false, false)], null, null, false, [], Name: "Host's shared name");
        typeof(RoomClient).GetProperty(nameof(RoomClient.ServerAddress))!.SetValue(waitingClient, guest.Server);
        typeof(RoomClient).GetProperty(nameof(RoomClient.Identity))!.SetValue(waitingClient, new Welcome(guest.Code, "guest", false, [], false));
        typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waitingClient, waitingSnapshot);
        roomField.SetValue(window, waitingClient);
        try
        {
            list.SelectedItem = guest;
            typeof(MainWindow).GetMethod("SyncCurrentRoomName", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [waitingSnapshot]);
            Check(((System.Windows.Controls.TextBlock)window.FindName("SavedRoomHeading")).Text == waitingSnapshot.Name &&
                rooms.Single(x => x.Id == guest.Id).Name == waitingSnapshot.Name && !rename.IsEnabled,
                "waiting guest replaces its room-code label with the host name and cannot rename it");
            Check(Wire.Read<SavedRoom[]>(store.Setting("rooms")!).Single(x => x.Id == guest.Id).Name == waitingSnapshot.Name,
                "waiting guest's canonical room name is saved for the next app launch");
        }
        finally
        {
            roomField.SetValue(window, previousRoom);
            waitingClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            list.SelectedItem = rooms.Single(x => x.Id == first.Id);
        }
        Check(!first.ToString().Contains(first.HostKey!), "saved-room accessibility labels exclude the private host key");
    }

    private static async Task CheckScrolling(Action<bool, string> check)
    {
        var innerContent = new System.Windows.Controls.Border { Height = 1200 };
        var inner = new System.Windows.Controls.ScrollViewer { Height = 100, Content = innerContent };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(inner);
        content.Children.Add(new System.Windows.Controls.Border { Height = 2000 });
        var outer = new System.Windows.Controls.ScrollViewer { Content = content };
        var host = new Window { Content = outer, Width = 300, Height = 220, Left = -20000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
        host.Show();
        try
        {
            await Task.Delay(50);
            check(PrecisionScrolling.GetEnabled(outer) && PrecisionScrolling.GetEnabled(inner), "precision scrolling is enabled by the application style");
            async Task Flush() { await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
            void Wheel(System.Windows.UIElement source, int delta) => source.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = System.Windows.UIElement.PreviewMouseWheelEvent });
            var step = SystemParameters.WheelScrollLines < 0 ? inner.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
            inner.ScrollToVerticalOffset(100); await Flush();
            Wheel(innerContent, -6); await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step / 20)) < .1 && outer.VerticalOffset == 0, "tiny trackpad delta moves proportionally inside the nearest scroll viewer");
            inner.ScrollToVerticalOffset(100); await Flush();
            for (var index = 0; index < 20; index++) Wheel(innerContent, -6);
            await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step)) < .1, "a burst of small trackpad events totals one wheel notch without losing movement");
            Wheel(innerContent, 120); await Flush();
            check(Math.Abs(inner.VerticalOffset - 100) < .1, "reversing scroll direction moves back by the same amount");
            Wheel(innerContent, -240); await Flush();
            check(Math.Abs(inner.VerticalOffset - (100 + step * 2)) < .1, "larger wheel deltas preserve their proportional distance");
            inner.ScrollToBottom(); await Flush();
            Wheel(innerContent, -120); await Flush();
            var outerStep = SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight : step;
            check(Math.Abs(outer.VerticalOffset - outerStep) < .1, "scrolling at a nested viewer edge continues in its parent");
            var rows = new System.Windows.Controls.StackPanel();
            for (var index = 0; index < 100; index++) rows.Children.Add(new System.Windows.Controls.Border { Height = 20 });
            inner.Content = rows; inner.CanContentScroll = true;
            outer.ScrollToTop(); await Flush(); inner.ScrollToVerticalOffset(10); await Flush();
            var logicalStep = SystemParameters.WheelScrollLines < 0 ? inner.ViewportHeight : SystemParameters.WheelScrollLines;
            if (SystemParameters.WheelScrollLines >= 0 && SystemParameters.WheelScrollLines < 20)
            {
                Wheel(rows, -6); await Flush();
                check(inner.VerticalOffset == 10, "logical lists retain small deltas instead of jumping a full item immediately");
                for (var index = 1; index < 20; index++) { Wheel(rows, -6); await Flush(); }
                check(Math.Abs(inner.VerticalOffset - (10 + logicalStep)) < .1, "logical lists accumulate fractional deltas across separate frames");
            }
        }
        finally { host.Close(); }
    }
}
