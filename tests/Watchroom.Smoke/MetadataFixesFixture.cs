using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Watchroom.Core;

static class MetadataFixesFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        await Wikipedia(directory, check);
        await Parsing(directory, check);
        await Images(directory, check);
        await ReportsAndPicker(directory, check);
    }
    private static async Task Wikipedia(string directory, Action<bool, string> check)
    {
        foreach (var (query, canonical, year) in new[] { ("Glass Onion A Knives Out Mystery", "Glass Onion: A Knives Out Mystery", 2022),
            ("John Wick Chapter 3 Parabellum", "John Wick: Chapter 3 – Parabellum", 2019), ("The Man from U N C L E", "The Man from U.N.C.L.E.", 2015) })
        {
            using var handler = new Handler(request => Json(request.RequestUri!.Query.Contains("generator=search") ? Pages(Page(42, canonical, year)) : "{\"query\":{\"pages\":[]}}"));
            using var http = new HttpClient(handler);
            var item = new MediaItem("movie", "movie.mkv", query, "Movie", year, SourceLibraryKind: "Movie");
            var result = await WikipediaMetadata.MatchAsync(http, item, [query], new(), default);
            check(result?.Title == canonical && result.Id == 42 && handler.Requests.Count == 2,
                "candidate search recovers canonical punctuation: " + query);
        }
        using (var handler = new Handler(_ => Json(Pages(Page(1, "Same", 1990), Page(2, "Same", 2020)))))
        using (var http = new HttpClient(handler))
        {
            check(await WikipediaMetadata.MatchAsync(http, new("same", "same.mkv", "Same", "Movie"), ["Same"], new(), default) is null,
                "Wikipedia remakes with no year remain ambiguous");
            check((await WikipediaMetadata.MatchAsync(http, new("same", "same.mkv", "Same", "Movie", 2020), ["Same"], new(), default))?.Id == 2,
                "Wikipedia candidate validation rejects a different release year");
        }
        var ruPage = new { pageid = 11, title = "Берегись автомобиля", extract = "Советский фильм 1966 года.", categories = new[] { new { title = "Категория:Фильмы 1966 года" } },
            langlinks = new[] { new { lang = "en", title = "Beware of the Car" } } };
        using (var handler = new Handler(request => Json(request.RequestUri!.Host == "ru.wikipedia.org" ? Pages(ruPage) : Pages(Page(22, "Beware of the Car", 1966)))))
        using (var http = new HttpClient(handler))
        {
            var result = await WikipediaMetadata.MatchAsync(http, new("ru", "ru.mkv", "Beregis Avtomobilya", "Movie", 1966), ["берегис автомобиля"], new(), default);
            check(result?.Title == "Beware of the Car" && result.Details?.ProviderIds?["wikipedia:ru"] == "11" && result.Details.ProviderIds["wikipedia"] == "22",
                "verified Russian language link supplies English text and separate edition IDs");
            handler.Requests.Clear();
            using var knownHttp = new HttpClient(handler, false);
            var known = new MediaItem("known", "known.mkv", "Берегись автомобиля", "Movie", 1966, MetadataProvider: "wikipedia:ru", MetadataId: 11);
            result = await WikipediaMetadata.MatchAsync(knownHttp, known, [known.Title], new(Language: "ru-RU"), default);
            check(result?.Provider == "wikipedia:ru" && handler.Requests.Single().Host == "ru.wikipedia.org" && handler.Requests.Single().Query.Contains("pageids=11"),
                "a Russian page ID is never requested from English Wikipedia");
        }
        check(MetadataTitles.RussianSearchAlias("Brilliantovaya Ruka") == "бриллиантовая рука" && MetadataTitles.RussianSearchAlias("Dzhentlmeny Udachi") == "джентлмены удачи" &&
            MetadataTitles.RussianSearchAlias("Operatsiya Y I Drugie Priklyucheniya Shurika") == "операция ы и другие приключения шурика", "Russian digraphs and word-final y preserve search spelling");
        check(new[] { "John Wick Chapter 3 Parabellum", "Sherlock Holmes A Game Of Shadows", "The Hitchhikers Guide to the Galaxy" }.All(q => MetadataTitles.RussianSearchAlias(q) is null),
            "English ch and sh do not generate garbage Cyrillic aliases");
        check(MetadataTitles.Queries(new("clean", "film.mkv", "Example Movie HDDVDRip 1080p", "Movie")).First() == "Example Movie", "release noise is removed before the first provider query");
        using (var handler = new Handler(_ => Json("[{\"show\":{\"id\":1,\"name\":\"Example Show\",\"premiered\":\"2020-01-01\",\"type\":\"Scripted\",\"url\":\"https://www.tvmaze.com/shows/1\",\"summary\":\"Plot\"}}]")))
        using (var http = new HttpClient(handler))
        {
            var result = await AutomaticArtwork.ResolveAsync(http, null, new("clean-show", "episode.mkv", "Example Show HDDVDRip 1080p", "Show", 2020, Series: "Example Show HDDVDRip 1080p", Episode: 1), new(Providers: ["tvmaze"]), directory, default);
            check(result?.Id == 1 && handler.Requests.Single().Query.Contains("q=Example%20Show"), "TVmaze searches and compares the cleaned first query");
        }
        using (var metadata = new MetadataClient("fixture-token", new TmdbFixture(movie: true)))
            check((await metadata.MatchAsync(new("clean-film", "film.mkv", "Example Movie HDDVDRip 1080p", "Movie", 2020, SourceLibraryKind: "Movie")))?.Id == 2,
                "TMDB searches and compares the cleaned first query");
        using (var handler = new Handler(request => Json(request.RequestUri!.Query.Contains("generator=search") ? Pages(Page(1, "Same", 1990), Page(2, "Same", 2020)) : Pages(Page(1, "Same", 1990)))))
        using (var http = new HttpClient(handler))
            check(await WikipediaMetadata.MatchAsync(http, new("remake", "remake.mkv", "Same", "Movie"), ["Same"], new(), default) is null,
                "an exact Wikipedia page without a local year still checks competing remakes");
        using (var handler = new Handler(request => request.RequestUri!.Host == "ru.wikipedia.org" ? Json(Pages(ruPage)) : new(HttpStatusCode.ServiceUnavailable)))
        using (var http = new HttpClient(handler))
        {
            var result = await WikipediaMetadata.MatchAsync(http, new("native", "native.mkv", "Берегись автомобиля", "Movie", 1966), ["Берегись автомобиля"], new(), default);
            check(result?.Id == 11 && result.Provider == "wikipedia:ru", "a failed preferred-language lookup retains verified native identity and text");
        }
        using (var handler = new Handler(request => request.RequestUri!.Host == "ru.wikipedia.org" ? Json(Pages(ruPage)) : throw new TaskCanceledException("fixture timeout")))
        using (var http = new HttpClient(handler))
        {
            var result = await WikipediaMetadata.MatchAsync(http, new("timeout", "timeout.mkv", "Берегись автомобиля", "Movie", 1966), ["Берегись автомобиля"], new(), default);
            check(result?.Id == 11 && result.Provider == "wikipedia:ru", "a preferred-language timeout preserves the verified native match");
        }
        using (var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/w/api.php" ? Limited() : new(HttpStatusCode.NotFound)))
        using (var http = new HttpClient(handler))
        {
            var failed = false;
            try { await WikipediaMetadata.MatchAsync(http, new("rate", "rate.mkv", "Missing", "Movie", 2020), ["Missing"], new(), default); }
            catch (HttpRequestException ex) { failed = ex.StatusCode == HttpStatusCode.TooManyRequests; }
            check(failed, "exhausted Wikipedia 429 remains a provider failure when fallback finds nothing");
        }
    }
    private static async Task Parsing(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "files"); var folder = Path.Combine(root, "Anime", "Smeshariki"); Directory.CreateDirectory(folder);
        var paths = new[] { "079-080. Space Odyssey.mp4", "149-150. Ancient Treasure.mp4", "151-152. New Year.mp4" }.Select(n => Path.Combine(folder, n)).ToArray();
        foreach (var path in paths)
        {
            await File.WriteAllTextAsync(path, "video fixture"); var parsed = FileNames.Parse(path);
            check(parsed.Series == "Smeshariki" && parsed.AbsoluteEpisodeEnd == parsed.AbsoluteEpisode + 1 && parsed.NumberingOrder == "absolute" && parsed.ShowId is not null,
                "leading episode range retains parent show and both numbers: " + Path.GetFileName(path));
        }
        var store = new LibraryStore(Path.Combine(directory, "parse-db"));
        foreach (var path in paths) store.Save(FileNames.Parse(path) with { Title = Path.GetFileNameWithoutExtension(path), Series = null, Episode = null, AbsoluteEpisode = null, EpisodeEnd = null,
            AbsoluteEpisodeEnd = null, ShowId = null, SeasonId = null, NumberingOrder = "aired" });
        await store.ScanAsync([new(root)]);
        check(store.All().All(x => x.Series == "Smeshariki" && x.AbsoluteEpisodeEnd is not null && x.NumberingOrder == "absolute"), "rescan repairs old range-as-movie records without changing file IDs");
        var outlaw = Path.Combine(root, "Anime", "Outlaw Star"); Directory.CreateDirectory(outlaw);
        foreach (var name in new[] { "NCOP1", "NCED2", "BD-Box CM", "DVD Memorial CM Collection", "Promo", "Preview", "Pilot Film" })
        {
            var path = Path.Combine(outlaw, "Outlaw Star - " + name + " (BDRip 1080p).mkv"); await File.WriteAllTextAsync(path, "video fixture");
            var parsed = FileNames.Parse(path);
            check(parsed.IsBonus && !parsed.IsExtra && parsed.Series == "Outlaw Star" && parsed.Season == 0 && parsed.Episode is null,
                "bonus content stays accessible under its show without guessed numbering: " + name);
        }
        await store.ScanAsync([new(root)]);
        check(LibraryCatalog.Browse(store.All()).Count == 2 && LibraryCatalog.Browse(store.All(), store.All().First(x => x.IsBonus).ShowId, season: 0).Count == 7,
            "bonus clips appear in the show's Specials rather than as seven movies");
        check(FileNames.Parse(Path.Combine(directory, "Movies", "079-080 Space Adventure.mkv")).Series is null && !FileNames.Parse(Path.Combine(directory, "Movies", "The Pilot Film (2020).mkv")).IsBonus,
            "movie numbers and words are not mistaken for episode ranges or bonus content");
        var pilot = Path.Combine(outlaw, "Outlaw Star - Pilot Film (BDRip 1080p).mkv");
        await File.WriteAllTextAsync(Path.ChangeExtension(pilot, ".nfo"), "<episodedetails><season>0</season><episode>1</episode><title>Pilot</title></episodedetails>");
        check(FileNames.Parse(pilot) is { IsBonus: false, Season: 0, Episode: 1 }, "explicit NFO numbering promotes a pilot bonus clip to a real special");
        await ShowFolderStrays(Path.Combine(directory, "strays"), check);
    }
    // Specials and disc clips kept beside a show's episodes belong to the show, not the movie shelf.
    private static async Task ShowFolderStrays(string root, Action<bool, string> check)
    {
        async Task<string> At(params string[] parts) { var path = Path.Combine([root, .. parts]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "video fixture"); return path; }
        var mixed = Path.Combine(root, "Mixed");
        var azumanga = new[] { "Azumanga Daioh - 01 - Entrance Ceremony.mkv", "Azumanga Daioh - 02 - Kimura.mkv", "Azumanga Daioh - XX - Original Opening Sequence.mkv",
            "Azumanga Daioh - XX - Movie Character Art Gallery.mkv", "Azumanga Daioh - XX - The Very Short Azumanga Daioh Movie.mkv" };
        var paths = new List<string>(); foreach (var name in azumanga) paths.Add(await At("Mixed", "Azumanga Daioh", name));
        var episode = FileNames.Parse(paths[0], "Mixed", mixed);
        var clips = paths.Skip(2).Select(path => FileNames.Parse(path, "Mixed", mixed)).ToArray();
        check(clips.All(clip => clip is { IsBonus: true, IsExtra: false, Kind: "Show", Season: 0, Episode: null, Series: "Azumanga Daioh" } && clip.ShowId == episode.ShowId) &&
            clips.Select(clip => clip.EpisodeTitle).SequenceEqual(["Original Opening Sequence", "Movie Character Art Gallery", "The Very Short Azumanga Daioh Movie"]),
            "unnumbered clips beside a show's numbered episodes are its bonus clips, titled without the show or XX placeholder");

        var onePiece = new List<string>(); foreach (var name in new[] { "One Piece - 1100.mkv", "One Piece - 1101.mkv", "One Piece - Egghead SP4.mkv", "One Piece - Egghead SP11.mkv" })
            onePiece.Add(await At("Mixed", "One Piece", name));
        var opEpisode = FileNames.Parse(onePiece[0], "Mixed", mixed);
        var sp4 = FileNames.Parse(onePiece[2], "Mixed", mixed); var sp11 = FileNames.Parse(onePiece[3], "Mixed", mixed);
        check(sp4 is { IsBonus: false, Kind: "Show", Season: 0, Episode: 4, Series: "One Piece" } && sp11 is { Season: 0, Episode: 11 } &&
            sp4.ShowId == opEpisode.ShowId && sp11.ShowId == opEpisode.ShowId, "SP-numbered files are numbered specials of the show beside them");
        var anime = Path.Combine(root, "Anime");
        var animeSp = FileNames.Parse(await At("Anime", "One Piece", "One Piece - Egghead SP6.mkv"), "Anime", anime);
        await At("Anime", "One Piece", "One Piece - 1102.mkv");
        check(animeSp is { Kind: "Anime", Season: 0, Episode: 6, Series: "One Piece", NumberingConflict: null } &&
            FileNames.Parse(await At("Anime", "Show", "Season 1", "Show - OVA2.mkv"), "Anime", anime) is { Season: 0, Episode: 2, Series: "Show", NumberingConflict: null },
            "SP and OVA specials join the anime folder's show, including inside a season folder");
        await At("Shows", "Hazbin Hotel", "Season 1", "Hazbin Hotel - S01E01.mkv"); await At("Shows", "Hazbin Hotel", "Season 1", "Hazbin Hotel - S01E02.mkv");
        var halves = Path.Combine("Anime", "One Piece", "One Piece Season 14 (Marineford) EP 459-516");
        await At(halves, "[AnimeRG] One Piece - 491 [1080p].mkv"); await At(halves, "[AnimeRG] One Piece - 493 [1080p].mkv");
        check(FileNames.Parse(await At("Shows", "Hazbin Hotel", "Season 1", "HAZBIN HOTEL (PILOT).mp4"), "Mixed", Path.Combine(root, "Shows")) is { IsBonus: true, Season: 0, EpisodeTitle: "PILOT" } &&
            FileNames.Parse(await At(halves, "[AnimeRG] One Piece - 492A [1080p].mkv"), "Anime", anime) is { IsBonus: false, Season: 14 },
            "a pilot in a season folder is a clip titled without its brackets, but half of a numbered episode keeps its season");

        // A film in a folder of unrelated episodes, or beside a single numbered file, stays a film.
        await At("Mixed", "Show - 01.mkv"); await At("Mixed", "Show - 02.mkv");
        check(FileNames.Parse(await At("Mixed", "Inception (2010).mkv"), "Mixed", mixed) is { Kind: "Movie", Series: null, IsBonus: false } &&
            FileNames.Parse(await At("Mixed", "Taxi", "Taxi 2 - 1080p.mkv"), "Mixed", mixed) is { Series: null } &&
            FileNames.Parse(await At("Mixed", "Taxi", "Taxi (1998).mkv"), "Mixed", mixed) is { Kind: "Movie", Series: null, IsBonus: false } &&
            FileNames.Parse(await At("Movies", "Azumanga Daioh", "Azumanga Daioh - XX - Original Closing Sequence.mkv"), "Movie", Path.Combine(root, "Movies")) is { Kind: "Movie", IsBonus: false },
            "films beside unrelated or lone numbered files, and anything in a Movies folder, stay films");
        var downloads = Path.Combine(root, "Downloads");
        foreach (var show in new[] { "Arrow", "Show", "Fargo", "Westworld" }) { await At("Downloads", show + " S01E01.mkv"); await At("Downloads", show + " S01E02.mkv"); }
        await At("Downloads", "Fargo", "Fargo S01E01.mkv"); await At("Downloads", "Fargo", "Fargo S01E02.mkv");
        var films = new List<MediaItem>();
        foreach (var name in new[] { "Arrowhead (2016).mkv", "Showgirls (1995).mkv", "Fargo (1996).mkv", "Westworld 1973 1080p.mkv" }) films.Add(FileNames.Parse(await At("Downloads", name), "Mixed", downloads));
        films.Add(FileNames.Parse(await At("Downloads", "Fargo", "Fargo (1996).mkv"), "Mixed", downloads));
        check(films.All(film => film is { Kind: "Movie", Series: null, IsBonus: false }) &&
            FileNames.Parse(await At("Downloads", "Arrow - Pilot Presentation.mkv"), "Mixed", downloads) is { IsBonus: true, Series: "Arrow", EpisodeTitle: "Pilot Presentation" },
            "a film whose name only starts like a show's, or is the show's name and a year, stays a film beside that show's episodes");
        var movies = Path.Combine(root, "MovieRoot");
        await At("MovieRoot", "Akira (1988).mkv");
        foreach (var name in new[] { "Hellsing Ultimate OVA 1.mkv", "Hellsing Ultimate OVA 2.mkv" })
            check(FileNames.Parse(await At("MovieRoot", name), "Movie", movies) is { Kind: "Movie", Series: null, Season: null, Episode: null },
                "an OVA-numbered file in a Movies folder stays a film: " + name);

        // Folders listed before this change fix themselves on the next scan.
        var store = new LibraryStore(Path.Combine(root, "db"));
        store.Save(FileNames.Parse(paths[2], "Movie", mixed) with { Kind = "Movie", Series = null, ShowId = null, IsBonus = false });
        store.Save(FileNames.Parse(onePiece[2], "Movie", mixed) with { Kind = "Movie", Series = null, ShowId = null, Season = null, Episode = null });
        await store.ScanAsync([new(mixed)]);
        var all = store.All();
        check(all.Single(x => x.Path == paths[2]) is { IsBonus: true, Kind: "Show", Season: 0 } rescanned && rescanned.ShowId == all.Single(x => x.Path == paths[0]).ShowId &&
            all.Single(x => x.Path == onePiece[2]) is { Season: 0, Episode: 4 } special && special.ShowId == all.Single(x => x.Path == onePiece[0]).ShowId &&
            !LibraryCatalog.Browse(all).Any(card => card.Media.Series is null && (card.Media.Path.Contains("Azumanga") || card.Media.Path.Contains("One Piece"))),
            "a rescan moves clips and specials stored as movies under their show");

        check(MetadataTitles.Queries(FileNames.Parse(await At("Movies", "Hyakuemu - Movie.mkv"), "Movie", Path.Combine(root, "Movies"))).Contains("Hyakuemu") &&
            !MetadataTitles.Queries(FileNames.Parse(await At("Movies", "Scary Movie (2000).mkv"), "Movie", Path.Combine(root, "Movies"))).Contains("Scary"),
            "a film labelled '- Movie' is also searched without the label, but a title ending in Movie keeps it");
    }
    private static async Task Images(string directory, Action<bool, string> check)
    {
        var largeImage = new byte[11 * 1024 * 1024]; ArtworkFixture.Png.CopyTo(largeImage, 0);
        using (var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(largeImage) }))
        using (var http = new HttpClient(handler))
        {
            check((await MetadataHttp.GetImage(http, "https://static.tvmaze.com/large.png", default)).Length == largeImage.Length,
                "artwork between 10 and 20 MB is accepted independently of the JSON limit");
            var rejected = false;
            try { await MetadataHttp.GetBytes(http, "https://api.tvmaze.com/large-json", default); } catch (IOException) { rejected = true; }
            check(rejected, "metadata responses retain their 10 MB budget");
        }
        using (var handler = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentLength = 21 * 1024 * 1024; return response; }))
        using (var http = new HttpClient(handler))
        {
            var rejected = false;
            try { await MetadataHttp.GetImage(http, "https://static.tvmaze.com/excessive.png", default); } catch (IOException) { rejected = true; }
            check(rejected, "artwork exceeding 20 MB is rejected before reading its body");
        }
        using (var wide = new SkiaSharp.SKBitmap(8193, 1))
        using (var encodedWide = wide.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        using (var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(encodedWide.ToArray()) }))
        using (var http = new HttpClient(handler))
        {
            var rejected = false;
            try { await MetadataHttp.GetImage(http, "https://static.tvmaze.com/wide.png", default); } catch (IOException) { rejected = true; }
            check(rejected, "excessive image dimensions are rejected even when the file is small");
        }
        using var bitmap = new SkiaSharp.SKBitmap(2, 2); bitmap.Erase(SkiaSharp.SKColors.Red);
        using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 90);
        using (var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(encoded.ToArray()) }))
        using (var http = new HttpClient(handler))
        {
            var path = await AutomaticArtwork.Download(http, "https://upload.wikimedia.org/poster.webp", directory, default);
            check(path.EndsWith(".png") && ArtworkCache.IsUsable(path) && SkiaSharp.SKBitmap.Decode(path) is { Width: 2, Height: 2 }, "WebP artwork is decoded and cached as display-compatible PNG");
        }
        using (var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/first" ? Redirect("https://static.tvmaze.com/final") : new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) }))
        using (var http = new HttpClient(handler))
        {
            check((await MetadataHttp.GetImage(http, "https://static.tvmaze.com/first", default)).SequenceEqual(ArtworkFixture.Png) && handler.Requests.Count == 2,
                "permitted HTTPS artwork redirects are followed");
        }
        foreach (var location in new[] { "http://static.tvmaze.com/insecure", "https://untrusted.invalid/image", "https://user:secret@static.tvmaze.com/image" })
        {
            using var handler = new Handler(_ => Redirect(location)); using var http = new HttpClient(handler); var rejected = false;
            try { await MetadataHttp.GetImage(http, "https://static.tvmaze.com/start", default); } catch (IOException) { rejected = true; }
            check(rejected && handler.Requests.Count == 1, "unsafe artwork redirect is rejected before another request: " + new Uri(location).Host);
        }
        using (var handler = new Handler(_ => Redirect("/loop")))
        using (var http = new HttpClient(handler))
        {
            var stopped = false; try { await MetadataHttp.GetImage(http, "https://static.tvmaze.com/loop", default); } catch (IOException) { stopped = true; }
            check(stopped && handler.Requests.Count == 6, "artwork redirect loops stop after five hops");
        }
        var library = new LibraryStore(Path.Combine(directory, "images-db")); new MetadataOptions(Providers: ["tvmaze"]).Save(library);
        foreach (var number in new[] { 1, 2 }) library.Save(new("ep" + number, "ep" + number + ".mkv", "Example Show", "Show", Series: "Example Show", Season: 1, Episode: number,
            MetadataProvider: "tvmaze", MetadataId: 1, ProviderIds: new() { ["tvmaze"] = "1" }, Matched: true));
        using (var handler = new Handler(request => request.RequestUri!.Host == "static.tvmaze.com" ? request.RequestUri.AbsolutePath == "/bad.png" ? new(HttpStatusCode.OK) { Content = new StringContent("broken") } :
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) } : Json(request.RequestUri.AbsolutePath.EndsWith("/seasons") ?
                "[{\"number\":1,\"name\":\"Season 1\",\"image\":{\"medium\":\"https://static.tvmaze.com/medium.png\",\"original\":\"https://static.tvmaze.com/huge.jpg\"}}]" :
                "[{\"season\":1,\"number\":1,\"name\":\"First\",\"summary\":\"First plot\",\"image\":{\"medium\":\"https://static.tvmaze.com/bad.png\"}},{\"season\":1,\"number\":2,\"name\":\"Second\",\"summary\":\"Second plot\",\"image\":{\"medium\":\"https://static.tvmaze.com/good.png\"}}]")))
        using (var http = new HttpClient(handler))
        {
            var summary = await SeriesMetadata.FetchAsync(library, directory, null, null, default, http, force: true);
            var entries = library.All();
            check(entries.Single(x => x.Episode == 2) is { EpisodeTitle: "Second", EpisodeOverview: "Second plot" } && ArtworkCache.IsUsable(entries.Single(x => x.Episode == 2).EpisodePoster),
                "a failed first episode image does not block metadata or artwork for the next episode");
            check(summary.Contains("0 shows need matching") && summary.Contains("1 artwork failures") && !handler.Requests.Any(r => r.AbsolutePath == "/huge.jpg"),
                "matched shows remain matched and medium images avoid oversized originals");
            check(entries.Single(x => x.Episode == 1).MetadataFetchedAt == 0 && entries.Single(x => x.Episode == 1).EpisodeOverview == "First plot",
                "text is saved independently while failed artwork remains eligible for retry");
        }
        var firstEpisode = library.All().Single(x => x.Episode == 1);
        library.Save(firstEpisode with { Season = 1, Episode = 1, AbsoluteEpisode = 2, NumberingOrder = "absolute", EpisodeTitle = "User title", EpisodeOverview = "User plot", AirDate = "1990-01-01", RuntimeMinutes = 42 });
        using (var handler = new Handler(request => request.RequestUri!.Host == "static.tvmaze.com" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) } :
            Json(request.RequestUri.AbsolutePath.EndsWith("/seasons") ? "[{\"number\":1}]" : "[{\"season\":1,\"number\":1,\"name\":\"Replacement title\",\"summary\":\"Replacement plot\",\"airdate\":\"2020-01-01\",\"runtime\":10,\"image\":{\"medium\":\"https://static.tvmaze.com/recovered.png\"}},{\"season\":1,\"number\":2,\"name\":\"Second\"}]")))
        using (var http = new HttpClient(handler))
        {
            await SeriesMetadata.FetchAsync(library, directory, null, null, default, http, refresh: MetadataRefresh.RetryArtwork);
            var recovered = library.All().Single(x => x.Id == firstEpisode.Id);
            check(recovered is { Season: 1, Episode: 1, AbsoluteEpisode: 2, EpisodeTitle: "User title", EpisodeOverview: "User plot", AirDate: "1990-01-01", RuntimeMinutes: 42 } && ArtworkCache.IsUsable(recovered.EpisodePoster),
                "artwork-only retry recovers images without remapping numbering or replacing existing text");
            using var scope = MetadataIssues.Begin(library);
            check(scope.State.Snapshot().All(i => i.Code != "artwork-failure"), "successful image retry clears the persisted artwork failure");
        }
        var editStore = new LibraryStore(Path.Combine(directory, "image-edit-db")); new MetadataOptions(Providers: ["tvmaze"]).Save(editStore);
        editStore.Save(new("editing", "editing.mkv", "Example Show", "Show", Series: "Example Show", Season: 1, Episode: 1, MetadataProvider: "tvmaze", MetadataId: 1, Matched: true));
        using (var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "static.tvmaze.com")
            {
                var changed = editStore.All().Single();
                editStore.Save(changed with { Season = 2, Episode = 3, EpisodeTitle = "Manual title", LockedFields = ["Season", "Episode", "EpisodeTitle", "SeasonPoster"] });
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) };
            }
            return Json(request.RequestUri.AbsolutePath.EndsWith("/seasons") ? "[{\"number\":1,\"image\":{\"medium\":\"https://static.tvmaze.com/changed.png\"}}]" : "[{\"season\":1,\"number\":1,\"name\":\"Provider title\"}]");
        }))
        using (var http = new HttpClient(handler))
        {
            await SeriesMetadata.FetchAsync(editStore, directory, null, null, default, http, force: true);
            check(editStore.All().Single() is { Season: 2, Episode: 3, EpisodeTitle: "Manual title", SeasonPoster: null },
                "an image response cannot overwrite numbering and locks changed during its download");
        }
    }
    private static async Task ReportsAndPicker(string directory, Action<bool, string> check)
    {
        var store = new LibraryStore(Path.Combine(directory, "reports-db")); new MetadataOptions(Providers: ["tvmaze"]).Save(store);
        store.Save(new("unmatched", "missing.mkv", "Missing Show", "Show", Series: "Missing Show", Season: 1, Episode: 1));
        using var handler = new Handler(_ => Json("[]")); using var http = new HttpClient(handler);
        await AutomaticArtwork.FetchAsync(store, directory, null, null, default, http);
        var before = handler.Requests.Count;
        await AutomaticArtwork.FetchAsync(store, directory, null, null, default, http);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "metadata-fetch-report.json")));
        check(handler.Requests.Count == before && report.RootElement.GetProperty("issues").GetArrayLength() > 0 && report.RootElement.GetProperty("deferredTitles").GetInt32() == 1,
            "cooldown-only fetch retains outstanding issues and reports deferred titles without network calls");
        check(report.RootElement.GetProperty("issues")[0].GetProperty("retryAt").GetInt64() > Wire.Now, "outstanding report includes the next retry time");
        var onePiece = new MediaItem("anime", Path.Combine(directory, "Anime", "One Piece", "01.mkv"), "One Piece", "Anime", Series: "One Piece", Episode: 1);
        var candidates = new[] { new MetadataMatch(1505, "tv", "One Piece", "1999", null, "", "Anime", Animated: true), new MetadataMatch(73510, "tv", "The One Piece", null, null, "", "Anime", Animated: true),
            new MetadataMatch(46065, "tv", "One Piece", "2023", null, "", "Show", Animated: false) };
        check(MetadataMatching.Choose(candidates, onePiece)?.Id == 1505, "literal anime title outranks an article-stripped alias and excludes live action");
        check(MetadataMatching.Choose([candidates[0], candidates[1] with { Title = "One Piece" }], onePiece) is null, "same-name anime adaptations still require identification");
        using var pickerHandler = new Handler(_ => Json("[{\"show\":{\"id\":1719,\"name\":\"Mr. Bean\",\"premiered\":\"1990-01-01\",\"type\":\"Scripted\",\"url\":\"https://www.tvmaze.com/shows/1719\",\"summary\":\"Live plot\"}},{\"show\":{\"id\":5251,\"name\":\"Mr. Bean\",\"premiered\":\"2002-01-01\",\"type\":\"Animation\",\"url\":\"https://www.tvmaze.com/shows/5251\",\"summary\":\"Animated plot\"}}]"));
        using var pickerHttp = new HttpClient(pickerHandler);
        var bean = new MediaItem("bean", "bean.mkv", "Mr Bean", "Show", Series: "Mr Bean", Season: 1, Episode: 1);
        store.Save(bean with { Id = "identified-bean", Path = "other-bean.mkv", MetadataProvider = "tvmaze", MetadataId = 1719, ProviderIds = new() { ["tvmaze"] = "1719" }, Matched = true });
        var result = await MetadataIdentification.SearchAsync(store, bean, "Mr Bean", null, client: pickerHttp);
        check(result.Matches.Count == 2 && result.Matches.Any(x => x.Description.Contains("1990") && x.Description.Contains("Live action")) && result.Matches.Any(x => x.Description.Contains("2002") && x.Description.Contains("Animation")),
            "keyless identification lists live action and animated show options with years and provider");
        check(MetadataMatching.Choose(result.Matches, bean) is null, "Mr Bean ambiguity is not silently resolved by provider search ordering");
        check(result.Matches.Single(x => x.Id == 1719).Evidence is not null && result.Matches.Single(x => x.Id == 5251).Evidence is null,
            "the picker suggests an identified local release without merging folders or choosing it automatically");
        var locked = bean with { Year = 1989, Overview = "Local overview", Poster = "local.png", LockedFields = ["Year", "Overview", "Poster", "Title"] };
        var applied = MetadataIdentification.Apply(locked, result.Matches[0], "downloaded.png");
        check(applied is { Matched: true, MetadataProvider: "tvmaze", Year: 1989, Overview: "Local overview", Poster: "local.png", Title: "Mr Bean", Episode: 1 },
            "explicit keyless match stores provider identity while preserving local locks and numbering");
        var changedMatch = MetadataIdentification.Apply(bean with { MetadataProvider = "tvmaze", MetadataId = 5251, MetadataType = "tv", Poster = "old-download.png", SeriesPoster = "old-download.png", PosterSource = "https://www.tvmaze.com/shows/5251", EpisodeTitle = "Old episode", EpisodePoster = "old-episode.png" }, result.Matches.Single(x => x.Id == 1719), null);
        check(changedMatch is { Matched: true, MetadataId: 1719, Poster: null, SeriesPoster: null, EpisodeTitle: null, EpisodePoster: null, ArtworkFetchedAt: 0 },
            "manual identity correction clears unrelated downloaded art even if the new poster is unavailable");
        new MetadataOptions(Providers: ["wikipedia"]).Save(store);
        using var wikiHandler = new Handler(_ => Json(Pages(Page(42, "Glass Onion: A Knives Out Mystery", 2022))));
        using var wikiHttp = new HttpClient(wikiHandler);
        var wiki = await MetadataIdentification.SearchAsync(store, new("film", "film.mkv", "Glass Onion A Knives Out Mystery", "Movie"), "Glass Onion A Knives Out Mystery", null, client: wikiHttp);
        check(wiki.Matches is [ { Provider: "wikipedia", Id: 42 } ] && MetadataIdentification.Apply(new("film", "film.mkv", "Glass Onion", "Movie"), wiki.Matches[0], null).MetadataId == 42,
            "keyless Wikipedia identification persists the explicitly selected film page");
        var retryStore = new LibraryStore(Path.Combine(directory, "art-only-db")); new MetadataOptions(Providers: ["tvmaze"]).Save(retryStore);
        var missingPlot = bean with { MetadataProvider = "tvmaze", MetadataId = 1719, MetadataType = "tv", ProviderIds = new() { ["tvmaze"] = "1719" }, Matched = true };
        retryStore.Save(missingPlot);
        using (var scope = MetadataIssues.Begin(retryStore)) scope.State.Set("title", missingPlot, "provider-missing-fields", overviewMissing: true);
        using var retryHandler = new Handler(request => request.RequestUri!.Host == "static.tvmaze.com" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) } :
            Json(request.RequestUri.AbsolutePath == "/shows/1719" ? "{\"id\":1719,\"name\":\"Mr. Bean\",\"premiered\":\"1990-01-01\",\"type\":\"Scripted\",\"url\":\"https://www.tvmaze.com/shows/1719\",\"summary\":\"Remote plot\",\"image\":{\"medium\":\"https://static.tvmaze.com/bean.png\"}}" : "[]"));
        using var retryHttp = new HttpClient(retryHandler);
        var retryReport = await AutomaticArtwork.FetchAsync(retryStore, Path.Combine(directory, "art-only-report"), null, null, default, retryHttp, refresh: MetadataRefresh.RetryArtwork);
        using (var scope = MetadataIssues.Begin(retryStore))
            check(scope.State.Snapshot().Any(i => i.Stage == "title" && i.OverviewMissing) && retryStore.All().Single().Overview is null,
                "artwork-only retry preserves the outstanding missing-overview issue and existing text");
        check(retryReport.Contains("0 shows need matching") && retryReport.Contains("1 shows missing episode metadata"),
            "an identified show with no provider episode inventory is reported as missing metadata rather than unmatched");
    }
    private static object Page(int id, string title, int year) => new { pageid = id, title, extract = title + " is a " + year + " film.", categories = new[] { new { title = "Category:" + year + " films" } } };
    private static string Pages(params object[] pages) => JsonSerializer.Serialize(new { query = new { pages } });
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Limited() { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return response; }
    private static HttpResponseMessage Redirect(string target) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(target, UriKind.RelativeOrAbsolute); return response; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Requests.Add(request.RequestUri!); return Task.FromResult(handle(request)); }
    }
}
