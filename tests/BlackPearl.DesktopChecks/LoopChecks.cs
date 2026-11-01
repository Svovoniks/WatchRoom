using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// The Loop button cycles Off, Loop all and Loop one; at the end of a video Loop one replays it,
// Loop all wraps a finished queue or show, and Loop off keeps the usual end-of-video behaviour.
static class LoopChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var modeField = typeof(MainWindow).GetField("loopMode", flags)!;
        void Mode(string mode) { modeField.SetValue(window, Enum.Parse(modeField.FieldType, mode)); Call("RefreshLoopButton"); }
        string CurrentMode() => modeField.GetValue(window)!.ToString()!;
        var directory = Path.Combine(Path.GetTempPath(), "wr-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new[] { "Loop S00E01.avi", "Loop S01E01.avi", "Loop S01E02.avi", "Loop movie.avi", "Loop movie two.avi" }.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var path in paths) VideoFixture.Write(path, 20);
        var special = new MediaItem("loop-special", paths[0], "Loop Show", "Show", Series: "Loop Show", Season: 0, Episode: 1);
        var first = new MediaItem("loop-1", paths[1], "Loop Show", "Show", Series: "Loop Show", Season: 1, Episode: 1);
        var second = new MediaItem("loop-2", paths[2], "Loop Show", "Show", Series: "Loop Show", Season: 1, Episode: 2);
        var movie = new MediaItem("loop-movie", paths[3], "Loop movie", "Movie");
        var movieTwo = new MediaItem("loop-movie-two", paths[4], "Loop movie two", "Movie");
        var originalItems = Field<List<MediaItem>>("items");
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var loop = (ToggleButton)window.FindName("LoopButton");
        var card = (Border)window.FindName("UpNextCard");
        var upNext = (TextBlock)window.FindName("UpNextEpisode");
        async Task<bool> Advances()
        {
            var player = Field<MediaPlayer>("player"); var start = player.Time;
            for (var i = 0; i < 40; i++) { await Task.Delay(100); if (player.IsPlaying && player.Time > start + 300) return true; }
            return false;
        }
        // Plays the video near its end, pauses, and reports the end as LibVLC would.
        async Task<Task> End(MediaItem item)
        {
            var player = Field<MediaPlayer>("player");
            player.Time = 15000; await Task.Delay(300);
            player.SetPause(true);
            for (var i = 0; i < 30 && player.IsPlaying; i++) await Task.Delay(100);
            return (Task)Call("PlaybackEnded", item, null, Field<int>("loadGeneration"))!;
        }
        async Task<bool> Restarted(MediaItem item)
        {
            var player = Field<MediaPlayer>("player");
            return Field<MediaItem>("playingItem").Id == item.Id && await Advances() && player.Time < 8000;
        }
        try
        {
            Set("items", originalItems.Concat([special, first, second, movie, movieTwo]).ToList());
            queue.Clear(); Set("queuePosition", -1);
            Call("ShowPage", "Library");
            await (Task)Call("PlayLocalItem", first)!;
            Call("UpdateSessionControls");
            check(await Advances(), "the loop fixture plays");

            // The button cycles like VLC's.
            Mode("Off");
            check(loop.IsEnabled && loop.IsChecked == false && (string)loop.ToolTip == "Loop off (L)", "Loop starts off and is enabled outside a room");
            Call("ToggleLoop", window, new RoutedEventArgs());
            check(CurrentMode() == "All" && loop.IsChecked == true && (string)loop.Content == "" && (string)loop.ToolTip == "Loop all (L)", "one press turns on Loop all");
            Call("ToggleLoop", window, new RoutedEventArgs());
            check(CurrentMode() == "One" && loop.IsChecked == true && (string)loop.Content == "" && (string)loop.ToolTip == "Loop one (L)", "a second press switches to Loop one with its own icon");
            Call("ToggleLoop", window, new RoutedEventArgs());
            check(CurrentMode() == "Off" && loop.IsChecked == false && (string)loop.Content == "", "a third press turns looping off");

            // Folded into the … menu, the Loop row names the current mode.
            var overflow = (StackPanel)window.FindName("OverflowItems");
            bool RowSays(string label) => overflow.Children.OfType<Button>().Any(row => row.Content is Grid grid && grid.Children.OfType<TextBlock>().Any(text => text.Text == label));
            loop.Visibility = Visibility.Collapsed; Call("RefreshOverflowMenu");
            check(RowSays("Loop off"), "a folded Loop row shows the current mode");
            Call("ToggleLoop", window, new RoutedEventArgs());
            check(RowSays("Loop all") && !RowSays("Loop off"), "changing the mode updates the folded Loop row");
            loop.Visibility = Visibility.Visible; Call("RefreshOverflowMenu");

            // Loop off: the last episode ends without replaying or offering anything.
            Mode("Off");
            await (Task)Call("PlayLocalItem", second)!;
            var ended = await End(second);
            check(await Task.WhenAny(ended, Task.Delay(3000)) == ended && card.Visibility != Visibility.Visible && !Field<MediaPlayer>("player").IsPlaying,
                "with Loop off the last episode ends without replaying");

            // Loop one replays the video from the start.
            Mode("One");
            await (Task)Call("PlayLocalItem", first)!;
            ended = await End(first);
            check(await Task.WhenAny(ended, Task.Delay(3000)) == ended && card.Visibility != Visibility.Visible && await Restarted(first),
                "Loop one replays the episode from the start instead of offering the next one");

            // Loop all carries on to the next episode as usual.
            Mode("All");
            ended = await End(first);
            for (var i = 0; i < 30 && card.Visibility != Visibility.Visible; i++) await Task.Delay(100);
            check(card.Visibility == Visibility.Visible && upNext.Text == $"Season 1 · {second.EpisodeDisplayTitle}" && !ended.IsCompleted,
                "Loop all still offers the next episode in the middle of a show");
            Call("EndUpNext", false); await ended;

            // After the last episode, Loop all offers the first regular episode, not the special.
            await (Task)Call("PlayLocalItem", second)!;
            ended = await End(second);
            for (var i = 0; i < 30 && card.Visibility != Visibility.Visible; i++) await Task.Delay(100);
            check(card.Visibility == Visibility.Visible && upNext.Text == $"Season 1 · {first.EpisodeDisplayTitle}",
                "Loop all offers the show's first regular episode after the last one");
            Call("UpNextPlayClicked", window, new RoutedEventArgs(Button.ClickEvent));
            await ended;
            check(Field<MediaItem>("playingItem").Id == first.Id && await Advances(), "Loop all starts the show again");

            // Loop all wraps a finished queue to its first video.
            queue.Clear(); queue.AddRange([movie, movieTwo]); Call("PublishQueue");
            await (Task)Call("PlayQueueAt", 1)!;
            check(Field<MediaItem>("playingItem").Id == movieTwo.Id && Field<int>("queuePosition") == 1, "the queue's last video plays");
            ended = await End(movieTwo);
            check(await Task.WhenAny(ended, Task.Delay(5000)) == ended && Field<MediaItem>("playingItem").Id == movie.Id && Field<int>("queuePosition") == 0 && await Advances(),
                "Loop all plays the queue again from its first video");

            // With nothing else to play, Loop all replays the video.
            queue.Clear(); Set("queuePosition", -1); Call("PublishQueue");
            await (Task)Call("PlayLocalItem", movie)!;
            ended = await End(movie);
            check(await Task.WhenAny(ended, Task.Delay(3000)) == ended && await Restarted(movie), "Loop all replays a movie that is not in a queue");
        }
        finally
        {
            Mode("Off");
            if (card.Visibility == Visibility.Visible) Call("EndUpNext", false);
            await (Task)Call("Disconnect", false, false)!;
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            Set("items", originalItems);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
