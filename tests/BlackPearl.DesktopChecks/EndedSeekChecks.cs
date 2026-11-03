using System.IO;
using System.Reflection;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// A finished video ignores seeks, so a timeline position chosen after the end is kept and used when it plays again.
static class EndedSeekChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-ended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Ended seek.avi");
        VideoFixture.Write(path, 20);
        var item = new MediaItem("ended-seek", path, "Ended seek", "Movie");
        var earlierPath = Path.Combine(directory, "Earlier.avi");
        VideoFixture.Write(earlierPath, 20);
        var earlier = new MediaItem("ended-seek-earlier", earlierPath, "Earlier", "Movie");
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var timeline = (Slider)window.FindName("Timeline");
        try
        {
            queue.Clear(); Set("queuePosition", -1);
            await (Task)Call("PlayLocalItem", item)!;
            var player = Field<MediaPlayer>("player");
            for (var i = 0; i < 50 && !(player.IsPlaying && player.Length > 0); i++) await Task.Delay(100);
            player.Time = Math.Max(0, player.Length - 400);
            for (var i = 0; i < 60 && player.State != VLCState.Ended; i++) await Task.Delay(100);
            check(player.State == VLCState.Ended, "the fixture video reaches its end");
            var previous = (Button)window.FindName("PreviousButton");
            Call("UpdateSessionControls");
            check(previous.IsEnabled && (string)previous.ToolTip == "Back to the start (P)" &&
                System.Windows.Automation.AutomationProperties.GetName(previous) == "Back to the start",
                "after the video ends, Previous offers to return to its start");
            previous.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Call("UpdateSessionControls");
            check(player.State == VLCState.Ended && Field<long?>("endedSeek") == 0 && !previous.IsEnabled && (string)previous.ToolTip == "Previous video (P)",
                "pressed after the end, Previous keeps the start for the next Play, and a second press would step back");

            Set("seeking", true); timeline.Value = 5000; Call("CommitSeek");
            Call("PlaybackTickCore");
            check(Math.Abs(timeline.Value - 5000) < 1, "the timeline keeps a position chosen after the video ended");
            Call("RequestPlayback", true);
            for (var i = 0; i < 50 && !(player.IsPlaying && player.Time > 0); i++) await Task.Delay(100);
            await Task.Delay(300);
            check(player.IsPlaying && player.Time is > 4500 and < 9000, $"Play after the end starts from the chosen position ({player.Time} ms)");

            Call("UpdateSessionControls");
            check(previous.IsEnabled && (string)previous.ToolTip == "Back to the start (P)", "past the first seconds, Previous offers to return to the start even without a previous video");
            previous.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 30 && player.Time > 3000; i++) await Task.Delay(100);
            check(player.Time < 3000, $"Previous returns to the start of the video ({player.Time} ms)");
            Call("UpdateSessionControls");
            check(!previous.IsEnabled && (string)previous.ToolTip == "Previous video (P)" &&
                System.Windows.Automation.AutomationProperties.GetName(previous) == "Previous video",
                "near the start, Previous steps back and is disabled with nothing before");

            // In a queue, a press near the start plays the video before.
            queue.Clear(); queue.AddRange([earlier, item]); Set("queuePosition", 1); Call("PublishQueue");
            check(previous.IsEnabled && (string)previous.ToolTip == "Previous video (P)", "near the start of a queued video, Previous offers the video before");
            previous.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 50 && Field<MediaItem>("playingItem")?.Id != earlier.Id; i++) await Task.Delay(100);
            check(Field<MediaItem>("playingItem")?.Id == earlier.Id && Field<int>("queuePosition") == 0, "pressed near the start, Previous plays the queue's video before");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
