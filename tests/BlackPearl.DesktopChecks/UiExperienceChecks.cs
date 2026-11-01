using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Detached layouts and generated fixtures: no personal library or room service needed.
static class UiExperienceChecks
{
    public static void Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var root = (FrameworkElement)window.Content;
        var store = (LibraryStore)Field("library");
        var checks = 0;
        void Check(bool value, string message)
        { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        void PumpUntil(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            do
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                if (done()) return;
                Thread.Sleep(10);
            } while (DateTime.UtcNow < deadline);
            throw new Exception("Timed out waiting for UI fixture");
        }
        void Filter(int status)
        {
            Control<ComboBox>("LibraryStatusFilter").SelectedIndex = status;
            Call("FilterLibrary", false, false);
            PumpUntil(() => typeof(MainWindow).GetField("librarySearchCancellation", flags)!.GetValue(window) is null);
        }
        void Layout(int width, int height)
        { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        void Capture(string name, int width, int height)
        {
            Layout(width, height);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/ui-review/screens");
            using var output = File.Create($"artifacts/ui-review/screens/{name}-{width}.png"); encoder.Save(output);
        }
        IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        try
        {
            var titles = new[] { "Arrival", "The Grand Budapest Hotel", "Interstellar", "Past Lives", "Spirited Away", "The Secret Life of Walter Mitty" };
            var media = titles.Select((title, index) =>
            {
                var path = Path.Combine(App.DataDirectory, $"ui-experience-{index}.mp4");
                File.WriteAllText(path, "UI fixture only");
                return new MediaItem("ux-" + index, path, title, "Movie", Year: 2014 + index, RuntimeMinutes: 110,
                    Overview: "A movie from your library. Play on this device or invite friends to watch in a room.");
            }).ToList();
            var episode = media[0] with { Id = "ux-episode", Title = "Pilot", Series = "The Expanse", Kind = "Show", Season = 1, Episode = 1 };
            var untouchedEpisode = episode with { Id = "ux-episode-2", Title = "The next episode", Episode = 2 };
            var otherSeason = episode with { Id = "ux-season-2", Season = 2 };
            media.AddRange([episode, untouchedEpisode, otherSeason]);
            var unavailable = media[0] with { Id = "ux-unavailable", Path = Path.Combine(App.DataDirectory, "missing.mp4"), Title = "Offline movie", Available = false };
            media.Add(unavailable);
            store.Setting("position:" + media[0].Id, "1260000");
            store.Setting("position:" + episode.Id, "65000");
            store.Setting("watched:" + media[1].Id, "true");
            store.Setting("position:" + media[1].Id, "40000");
            store.Setting("position:" + unavailable.Id, "40000");
            Set("libraryLoaded", true); Set("items", media);
            Call("ShowPage", "Library"); Filter(2);
            var cards = (LibraryCardCollection)Field("libraryCards");
            Check(cards.Count == 2 && cards.Any(card => card.IsMovie) && cards.Any(card => card.Card.Level == "series"),
                "Continue watching includes resumed movies and series, excludes watched and unavailable files");
            var movie = cards.Single(card => card.IsMovie);
            Check(movie.PlaybackLabel == "Resume · 21:00" && movie.HasPlaybackProgress && movie.Progress > 0,
                "movie poster exposes its saved resume time and progress");
            Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(episode), "Show", null!); Filter(2);
            Check(cards.Count == 1 && cards[0].Card.Season == 1, "Continue watching keeps only the season containing an unfinished video");
            Call("NavigateLibrary", 0, null!, null!, null!); Filter(4);
            Check(cards.Count == 1 && cards[0].Card.Media.Id == media[1].Id, "Watched filter returns completed titles");
            Filter(3); Check(cards.All(card => !card.IsWatched), "Unwatched filter excludes completed titles");
            Filter(0); var allCount = cards.Count;
            Call("ShowPage", "Queues"); Call("BrowseHistory", true); Filter(0);
            Check(Control<FrameworkElement>("LibraryPage").Visibility == Visibility.Visible && cards.Count == allCount,
                "visible navigation history restores the library");
            Call("Select", unavailable); Layout(900, 600);
            Check(!Control<Button>("DetailPlay").IsEnabled && Control<TextBlock>("DetailAvailability").Text.Contains("Reconnect"),
                "unavailable details explain how to recover before disabled playback actions");
            Call("Select", media[0]);
            Check(Control<TextBlock>("DetailAvailability").Visibility == Visibility.Collapsed && Control<Button>("DetailPlay").Content.ToString()!.Contains("Resume"),
                "available details clear stale unavailable guidance and offer resume");
            Check(Control<TextBox>("DetailFile").IsReadOnly && Control<TextBox>("DetailFile").Text == media[0].Path,
                "file location can be selected and copied without being edited");
            var queues = (List<SavedQueue>)Field("savedQueues"); queues.Clear(); queues.Add(new("ux-queue", "Movie night", [media[0].Id, media[1].Id, episode.Id]));
            Call("OpenQueue", "ux-queue");
            Check(Control<Button>("PlaySavedQueueButton").Visibility == Visibility.Visible, "filled queue exposes playback actions");
            queues[0] = queues[0] with { MediaIds = [] }; Call("RefreshQueuePickers");
            Check(Control<Button>("PlaySavedQueueButton").Visibility == Visibility.Collapsed && Control<Button>("AddQueueVideosButton").IsEnabled,
                "empty queue removes disabled playback actions and leaves Add videos available");
            var rooms = (ObservableCollection<SavedRoom>)Field("savedRooms"); rooms.Clear();
            rooms.Add(new("ux-room", "Friday movie night", "https://room.test", new string('A', 24), new string('1', 64)));
            Control<ListBox>("SavedRoomsList").SelectedIndex = 0;
            // Inspect all screens in both themes at the minimum and default desktop sizes.
            foreach (var theme in new[] { "dark", "light" })
            {
                Set("light", theme == "light"); Call("ApplyTheme");
                foreach (var size in new[] { (900, 600), (1280, 810) })
                {
                    Call("ShowPage", "Library"); Filter(0); Capture("library-" + theme, size.Item1, size.Item2);
                    Call("Select", media[0]); Capture("details-" + theme, size.Item1, size.Item2);
                    Call("ShowPage", "Queues"); Capture("queue-empty-" + theme, size.Item1, size.Item2);
                    queues[0] = queues[0] with { MediaIds = [media[0].Id, media[1].Id, episode.Id] }; Call("RefreshQueuePickers"); Capture("queue-" + theme, size.Item1, size.Item2);
                    queues[0] = queues[0] with { MediaIds = [] }; Call("RefreshQueuePickers");
                    foreach (var page in new[] { "Folders", "Rooms", "Welcome" }) { Call("ShowPage", page); Capture(page.ToLowerInvariant() + "-" + theme, size.Item1, size.Item2); }
                    Call("ShowPage", "Settings");
                    foreach (var section in new[] { "Profile", "Metadata", "Playback", "Appearance", "Updates" })
                    {
                        Call("ShowPreferenceSection", section); Capture("settings-" + section.ToLowerInvariant() + "-" + theme, size.Item1, size.Item2);
                        if (section == "Metadata")
                        {
                            var viewport = Control<ScrollViewer>("MetadataPreferences");
                            var right = viewport.TransformToAncestor(root).Transform(new Point(viewport.ActualWidth - 12, 0)).X;
                            Check(Descendants<Button>(Control<ListBox>("MetadataProviders")).All(button =>
                                button.TransformToAncestor(root).Transform(new Point(button.ActualWidth, 0)).X <= right),
                                $"metadata reorder controls fit inside their viewport at {size} in {theme}");
                        }
                    }
                }
            }
            Console.WriteLine($"{checks} UI experience checks passed.");
        }
        finally { window.Close(); }
    }
}
