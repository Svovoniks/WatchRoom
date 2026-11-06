using BlackPearl.Core;

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
        // Unmatched means nothing has identified any of a title's own files yet.
        MediaItem Episode(string id, string title, int episode) => LibraryIdentity.Ensure(new(id, id + ".mkv", title, "Show", Series: title, Season: 1, Episode: episode));
        MediaItem Film(string id, string title) => new(id, id + ".mkv", title, "Movie");
        var lostShow = Episode("lost-1", "Lost Show", 1);
        var unmatched = LibraryCatalog.UnmatchedTitles([Film("lost-film", "Lost Film"), lostShow, Episode("lost-2", "Lost Show", 2),
            Film("found-film", "Found Film") with { MetadataProvider = "tmdb", MetadataId = 5 }, Film("nfo-film", "Nfo Film") with { ProviderIds = new() { ["imdb"] = "tt1" } },
            Film("locked-film", "Locked Film") with { MetadataLocked = true }, Film("clip-only", "Clip Only") with { IsBonus = true },
            Episode("part-1", "Part Matched", 1) with { MetadataProvider = "tmdb", MetadataId = 6 }, Episode("part-2", "Part Matched", 2),
            Episode("clips-1", "Clips Unlooked", 1) with { MetadataProvider = "tmdb", MetadataId = 7 }, Episode("clips-2", "Clips Unlooked", 2) with { IsBonus = true, Episode = null },
            new MediaItem("song", "song.mp3", "Song", MusicFiles.Kind), Film("gone", "Gone") with { Available = false }, Episode("hole", "Placeholder Only", 1) with { IsVirtual = true }]);
        check(unmatched.SetEquals(["lost-film", LibraryIdentity.ShowKey(lostShow)]), $"only titles nothing has identified are unmatched (got {string.Join(", ", unmatched)})");
        // A real pass: a title starts unmatched and stays so through a failed lookup, an empty one and a rescan, until a lookup finds it.
        var root = Path.Combine(Path.GetTempPath(), "blackpearl-unmatched-" + Guid.NewGuid().ToString("N")); var media = Path.Combine(root, "media"); Directory.CreateDirectory(media);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(media, "Example Show S01E01.mkv"), [0]);
            var folder = new LibraryFolder(media);
            var store = new LibraryStore(Path.Combine(root, "library")); new MetadataOptions(Providers: ["tvmaze"]).Save(store); await store.ScanAsync([folder]);
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 1, "a newly found title starts unmatched");
            async Task Lookup(HttpMessageHandler handler)
            {
                using var client = new HttpClient(handler);
                await AutomaticArtwork.FetchAsync(store, root, null, null, default, client, force: true);
            }
            await Lookup(new CannedHandler(null));
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 1, "a failed lookup leaves a title unmatched");
            await Lookup(new CannedHandler("[]"));
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 1, "a lookup that finds nothing leaves a title unmatched");
            await store.ScanAsync([folder]);
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 1, "a rescan leaves a title unmatched");
            await Lookup(new ArtworkFixture());
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 0, "a lookup that finds a title takes it out of unmatched");
            await store.ScanAsync([folder]);
            check(LibraryCatalog.UnmatchedTitles(store.All()).Count == 0, "a rescan keeps a matched title out of unmatched");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
    // Answers every request with the same body, or fails it when there's none.
    private sealed class CannedHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => body is null
            ? throw new HttpRequestException("offline")
            : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
