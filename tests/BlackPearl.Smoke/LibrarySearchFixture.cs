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
        SearchFilters(check);
    }

    // The Search tab: text, type of title, title filters and file filters, with languages read from the files and cached.
    private static void SearchFilters(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blackpearl-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "db"));
            string Video(string name, Mp4Fixture.Audio[] audio, Mp4Fixture.Subtitle[]? subtitles = null)
            { var path = Path.Combine(directory, name); Mp4Fixture.Write(path, audio, subtitles ?? []); return path; }
            Mp4Fixture.Audio Sound(string? language = null) => new(new short[8000], 8000, language);
            // The first audio track is untagged, and was identified as French while it played.
            var dubbed = Video("Dubbed Film (2004).mp4", [Sound(), Sound("jpn")], [new("tx3g", ["Hello there."], "eng")]);
            store.CacheTrackLanguage(dubbed, LibraryStore.TrackLanguageFile(dubbed)!, true, 0, "fr");
            var plain = Video("Plain Film (1994).mp4", [Sound("eng")]);
            File.WriteAllText(Path.Combine(directory, "Plain Film (1994).ru.srt"), "1\n00:00:01,000 --> 00:00:02,000\nПривет\n");
            var avi = Path.Combine(directory, "Old Film.avi"); File.WriteAllText(avi, "avi");
            var first = Video("Search Show S01E01.mp4", [Sound("jpn")]);
            var second = Video("Search Show S01E02.mp4", [Sound("eng")]);
            var paths = new[] { dubbed, plain, avi, first, second };

            var languages = FileLanguageIndex.Read(store, paths);
            check(languages[dubbed].Audio.Order().SequenceEqual(["fr", "ja"]) && languages[dubbed].Subtitles.SequenceEqual(["en"]) && languages[dubbed].HasSubtitles,
                "the language index reads tagged tracks and takes identified languages for untagged ones");
            check(languages[plain].Audio.SequenceEqual(["en"]) && languages[plain].Subtitles.SequenceEqual(["ru"]) && languages[plain].HasSubtitles,
                "subtitle files beside a video count by the language their name gives");
            check(!languages[avi].Read && languages[avi].Audio.Length == 0 && !languages[avi].HasSubtitles, "containers whose tracks can't be listed have no known audio");
            var cached = store.CachedTrackLists();
            check(cached.Count == 4 && cached[LibraryStore.TrackLanguageFile(dubbed)!] == "a0=;a1=ja;s2=en", "the track lists of MP4 and Matroska files are cached by file version");
            var firstKey = LibraryStore.TrackLanguageFile(first)!;
            store.CacheTrackLists([(first, firstKey, "a0=de")]);
            check(FileLanguageIndex.Read(store, [first])[first].Audio.SequenceEqual(["de"]), "a cached track list is used without reading the file again");
            store.CacheTrackLists([(first, firstKey, "a0=ja")]);

            MediaItem Episode(string id, string path, int number, string title) =>
                new(id, path, "Search Show", "Anime", Series: "Search Show", Season: 1, Episode: number, EpisodeTitle: title, Year: 2015, RuntimeMinutes: 24, Genres: ["Animation"]);
            MediaItem[] items =
            [
                new("dubbed", dubbed, "Dubbed Film", "Movie", Year: 2004, Genres: ["Drama"], Rating: 7.5, FileRuntimeMinutes: 95),
                new("plain", plain, "Plain Film", "Movie", Year: 1994, Genres: ["Comedy"], Rating: 6.1, RuntimeMinutes: 130),
                new("avi", avi, "Old Film", "Movie", Year: 1961),
                Episode("first", first, 1, "The Lighthouse"), Episode("second", second, 2, "Harbour"),
                new("song", Path.Combine(directory, "song.mp3"), "Search Song", MusicFiles.Kind, Series: "Search Album", Episode: 1),
            ];
            List<LibraryCard> Find(string query, LibrarySearchFilters filters) => LibrarySearch.Search(items, query, filters, languages);
            string[] Titles(string query, LibrarySearchFilters filters) => Find(query, filters).Select(card => card.DisplayTitle).ToArray();
            check(Titles("", new()).SequenceEqual(["Dubbed Film", "Old Film", "Plain Film", "Search Show"]), "Search lists every video title once, without music");
            check(Titles("", new(Category: LibraryCatalog.Music)).SequenceEqual(["Search Album"]) && Titles("", new(Category: 3)).SequenceEqual(["Search Show"]) &&
                Titles("", new(Category: 1)).Length == 3, "the type of title narrows Search to films, anime or music");
            check(Titles("FILM", new()).Length == 3 && Titles("plain", new(AudioLanguage: "ja")).Length == 0, "text and filters combine, ignoring case");
            var episode = Find("lighthouse", new()).Single();
            check(episode.DisplayTitle == "Search Show" && episode.Caption.EndsWith("· 1 episode matches"), "an episode title finds its show and says how many episodes match");
            var japanese = Find("", new(AudioLanguage: "ja"));
            check(japanese.Select(card => card.DisplayTitle).SequenceEqual(["Dubbed Film", "Search Show"]) && japanese[1].Caption.EndsWith("· 1 of 2 files match"),
                "an audio language finds films and shows that have it, and a show says how many of its files do");
            check(Titles("", new(AudioLanguage: "fr")).SequenceEqual(["Dubbed Film"]), "languages identified for untagged tracks count");
            check(Titles("", new(SubtitleLanguage: "ru")).SequenceEqual(["Plain Film"]) && Titles("", new(SubtitleLanguage: "en")).SequenceEqual(["Dubbed Film"]),
                "a subtitle language matches embedded subtitles and subtitle files");
            check(Titles("", new(SubtitleLanguage: LibrarySearchFilters.NoSubtitles)).SequenceEqual(["Old Film", "Search Show"]), "No subtitles finds videos without any");
            check(Titles("", new(Genre: "drama")).SequenceEqual(["Dubbed Film"]) && Titles("", new(Decade: 1990)).SequenceEqual(["Plain Film"]) &&
                Titles("", new(Decade: 2010)).SequenceEqual(["Search Show"]) && Titles("", new(MinimumRating: 7)).SequenceEqual(["Dubbed Film"]),
                "genre, decade and rating filter by the title's metadata");
            check(Titles("", new(Length: SearchLength.OverTwoHours)).SequenceEqual(["Plain Film"]) && Titles("", new(Length: SearchLength.UpToHalfHour)).SequenceEqual(["Search Show"]) &&
                Titles("", new(Length: SearchLength.UpToTwoHours)).SequenceEqual(["Dubbed Film"]), "length uses the file's own runtime first, then the provider's");
            File.Delete(second);
            store.RemoveStaleTrackLanguages();
            check(store.CachedTrackLists().Count == 3 && store.CachedTrackLists().ContainsKey(firstKey), "cleanup removes the cached track lists of deleted files");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
    // Answers every request with the same body, or fails it when there's none.
    private sealed class CannedHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => body is null
            ? throw new HttpRequestException("offline")
            : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
