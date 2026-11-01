using System.IO;
using System.Reflection;
using System.Windows;
using Watchroom.Core;
using Watchroom.Desktop;

// Videos opened from Explorer play as their library record when the library knows them, and on their own otherwise.
static class OpenFileChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-open-" + Guid.NewGuid().ToString("N"));
        var shows = Path.Combine(directory, "Shows", "Opened Show", "Season 1"); var downloads = Path.Combine(directory, "Downloads");
        Directory.CreateDirectory(shows); Directory.CreateDirectory(downloads);
        var libraryPath = Path.Combine(shows, "Opened Show S01E01.avi"); VideoFixture.Write(libraryPath, 20);
        var nextPath = Path.Combine(shows, "Opened Show S01E02.avi"); VideoFixture.Write(nextPath, 20);
        var copyPath = Path.Combine(downloads, "Opened Show S01E01.avi"); File.Copy(libraryPath, copyPath);
        var otherPath = Path.Combine(downloads, "Something else.avi"); VideoFixture.Write(otherPath, 20);
        var first = FileNames.Parse(libraryPath, "Show") with { Id = "opened-1", FileSize = new FileInfo(libraryPath).Length };
        var second = FileNames.Parse(nextPath, "Show") with { Id = "opened-2", FileSize = new FileInfo(nextPath).Length };
        var originalItems = Field<List<MediaItem>>("items");
        try
        {
            Set("items", originalItems.Concat([first, second]).ToList());
            await (Task)Call("OpenFileAsync", copyPath)!;
            var playing = Field<MediaItem>("playingItem");
            check(playing.Id == first.Id && playing.Path == copyPath && Field<MediaItem>("selected").Id == first.Id && Field<string>("currentPage") == "Room",
                "a copy of a library episode opens in the player as that episode, with its details page behind it");
            check(EpisodePlayback.Next(Field<List<MediaItem>>("items"), playing, false, item => File.Exists(item.Path))?.Id == second.Id,
                "an opened library episode continues with the library's next episode");
            Call("RefreshPlayingItem");
            check(Field<MediaItem>("playingItem").Path == copyPath, "library refreshes keep playing the opened file");

            // A video opened at launch shows the player at once and plays before the library has rendered.
            var launched = new MainWindow(copyPath);
            T LaunchedField<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(launched)!;
            check(LaunchedField<string>("currentPage") == "Room" && ((FrameworkElement)launched.FindName("PlayerEmpty")).Visibility == Visibility.Collapsed &&
                ((System.Windows.Controls.TextBlock)launched.FindName("RoomHeading")).Text.Contains("Opened Show"),
                "a video opened at launch starts in the player, titled from its file name");
            launched.Close();
            await (Task)Call("Disconnect", false, false)!;
            var backLocations = Field<System.Collections.IList>("backLocations"); backLocations.Clear();
            await (Task)Call("OpenStartupFileAsync", copyPath, Task.FromResult(Field<List<MediaItem>>("items")))!;
            playing = Field<MediaItem>("playingItem");
            string Page(int index) => (string)backLocations[index]!.GetType().GetProperty("Page")!.GetValue(backLocations[index])!;
            check(playing.Id == first.Id && playing.Path == copyPath && Field<string>("currentPage") == "Room" &&
                backLocations.Count == 2 && Page(0) == "Home" && Page(1) == "Details",
                "a video opened at launch plays as its library record, with its details page and Home behind the player");

            await (Task)Call("OpenFileAsync", otherPath)!;
            playing = Field<MediaItem>("playingItem");
            check(playing.Path == otherPath && Field<List<MediaItem>>("items").All(item => item.Id != playing.Id) && Field<string>("currentPage") == "Room",
                "a video outside the library plays on its own");

            // A second launch hands its file to this window instead of opening another app.
            OpenFileHandoff.Claim();
            using var listening = new CancellationTokenSource();
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            OpenFileHandoff.Listen(path => received.TrySetResult(path), listening.Token);
            check(OpenFileHandoff.FileArgument(["--updated", copyPath]) == copyPath && OpenFileHandoff.FileArgument([Path.Combine(downloads, "missing.avi")]) is null,
                "only an existing file argument is opened");
            check(OpenFileHandoff.Forward(copyPath) && await received.Task.WaitAsync(TimeSpan.FromSeconds(10)) == copyPath,
                "a file opened while the app runs reaches the running window");
            // A closing app stops taking files at once, so a file opened while it shuts down opens its own window without waiting.
            OpenFileHandoff.Release();
            await Task.Delay(200);
            var forwarding = Environment.TickCount64;
            check(!OpenFileHandoff.Forward(copyPath) && OpenFileHandoff.Claimed && Environment.TickCount64 - forwarding < 2000,
                "a file opened while the running app closes starts its own window without waiting for the old one to exit");
            OpenFileHandoff.Release();
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            Set("items", originalItems);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
