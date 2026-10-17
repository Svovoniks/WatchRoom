using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

// The end-of-episode action follows the playing video's current metadata, and
// a loaded queue continues only the video it started.
static class EpisodeEndChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-episodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new[] { "Show S01E01.avi", "Show S01E02.avi", "Queue movie.avi", "Queue movie two.avi" }.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var path in paths) VideoFixture.Write(path, 20);
        var first = new MediaItem("episode-end-1", paths[0], "Fixture Show", "Show", Series: "Fixture Show", Season: 1, Episode: 1);
        var second = new MediaItem("episode-end-2", paths[1], "Fixture Show", "Show", Series: "Fixture Show", Season: 1, Episode: 2);
        var movie = new MediaItem("episode-end-movie", paths[2], "Queue movie", "Movie");
        var movieTwo = new MediaItem("episode-end-movie-two", paths[3], "Queue movie two", "Movie");
        var items = Field<List<MediaItem>>("items"); var originalItems = items;
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var button = (Button)window.FindName("EpisodeEndButton");
        try
        {
            var library = items.Concat([first, second, movie, movieTwo]).ToList(); Set("items", library);
            queue.Clear(); queue.AddRange([movie, movieTwo]); Set("queuePosition", -1);
            await (Task)Call("PlayLocalItem", first)!;
            Call("UpdateSessionControls");
            check(button.Visibility == Visibility.Visible && (string)button.Content == "Next episode →",
                "an episode started from the library shows Next episode while a queue is loaded");
            check(!(bool)Call("ContinuesQueue", first)!, "a loaded queue does not replace the next episode of a library video");

            // Metadata matching assigns a show identity after playback started.
            var matched = library.Select(item => item.Series is null ? item : item with { ShowId = "show-matched", Series = "Fixture Show (2026)" }).ToList();
            Set("items", matched); Call("RefreshPlayingItem"); Call("UpdateSessionControls");
            check(Field<MediaItem>("playingItem").ShowId == "show-matched" && (string)button.Content == "Next episode →",
                "the playing episode follows library metadata updates and keeps its next episode");

            Set("queuePosition", 0); queue[0] = first;
            check((bool)Call("ContinuesQueue", first)!, "a video started from the queue continues the queue");
            Set("queuePosition", 1); queue[1] = first;
            check(!(bool)Call("ContinuesQueue", first)!, "at the end of the queue an episode offers its next episode instead");

            var library2 = Field<LibraryStore>("library");
            library2.Setting("position:" + movie.Id, "65000");
            var movieCard = new LibraryCardView(new("Queue movie", "", null, movie, "movie"));
            Call("UpdateCardPlayback", movieCard);
            check(movieCard.HasPlaybackProgress && movieCard.PlaybackLabel == "Resume · 1:05",
                "movie cards keep their resume overlay when playback progress refreshes");
            library2.Setting("position:" + movie.Id, "0");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            Set("items", originalItems);
            queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            foreach (var path in paths) File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
