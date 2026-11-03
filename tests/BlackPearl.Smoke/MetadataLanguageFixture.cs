using System.Net;
using System.Text;
using BlackPearl.Core;

// Changing the metadata language refetches titles and plots once, and films show the provider's name in that language.
static class MetadataLanguageFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var poster = Path.Combine(directory, "poster.png"); await File.WriteAllBytesAsync(poster, ArtworkFixture.Png);
        var store = new LibraryStore(Path.Combine(directory, "db"));
        var english = new MetadataOptions(Providers: ["tmdb"]); english.Save(store);
        var film = new MediaItem("matrix", Path.Combine(directory, "Movies", "The Matrix (1999).mkv"), "The Matrix", "Movie", 1999, Poster: poster, SourceLibraryKind: "Movie");
        var episode = new MediaItem("owl", Path.Combine(directory, "Shows", "Owl", "Owl S01E01.mkv"), "Owl", "Show", 2016, Series: "Owl", Season: 1, Episode: 1,
            Poster: poster, SeriesPoster: poster, ShowId: "owl-show", MetadataProvider: "tmdb", MetadataId: 333, MetadataKind: "Show", MetadataType: "tv",
            ProviderIds: new() { ["tmdb"] = "333" }, SourceLibraryKind: "Show");
        store.Save(film); store.Save(episode);
        var handler = new LanguageHandler(); using var http = new HttpClient(handler);

        using (var tmdb = new MetadataClient("fixture", handler, english))
            await AutomaticArtwork.FetchAsync(store, directory, "fixture", null, default, http, metadata: tmdb);
        var matched = store.ByIds(["matrix"]).Single();
        check(matched is { ProviderTitle: "The Matrix", Title: "The Matrix", Overview: "A hacker learns the truth." }, "an automatic film match records the provider's title in the metadata language");
        check(MetadataOptions.FetchedLocale(store) == english.Locale, "a library never refreshed in another locale counts as fetched in the default one");

        handler.Requests.Clear();
        using (var tmdb = new MetadataClient("fixture", handler, english))
            await AutomaticArtwork.FetchAsync(store, directory, "fixture", null, default, http, metadata: tmdb);
        check(!handler.Requests.Any(x => x.AbsolutePath.Contains("/movie")), "a complete library in the same locale is not fetched again");

        var russian = english with { Language = "ru-RU" }; russian.Save(store);
        handler.Requests.Clear();
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await AutomaticArtwork.FetchAsync(store, directory, "fixture", null, default, http, metadata: tmdb);
        var translated = store.ByIds(["matrix"]).Single();
        check(handler.Requests.Any(x => x.AbsolutePath.EndsWith("/movie/603") && x.Query.Contains("language=ru-RU")), "a new metadata language refetches complete titles in that language");
        check(translated is { ProviderTitle: "Матрица", Title: "The Matrix", Overview: "Хакер узнаёт правду." } && translated.ShownTitle == "Матрица" && translated.DisplayTitle == "The Matrix",
            "a film's shown title and plot follow the new language while its file title still drives matching");
        var show = store.ByIds(["owl"]).Single();
        check(show is { Series: "Сова", EpisodeTitle: "Пилот", ProviderTitle: null }, "a show's name and episode titles follow the new language");
        check(MetadataOptions.FetchedLocale(store) == russian.Locale, "the refreshed locale is recorded once the refresh completes");
        check(translated.TmdbFields?.ContainsKey("providerTitle") == true &&
            TmdbRetention.Expire(Wire.Serialize(translated), Wire.Now + (long)TmdbRetention.MaximumAge.TotalMilliseconds + 1, out _) is var expired &&
            Wire.Read<MediaItem>(expired) is { ProviderTitle: null, Title: "The Matrix" }, "a provider title expires with the rest of TMDB's data");

        handler.Requests.Clear();
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await AutomaticArtwork.FetchAsync(store, directory, "fixture", null, default, http, metadata: tmdb);
        check(!handler.Requests.Any(x => x.AbsolutePath.Contains("/movie")), "the language refresh happens once, not on every fetch");

        var card = LibraryCatalog.Browse(store.All()).Single(x => x.Media.Id == "matrix");
        check(card.DisplayTitle == "Матрица" && translated.QueueTitle == "Матрица" && translated.EpisodeDisplayTitle == "Матрица", "library cards, queues and details show the translated film title");
        check(LibraryCatalog.Search(store.All(), "Матр").Any(x => x.Media.Id == "matrix") && LibraryCatalog.Search(store.All(), "Matrix").Any(x => x.Media.Id == "matrix"),
            "a film is found by either its shown or its file title");
        check((translated with { LockedFields = ["Title"], Title = "Matrix (my cut)" }).ShownTitle == "Matrix (my cut)" &&
            (translated with { LocalMetadataFields = ["Title"] }).ShownTitle == "The Matrix", "a corrected or NFO title wins over the provider's");
        var match = new MetadataMatch(603, "movie", "Матрица", "1999", null, "Хакер узнаёт правду.", "Movie", Provider: "tmdb");
        check(MetadataIdentification.Apply(film, match, null).ProviderTitle == "Матрица", "a manual film match records the provider's title too");

        // A build without TMDB, or TMDB failing, must not move TMDB titles to the next provider while relocalizing.
        var keyless = new LibraryStore(Path.Combine(directory, "keyless-db")); russian.Save(keyless);
        check(!AutomaticArtwork.Relocalizes(keyless, russian with { Providers = null }, tmdbAvailable: false) &&
            AutomaticArtwork.Relocalizes(keyless, russian with { Providers = null }, tmdbAvailable: true) &&
            AutomaticArtwork.Relocalizes(keyless, russian with { Providers = ["wikipedia"] }, tmdbAvailable: false), "the language refresh waits while TMDB is chosen but unavailable");
        var both = russian with { Providers = ["tmdb", "wikipedia"] };
        var english603 = matched with { MetadataProvider = "tmdb", MetadataId = 603, MetadataKind = "Movie", MetadataType = "movie", ProviderIds = new() { ["tmdb"] = "603" }, ArtworkFetchedAt = Wire.Now, TmdbFields = null };
        var outage = new LibraryStore(Path.Combine(directory, "outage-db")); both.Save(outage); outage.Save(english603);
        handler.Requests.Clear(); handler.TmdbDown = true;
        using (var tmdb = new MetadataClient("fixture", handler, both))
            await AutomaticArtwork.FetchAsync(outage, directory, "fixture", null, default, http, metadata: tmdb);
        handler.TmdbDown = false;
        check(outage.ByIds(["matrix"]).Single() is { MetadataProvider: "tmdb", MetadataId: 603, ProviderTitle: "The Matrix" } && !handler.Requests.Any(x => x.Host.EndsWith("wikipedia.org")),
            "a TMDB title keeps its match when TMDB fails during a language refresh");
        var drifted = new LibraryStore(Path.Combine(directory, "drifted-db")); both.Save(drifted);
        drifted.Save(english603 with { MetadataProvider = "wikipedia:ru", MetadataId = 4242, ProviderIds = new() { ["tmdb"] = "603", ["wikipedia:ru"] = "4242" }, ProviderTitle = "Матрица (Википедия)" });
        handler.Requests.Clear();
        using (var tmdb = new MetadataClient("fixture", handler, both))
            await AutomaticArtwork.FetchAsync(drifted, directory, "fixture", null, default, http, metadata: tmdb);
        check(drifted.ByIds(["matrix"]).Single() is { MetadataProvider: "tmdb", MetadataId: 603, ProviderTitle: "Матрица" }, "a title moved off TMDB returns to it in a language refresh with TMDB available");

        // Choosing a country after the language while the refresh runs: the running fetch stops, and only the final locale is recorded.
        var changing = new LibraryStore(Path.Combine(directory, "changing-db")); russian.Save(changing);
        changing.Save(english603); changing.Save(english603 with { Id = "drive", Path = Path.Combine(directory, "Movies", "Drive (2011).mkv"), Title = "Drive", Year = 2011, MetadataId = 604, ProviderIds = new() { ["tmdb"] = "604" } });
        var russia = russian with { Country = "RU" };
        handler.Requests.Clear(); handler.OnTmdb = () => { russia.Save(changing); handler.OnTmdb = null; };
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await AutomaticArtwork.FetchAsync(changing, directory, "fixture", null, default, http, metadata: tmdb);
        var asked = new[] { "/movie/603", "/movie/604" }.Count(id => handler.Requests.Any(x => x.AbsolutePath.EndsWith(id)));
        check(asked == 1 && MetadataOptions.FetchedLocale(changing) == english.Locale, "a fetch stops when the locale changes under it and does not record the old one");
        handler.Requests.Clear();
        using (var tmdb = new MetadataClient("fixture", handler, russia))
            await AutomaticArtwork.FetchAsync(changing, directory, "fixture", null, default, http, metadata: tmdb);
        check(handler.Requests.Any(x => x.AbsolutePath.EndsWith("/movie/603")) && handler.Requests.Any(x => x.AbsolutePath.EndsWith("/movie/604")) &&
            MetadataOptions.FetchedLocale(changing) == russia.Locale && changing.ByIds(["drive"]).Single().ProviderTitle == "Драйв", "the next fetch refreshes every title in the final locale and records it");

        // The episode phase reports its stop, so switching the locale away and back during it does not count the refresh as complete.
        var shows = new LibraryStore(Path.Combine(directory, "shows-db")); russian.Save(shows);
        shows.Save(episode); shows.Save(episode with { Id = "lynx", Path = Path.Combine(directory, "Shows", "Lynx", "Lynx S01E01.mkv"), Title = "Lynx", Series = "Lynx", ShowId = "lynx-show", MetadataId = 334, ProviderIds = new() { ["tmdb"] = "334" } });
        var stopped = false; handler.Requests.Clear();
        handler.OnTmdb = () => { if (handler.Requests.Any(x => x.AbsolutePath.Contains("/season/"))) { russia.Save(shows); handler.OnTmdb = null; } };
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await SeriesMetadata.FetchAsync(shows, directory, "fixture", null, default, http, metadata: tmdb, stopOnLocaleChange: () => stopped = true);
        check(stopped && handler.Requests.Count(x => x.AbsolutePath.Contains("/season/")) == 1, "the episode phase stops at the next show once the locale changes, and says so");
        var asked2 = new LibraryStore(Path.Combine(directory, "shows-asked-db")); russian.Save(asked2);
        foreach (var entry in shows.All()) asked2.Save(entry with { MetadataFetchedAt = 0, EpisodeTitle = null, TmdbFields = null });
        handler.Requests.Clear();
        handler.OnTmdb = () => { if (handler.Requests.Any(x => x.AbsolutePath.Contains("/season/"))) { russia.Save(asked2); handler.OnTmdb = null; } };
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await SeriesMetadata.FetchAsync(asked2, directory, "fixture", null, default, http, metadata: tmdb);
        check(handler.Requests.Count(x => x.AbsolutePath.Contains("/season/")) == 2, "a fetch nobody asked to stop keeps going through every show");

        // A full automatic fetch passes that stop through: a locale change in the episode phase leaves the remaining shows and the locale for the follow-up.
        var full = new LibraryStore(Path.Combine(directory, "shows-full-db")); russian.Save(full);
        foreach (var entry in asked2.All()) full.Save(entry with { MetadataFetchedAt = 0, EpisodeTitle = null, ArtworkFetchedAt = 0, TmdbFields = null });
        handler.Requests.Clear();
        handler.OnTmdb = () => { if (handler.Requests.Any(x => x.AbsolutePath.Contains("/season/"))) { russia.Save(full); handler.OnTmdb = null; } };
        using (var tmdb = new MetadataClient("fixture", handler, russian))
            await AutomaticArtwork.FetchAsync(full, directory, "fixture", null, default, http, metadata: tmdb);
        check(handler.Requests.Count(x => x.AbsolutePath.Contains("/season/")) == 1 && MetadataOptions.FetchedLocale(full) == english.Locale,
            "an automatic fetch stops its episode phase on a locale change and leaves the locale unrecorded");

        check(WikipediaMetadata.Title("Тачки (мультфильм)") == "Тачки" && WikipediaMetadata.Title("Шрэк (мультфильм, 2001)") == "Шрэк" &&
            WikipediaMetadata.Title("Шерлок (телефильм)") == "Шерлок" && WikipediaMetadata.Title("Drive (2011 film)") == "Drive" &&
            WikipediaMetadata.Title("Матрица (фильм)") == "Матрица" && WikipediaMetadata.Title("Солярис") == "Солярис", "Wikipedia film disambiguations, animated and TV films included, are not shown in titles");
    }

    private sealed class LanguageHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool TmdbDown;
        public Action? OnTmdb;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; Requests.Add(uri);
            if (uri.Host == "image.tmdb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) });
            if (TmdbDown && uri.Host == "api.themoviedb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (uri.Host.EndsWith("wikipedia.org")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"query":{"pages":[]}}""", Encoding.UTF8, "application/json") });
            if (uri.Host == "api.themoviedb.org") OnTmdb?.Invoke();
            var ru = uri.Query.Contains("language=ru-RU");
            var drive = uri.AbsolutePath.EndsWith("/movie/604");
            var movie = $$"""{"id":603,"title":"{{(ru ? "Матрица" : "The Matrix")}}","original_title":"The Matrix","release_date":"1999-03-31","poster_path":"/matrix.jpg","overview":"{{(ru ? "Хакер узнаёт правду." : "A hacker learns the truth.")}}","genre_ids":[28],"genres":[{"id":28,"name":"Action"}],"runtime":136}""";
            var json = uri.AbsolutePath switch
            {
                var p when p.EndsWith("/search/movie") => "{\"results\":[" + movie + "]}",
                var p when p.EndsWith("/movie/603") => movie,
                var p when drive => $$"""{"id":604,"title":"{{(ru ? "Драйв" : "Drive")}}","original_title":"Drive","release_date":"2011-09-15","poster_path":"/drive.jpg","overview":"{{(ru ? "Водитель." : "A driver.")}}","genres":[{"id":18,"name":"Drama"}],"runtime":100}""",
                var p when p.EndsWith("/tv/334/season/1") => $$"""{"name":"Season 1","episodes":[{"episode_number":1,"name":"{{(ru ? "Начало" : "Start")}}","overview":"","air_date":"2017-01-01"}]}""",
                var p when p.EndsWith("/tv/334") => $$"""{"id":334,"name":"{{(ru ? "Рысь" : "Lynx")}}","original_name":"Lynx","first_air_date":"2017-01-01","overview":"","genres":[{"id":18}],"seasons":[{"season_number":1}],"number_of_seasons":1,"number_of_episodes":1}""",
                var p when p.EndsWith("/tv/333/season/1") => $$"""{"name":"Season 1","episodes":[{"episode_number":1,"name":"{{(ru ? "Пилот" : "Pilot")}}","overview":"","air_date":"2016-01-01"}]}""",
                var p when p.EndsWith("/tv/333") => $$"""{"id":333,"name":"{{(ru ? "Сова" : "Owl")}}","original_name":"Owl","first_air_date":"2016-01-01","overview":"{{(ru ? "Описание" : "A show about an owl.")}}","genres":[{"id":18}],"seasons":[{"season_number":1}],"number_of_seasons":1,"number_of_episodes":1}""",
                _ => """{"results":[],"tv_results":[],"movie_results":[]}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
