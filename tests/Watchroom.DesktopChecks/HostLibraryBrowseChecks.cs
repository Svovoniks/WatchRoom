using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watchroom.Core;
using Watchroom.Desktop;

static class HostLibraryBrowseChecks
{
    public static void Run(string directory)
    {
        // Detached fixture content; no desktop app is opened or personal library read.
        var window = new MainWindow(); var client = new RoomClient();
        void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        T Control<T>(string name) => (T)window.FindName(name);
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        void Pump(Task task)
        {
            while (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }
        var snapshot = new RoomSnapshot([new("host", "Host", true, true), new("guest", "Guest", false, true)], null, null, false,
            LibraryBrowsing: true, LibraryActivity: new("guest", "Guest", "All titles", "", 0));
        typeof(RoomClient).GetProperty("Identity")!.SetValue(client, new Welcome("fixture", "guest", false, [], false));
        typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot);
        ((TaskCompletionSource)typeof(RoomClient).GetField("directConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).SetResult();
        typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, client);
        try
        {
            var pixels = Enumerable.Repeat((byte)120, 64 * 96 * 3).ToArray();
            var bitmap = BitmapSource.Create(64, 96, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, "poster.png"); using (var file = File.Create(path)) encoder.Save(file);
            var movies = Enumerable.Range(0, 25).Select(i => new MediaItem("movie-" + i, "C:/private/movie.mkv", $"Movie {i:00}", "Movie", Poster: path)).ToArray();
            var thumbnail = (string)typeof(MainWindow).GetMethod("CachedLibraryThumbnail", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [path])!;
            var first = SharedLibrary.Browse(movies, new(), _ => thumbnail);
            Check(first.Entries.All(entry => entry.Thumbnail == thumbnail) && !Wire.Serialize(first).Contains("private") && !Wire.Serialize(first).Contains(path), "poster bytes cross the catalog without host paths");
            var show = new MediaItem("episode", "C:/private/episode.mkv", "Show", "Show", Series: "Show", Season: 1, Episode: 1,
                SeriesPoster: path, SeasonPoster: path, EpisodePoster: path, RuntimeMinutes: 24);
            foreach (var browse in new[] { new LibraryBrowseRequest(), new(ParentId: "episode"), new(ParentId: "episode", Season: 1) })
                Check(SharedLibrary.Browse([show], browse, _ => thumbnail).Entries.Single().Thumbnail == thumbnail, "show, season, and episode artwork uses regular catalog selection");
            Call("RefreshSharedLibrary"); Call("FilterHostLibrary");
            void Receive(LibraryBrowsePage page, long? sequence = null) => Call("ReceiveHostLibrary", new WireMessage("catalog-page", Number: sequence ?? Field<long>("guestBrowseRequest"), Data: Wire.Serialize(page)));
            Receive(first);
            var cards = Field<LibraryCardCollection>("guestLibraryCards");
            var grid = Control<ItemsControl>("PosterGrid");
            Check(ReferenceEquals(grid.ItemsSource, cards) && cards.Count == 8, "host data uses the regular poster grid");
            Check(Control<FrameworkElement>("RescanButton").Visibility == Visibility.Collapsed && Control<FrameworkElement>("LibraryStatusFilter").Visibility == Visibility.Collapsed,
                "local-only toolbar actions are removed while browsing the host");
            Pump(cards[0].LoadPosterAsync());
            Check(cards[0].Poster is not null && cards[0].IsRemote && cards[0].Card.Poster is null, "remote poster decodes from bytes without local filesystem access");
            var broken = LibraryCardView.FromSharedEntry(first.Entries[0] with { Thumbnail = "broken image" });
            Pump(broken.LoadPosterAsync()); Check(broken.Poster is null, "invalid remote artwork safely uses the regular placeholder");
            Call("RequestHostLibrary", true); Receive(SharedLibrary.Browse(movies, new(Page: 1), _ => thumbnail));
            Check(cards.Count == 16 && cards.Select(card => card.Card.Media.Id).Distinct().Count() == 16, "subsequent chunks append without replacing or duplicating cards");
            var firstCard = cards[0];
            var search = Control<TextBox>("SearchBox"); search.Text = "Movie 24"; Call("FilterHostLibrary");
            var oldRequest = Field<long>("guestBrowseRequest");
            Receive(SharedLibrary.Browse(movies, new(Query: "Movie 24"), _ => thumbnail));
            Check(cards.Count == 1 && cards[0].DisplayTitle == "Movie 24", "host search changes the data source results");
            search.Clear(); Call("FilterHostLibrary");
            Check(cards.Count == 16 && ReferenceEquals(firstCard, cards[0]) && !Field<bool>("guestBrowsePending"), "cached navigation restores card instances and artwork without refetching");
            Receive(first, oldRequest); Check(cards.Count == 16, "stale responses cannot replace the active cached location");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1280, 850)); content.Arrange(new Rect(0, 0, 1280, 850)); content.UpdateLayout();
            Pump(Task.WhenAll(cards.Select(card => card.LoadPosterAsync())));
            content.UpdateLayout();
            var rendered = new RenderTargetBitmap(1280, 850, 96, 96, PixelFormats.Pbgra32); rendered.Render(content);
            var preview = new PngBitmapEncoder(); preview.Frames.Add(BitmapFrame.Create(rendered));
            Directory.CreateDirectory("artifacts/library-ui");
            using (var file = File.Create("artifacts/library-ui/host-library-browse.png")) preview.Save(file);
            Call("LoadMoreHostTitles");
            var scroll = Control<ScrollViewer>("LibraryScroll"); scroll.ScrollToBottom(); content.UpdateLayout(); Call("LoadMoreHostTitles");
            Check(Field<bool>("guestBrowsePending") && Field<LibraryBrowseRequest>("guestBrowse").Page == 2, "scrolling near the bottom automatically requests the next chunk");
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot with { LibraryBrowsing = false, LibraryActivity = null });
            Call("RefreshSharedLibrary"); Receive(SharedLibrary.Browse(movies, new(Page: 2), _ => thumbnail));
            Check(cards.Count == 0 && !Field<bool>("guestBrowsePending"), "revoked access clears cached titles and ignores in-flight chunks");
        }
        finally
        {
            Call("ReleaseHostLibrary"); Field<DispatcherTimer>("guestBrowseTimer").Stop();
            typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
            Pump(client.DisposeAsync().AsTask()); window.Close();
        }
    }
}
