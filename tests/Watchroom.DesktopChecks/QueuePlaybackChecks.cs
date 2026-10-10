using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

static class QueuePlaybackChecks
{
    public static async Task Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) => (T)window.FindName(name);
        async Task WaitFor(Func<bool> predicate)
        {
            var until = Environment.TickCount64 + 10000;
            while (!predicate() && Environment.TickCount64 < until) await Task.Delay(25);
            if (!predicate()) throw new Exception($"Queue playback fixture timed out: native={Field<MediaPlayer?>("player")?.State}, " +
                $"item={Field<MediaItem?>("playingItem")?.Id}, cursor={Field<int>("queuePosition")}, status={Control<TextBlock>("StatusText").Text}");
        }
        // MainWindow loads the native player asynchronously after its library.
        // Wait for that prerequisite before exercising the playback buttons.
        await WaitFor(() => Field<MediaPlayer?>("player") is not null);
        // Earlier UI fixtures create files inside the watched test profile. A
        // delayed rescan would remove the synthetic IDs injected below. Drain
        // those notifications and cancel any fixture scan before checking queue
        // actions; these checks inject their own library entries below.
        var watched = Field<List<FileSystemWatcher>>("watchers").Where(w => w.EnableRaisingEvents).ToArray();
        foreach (var watcher in watched) watcher.EnableRaisingEvents = false;
        await Dispatcher.Yield(DispatcherPriority.Background);
        Field<DispatcherTimer>("scanDelay").Stop();
        Call("CancelScan", window, new RoutedEventArgs());
        await WaitFor(() => !Field<bool>("scanning"));
        // Keep synthetic queue IDs out of the watched library: discovering these
        // files during the fixture replaces them with scanner-generated IDs.
        var fixtureDirectory = Path.Combine(Path.GetTempPath(), "wr-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        var firstPath = Path.Combine(fixtureDirectory, "Queue first.avi");
        var lastPath = Path.Combine(fixtureDirectory, "Queue last.avi");
        VideoFixture.Write(firstPath, 30); VideoFixture.Write(lastPath, 30);
        var first = new MediaItem("queue-play-first", firstPath, "Queue first", "Movie");
        var last = new MediaItem("queue-play-last", lastPath, "Queue last", "Movie");
        var unavailable = new MediaItem("queue-play-unavailable", Path.Combine(directory, "queue-absent.avi"), "Unavailable", "Movie", Available: false);
        var items = Field<List<MediaItem>>("items"); items.AddRange([first, last, unavailable]);
        var saved = new SavedQueue("queue-play-fixture", "Playback fixture", [first.Id, "missing-reference", first.Id, unavailable.Id, last.Id]);
        var queues = Field<List<SavedQueue>>("savedQueues"); queues.Add(saved);
        var previousQueue = Field<List<MediaItem>>("queue").ToArray();
        var previousId = typeof(MainWindow).GetField("activeQueueId", flags)!.GetValue(window);
        var previousPosition = Field<int>("queuePosition");
        var player = Field<MediaPlayer>("player");
        bool Playing(MediaItem item, int position) => Field<MediaItem?>("playingItem")?.Id == item.Id &&
            Field<int>("queuePosition") == position && player.IsPlaying;
        try
        {
            Call("RefreshQueuePickers", saved.Id); Call("ShowPage", "Queues");
            var list = Control<ListBox>("SavedQueueItems"); list.SelectedIndex = 1;
            check(!Control<Button>("PlaySelectedSavedQueueButton").IsEnabled, "a missing saved queue reference cannot be played");
            list.SelectedIndex = 2;
            check(Control<Button>("PlaySelectedSavedQueueButton").IsEnabled, "selecting an available saved queue row enables Play selected");
            Control<Button>("PlaySelectedSavedQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Playing(first, 1));
            check(store.Setting("queuePosition") == "1" && Field<List<MediaItem>>("queue").Select(x => x.Id).SequenceEqual([first.Id, first.Id, unavailable.Id, last.Id]),
                "saved queue playback targets the selected duplicate after missing references without reordering");
            check((string)Field<string>("currentPage") == "Room", "playing a saved queue entry opens the player");
            var playerList = Control<ListBox>("QueueList"); playerList.SelectedIndex = 2;
            check(!Control<Button>("PlaySelectedQueueButton").IsEnabled, "the player disables playback for unavailable queue entries");
            await (Task)Call("PlayQueueAt", 2)!;
            check(Field<int>("queuePosition") == 1 && Field<MediaItem>("playingItem").Id == first.Id,
                "an unavailable direct playback request preserves the current video and cursor");
            playerList.SelectedIndex = 3;
            Control<Button>("PlaySelectedQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => Playing(last, 3));
            check(store.Setting("queuePosition") == "3", "Play selected in the player switches to any queue entry and persists the cursor");
            Call("ShowPage", "Queues"); list.SelectedIndex = 2;
            var savedKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            list.RaiseEvent(savedKey);
            await WaitFor(() => Playing(first, 1));
            check(savedKey.Handled, "Enter in the saved queue starts the selected occurrence");
            playerList.SelectedIndex = 0;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            playerList.RaiseEvent(key);
            await WaitFor(() => Playing(first, 0));
            check(key.Handled, "Enter in the player queue starts its selected entry");
            Call("TogglePlayerOptions", window, new RoutedEventArgs()); window.UpdateLayout();
            playerList.SelectedIndex = 1;
            var row = (ListBoxItem)playerList.ItemContainerGenerator.ContainerFromIndex(1);
            var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = row };
            playerList.RaiseEvent(doubleClick);
            await WaitFor(() => Playing(first, 1));
            check(doubleClick.Handled, "double-click in the player starts the chosen queue occurrence");
            Call("TogglePlayerOptions", window, new RoutedEventArgs());
            Call("ShowPage", "Queues"); list.SelectedIndex = 4; window.UpdateLayout();
            list.ScrollIntoView(list.SelectedItem); window.UpdateLayout();
            var savedRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(4);
            var savedDoubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent, Source = savedRow };
            list.RaiseEvent(savedDoubleClick);
            await WaitFor(() => Playing(last, 3));
            check(savedDoubleClick.Handled, "double-click in the saved queue starts the chosen video");
            await (Task)Call("PlayQueueAt", 0)!;
            await (Task)Call("PlayQueueStep", 1)!;
            await WaitFor(() => Playing(first, 1));
            await (Task)Call("PlayQueueStep", 1)!;
            await WaitFor(() => Playing(last, 3));
            check(queues.Single(x => x.Id == saved.Id).MediaIds.SequenceEqual(saved.MediaIds),
                "continuing after a direct jump skips unavailable files and preserves saved queue order");

            await using var guest = new RoomClient();
            typeof(RoomClient).GetProperty("Identity")!.SetValue(guest, new Welcome("fixture", "guest", false, [], false));
            Set("room", guest);
            try
            {
                Call("UpdateSessionControls");
                check(!Control<Button>("PlaySelectedQueueButton").IsEnabled && !Control<Button>("PlaySelectedSavedQueueButton").IsEnabled,
                    "guests cannot use either local queue playback action");
                await (Task)Call("PlayQueueAt", 0)!;
                await (Task)Call("PlaySavedQueueEntry", saved, 0)!;
                check(Field<int>("queuePosition") == 3 && Field<MediaItem>("playingItem").Id == last.Id,
                    "guest direct requests cannot replace the host-controlled video or queue");
            }
            finally { Set("room", null); }
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            queues.Remove(saved); items.RemoveAll(x => x.Id == first.Id || x.Id == last.Id || x.Id == unavailable.Id);
            var queue = Field<List<MediaItem>>("queue"); queue.Clear(); queue.AddRange(previousQueue);
            Set("activeQueueId", previousId); Set("queuePosition", previousPosition);
            Call("SaveQueues"); Call("RefreshQueuePickers", previousId!); Call("PublishQueue"); Call("ShowPage", "Library");
            File.Delete(firstPath); File.Delete(lastPath); Directory.Delete(fixtureDirectory);
            foreach (var watcher in watched) watcher.EnableRaisingEvents = true;
        }
    }
}
