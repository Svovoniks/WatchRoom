using Watchroom.Core;

static class LibrarySearchFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        var movie = new MediaItem("movie", "movie.mp4", "Example Movie", "Movie");
        var show = new MediaItem("show", "show.mkv", "Example Show", "Show", Series: "Example Show", Season: 1, Episode: 2);
        var anime = new MediaItem("anime", "anime.mkv", "Example Anime", "Anime", Series: "Example Anime", Season: 2, Episode: 1, Available: false);
        var items = new[] { movie, show, anime };
        check((await LibraryCatalog.SearchAsync(items, " eXaMpLe mOvIe ")).Single().Media.Id == movie.Id,
            "background search trims text and ignores case");
        foreach (var (filter, id) in new[] { (1, "movie"), (2, "show"), (3, "anime"), (5, "anime") })
            check((await LibraryCatalog.SearchAsync(items, "", filter)).Single().Media.Id == id,
                $"background library filter {filter} preserves category/availability rules");
        check((await LibraryCatalog.SearchAsync(items, "", 4)).Count == 3, "background artwork filter finds missing posters");
        check((await LibraryCatalog.SearchAsync(items, "Season 1", series: show.Series, kind: show.Kind)).Single().Season == 1,
            "background search preserves series scope");
        check((await LibraryCatalog.SearchAsync(items, "Episode 2", series: show.Series, kind: show.Kind, season: 1)).Single().Media.Id == show.Id,
            "background search preserves episode scope");
        check((await LibraryCatalog.SearchAsync(items, "absent")).Count == 0, "background search returns no matches");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var rejected = false;
        try { await LibraryCatalog.SearchAsync(items, "", cancellation: cancelled.Token); }
        catch (OperationCanceledException) { rejected = true; }
        check(rejected, "background search respects cancellation before starting");

        using var firstCancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId; var workerThread = callerThread;
        IEnumerable<MediaItem> SlowSource()
        {
            workerThread = Environment.CurrentManagedThreadId; entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Search fixture was not released");
            yield return movie;
        }
        var first = LibraryCatalog.SearchAsync(SlowSource(), "", cancellation: firstCancellation.Token);
        try
        {
            check(entered.Wait(TimeSpan.FromSeconds(10)) && workerThread != callerThread && !first.IsCompleted,
                "slow library enumeration runs off the caller thread");
            firstCancellation.Cancel();
            check((await LibraryCatalog.SearchAsync(items, "Movie")).Single().Media.Id == movie.Id,
                "new search completes while an older source is blocked");
        }
        finally { release.Set(); }
        rejected = false;
        try { await first; } catch (OperationCanceledException) { rejected = true; }
        check(rejected, "superseded search cancels during enumeration instead of returning stale results");
    }
}
