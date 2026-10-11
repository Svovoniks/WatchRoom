using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Core;
using Watchroom.Desktop;

static class HomeChecks
{
    public static async Task Run(string directory)
    {
        var window = new MainWindow();
        await using var client = new RoomClient();
        void Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        T Control<T>(string name) => (T)window.FindName(name);
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!condition()) throw new TimeoutException("Home fixture did not finish rendering");
        }
        void Layout(int width, int height, string name)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
        }
        var store = Field<LibraryStore>("library");
        var posterPath = Path.Combine(directory, "home-poster.png");
        var poster = BitmapSource.Create(120, 180, 96, 96, PixelFormats.Rgb24, null, Enumerable.Repeat((byte)120, 120 * 180 * 3).ToArray(), 120 * 3);
        var posterEncoder = new PngBitmapEncoder(); posterEncoder.Frames.Add(BitmapFrame.Create(poster));
        using (var posterFile = File.Create(posterPath)) posterEncoder.Save(posterFile);
        var items = new List<MediaItem>
        {
            new("home-episode1", "", "Night Train", "Show", Series: "Night Train", Season: 1, Episode: 1, RuntimeMinutes: 25),
            new("home-episode2", "", "Night Train", "Show", Series: "Night Train", Season: 1, Episode: 2, EpisodeTitle: "The crossing", RuntimeMinutes: 25),
            new("home-movie", "", "A Sunday in October", "Movie", Year: 2025, RuntimeMinutes: 100)
        };
        items.AddRange(Enumerable.Range(0, 12).Select(index => new MediaItem("home-new" + index, "", "Library title " + index, "Movie", Year: 2026, Poster: posterPath)));
        store.Setting("watched:home-episode1", "true"); store.Setting("position:home-movie", "90000");
        typeof(MainWindow).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, items);
        try
        {
            Call("RefreshHome");
            await Until(() => Control<ItemsControl>("HomeContinue").Items.Count == 2);
            Check(Control<ScrollViewer>("HomePage").Visibility == Visibility.Visible && Control<Button>("HomeNav").Visibility == Visibility.Visible,
                "Home is the startup tab and has a navigation entry");
            Check(Control<ItemsControl>("HomeSuggestions").Items.Count == 8 && Control<Button>("HomeMystery").IsEnabled,
                "local Home renders suggestions and enables the mystery card");
            var next = Control<ItemsControl>("HomeContinue").Items.Cast<LibraryCardView>().Single(view => view.IsEpisode);
            Check(next.Card.Media.Id == "home-episode2" && next.PlaybackLabel == "Next episode", "continue watching identifies the next unwatched episode");
            Layout(1280, 850, "home-desktop"); Layout(900, 640, "home-compact");
            Check(Control<ScrollViewer>("HomePage").ScrollableHeight > 0, "Home remains vertically scrollable at the minimum window size");
            Control<ScrollViewer>("HomePage").ScrollToBottom();
            Layout(900, 640, "home-suggestions-section");
            Control<ScrollViewer>("HomePage").ScrollToTop();
            var suggestionRow = Control<ItemsControl>("HomeSuggestions");
            var originalSource = suggestionRow.ItemsSource;
            var originalViews = suggestionRow.Items.Cast<LibraryCardView>().ToArray();
            foreach (var view in originalViews) await view.LoadPosterAsync();
            var originalImages = originalViews.Select(view => view.Poster).ToArray();
            var originalContainers = originalViews.Select(view => suggestionRow.ItemContainerGenerator.ContainerFromItem(view)).ToArray();
            Check(originalImages.All(image => image is not null) && originalContainers.All(container => container is not null), "suggestion fixture has loaded posters and realized card containers");
            var changes = new List<string?>();
            foreach (var view in originalViews) view.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            for (var index = 0; index < items.Count; index++)
                items[index] = items[index] with { Title = "Updated title " + (items.Count - index), Overview = "Updated metadata" };
            Call("RefreshHome");
            await Until(() => originalViews[0].Card.Media.Overview == "Updated metadata");
            Check(ReferenceEquals(suggestionRow.ItemsSource, originalSource) && suggestionRow.Items.Cast<LibraryCardView>().SequenceEqual(originalViews),
                "background metadata refresh retains the suggestion row and every card instance");
            Check(originalViews.Select(view => view.Poster).SequenceEqual(originalImages) && !changes.Contains("Poster") &&
                originalViews.Select(view => suggestionRow.ItemContainerGenerator.ContainerFromItem(view)).SequenceEqual(originalContainers),
                "background metadata refresh keeps loaded posters and card containers without blank frames");
            var retainedPoster = originalViews[0].Poster;
            var replacementPath = Path.Combine(directory, "home-poster-replacement.png");
            File.Copy(posterPath, replacementPath);
            var replacedId = originalViews[0].Card.Media.Id;
            var replacedIndex = items.FindIndex(item => item.Id == replacedId);
            items[replacedIndex] = items[replacedIndex] with { Poster = replacementPath };
            var blankPoster = false;
            originalViews[0].PropertyChanged += (_, args) => { if (args.PropertyName == "Poster" && originalViews[0].Poster is null) blankPoster = true; };
            Call("RefreshHome");
            await Until(() => originalViews[0].Card.Poster == replacementPath && !ReferenceEquals(originalViews[0].Poster, retainedPoster));
            Check(!blankPoster && ReferenceEquals(suggestionRow.ItemsSource, originalSource), "new artwork replaces a suggestion's poster in place without a placeholder frame");
            var snapshot = new RoomSnapshot([new("host", "Fixture Host", true, true), new("guest", "Guest", false, true)], null, null, false,
                LibraryBrowsing: true, LibraryActivity: new("guest", "Guest", "Home", "", Wire.Now));
            typeof(RoomClient).GetProperty("Identity")!.SetValue(client, new Welcome("fixture", "guest", false, [], false));
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot);
            ((TaskCompletionSource)typeof(RoomClient).GetField("directConnected", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!).SetResult();
            typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, client);
            Call("ShowPage", "Library"); Call("ShowPage", "Home");
            Check(Control<ItemsControl>("HomeContinue").Items.Count == 0 && System.Windows.Automation.AutomationProperties.GetName(Control<ScrollViewer>("HomePage")) == "Home · Fixture Host’s library",
                "guest Home clears local progress and identifies the host's library");
            var request = Field<LibraryBrowseRequest>("guestBrowse");
            for (var index = 0; index < 3; index++)
            {
                var page = SharedLibrary.Browse(items, request with { Page = index }, playback: store.PlaybackSettings());
                Call("ReceiveHostLibrary", new WireMessage("catalog-page", Number: Field<long>("guestBrowseRequest"), Data: Wire.Serialize(page)));
                await Task.Delay(30);
                if ((index + 1) * SharedLibrary.PageSize >= page.Total) break;
            }
            Check(Control<ItemsControl>("HomeContinue").Items.Count == 2 && Control<ItemsControl>("HomeSuggestions").Items.Count == 8 && Control<Button>("HomeMystery").IsEnabled,
                "guest Home assembles all paginated continue, suggestion, and mystery cards");
            var paused = Control<ItemsControl>("HomeContinue").Items.Cast<LibraryCardView>().Single(view => !view.IsEpisode);
            Check(paused.IsRemote && paused.PlaybackLabel == "Resume · 1:30", "guest Home displays the host's saved movie progress");
            Layout(1280, 850, "home-guest");
            Call("ReceiveHostLibrary", new WireMessage("catalog-page", Number: Field<long>("guestBrowseRequest") - 1,
                Data: Wire.Serialize(new LibraryBrowsePage([], 0, 0, "Home"))));
            Check(Control<ItemsControl>("HomeContinue").Items.Count == 2, "stale guest responses cannot replace Home rows");
            Call("OpenHomeTitle", paused);
            Check(Field<string>("currentPage") == "Details" && Field<bool>("guestHomeResume") && Control<Button>("DetailPlay").Content.Equals("Play in room"),
                "guest continue watching opens room playback details with resume intent");
            Call("ShowPage", "Home");
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, snapshot with { LibraryBrowsing = false, LibraryActivity = null });
            Call("RefreshSharedLibrary");
            Check(Control<ItemsControl>("HomeContinue").Items.Count == 0 && Control<ItemsControl>("HomeSuggestions").Items.Count == 0 && !Control<Button>("HomeMystery").IsEnabled &&
                Control<TextBlock>("HomeStatus").Text.Contains("disabled"), "revoking browsing immediately clears every guest Home action");
            typeof(MainWindow).GetField("room", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, null);
            Call("RefreshSharedLibrary");
            await Until(() => Control<ItemsControl>("HomeContinue").Items.Count == 2);
            Check(System.Windows.Automation.AutomationProperties.GetName(Control<ScrollViewer>("HomePage")) == "Home", "leaving a room restores local Home content");
        }
        finally { window.Close(); }
    }
}
