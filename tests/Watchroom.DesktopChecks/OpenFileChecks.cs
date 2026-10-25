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
            listening.Cancel();
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            Set("items", originalItems);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
