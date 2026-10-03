using Watchroom.Core;
using System.Net;
using System.Text;

static class GroupingAuditFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var media = Path.Combine(directory, "media");
        var anime = Path.Combine(media, "Anime"); var movies = Path.Combine(media, "Movies");
        var dbDirectory = Path.Combine(directory, "db");
        var store = new LibraryStore(dbDirectory);
        var roots = new LibraryFolder[] { new(anime), new(movies) };
        store.Setting("folders", Wire.Serialize(roots));
        async Task<string> FileAt(params string[] parts)
        {
            var path = Path.Combine([media, .. parts]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [0]); return path;
        }
        var komi = new List<MediaItem>();
        for (var season = 1; season <= 2; season++)
            for (var episode = 1; episode <= (season == 1 ? 12 : 11); episode++)
            {
                var path = await FileAt("Anime", "Komi Can't Communicate", $"Komi Can't Communicate S{season:00}",
                    $"[Group] Komi Can't Communicate{(season == 2 ? " Season_2" : "")}_-_{episode:00}{(season == 2 && episode == 1 ? "_v2" : "")}.mkv");
                var parsed = FileNames.Parse(path, "Anime", anime);
                check(parsed.Season == season && parsed.Episode == episode && parsed.AbsoluteEpisode is null && parsed.NumberingSource == "season-folder",
                    $"explicit folder numbering parsed for S{season:00}E{episode:00}");
                var legacy = parsed with { Season = season == 2 && episode == 1 ? 2 : 1,
                    AbsoluteEpisode = episode, NumberingSource = null,
                    ShowId = season == 2 && episode == 1 ? "historical-second-id" : "historical-first-id" };
                store.Save(legacy); komi.Add(legacy);
            }
        var filmPaths = new[] {
            await FileAt("Anime", "Suzume No Tojimari", "[Group] Suzume No Tojimari [1080p].mkv"),
            await FileAt("Anime", "Spy x Family", "Spy x Family Code White", "Spy.X.Family.Code.White.2024.1080p.mp4"),
            await FileAt("Anime", "Chainsaw Man", "Reze", "[Group] Chainsaw Man - Reze-hen (Chainsaw Man The Movie Reze Arc) [1080p].mkv") };
        foreach (var path in filmPaths)
        {
            var parsed = FileNames.Parse(path, "Anime", anime);
            check(parsed.Series is null && parsed.ShowId is null, "unnumbered anime film does not inherit TV structure");
            store.Save(parsed with { Kind = "Movie", Title = "Old folder alias", Series = "Old folder alias", ShowId = "legacy-" + parsed.Id });
        }
        var unclePath = await FileAt("Movies", "The Man From U.N.C.L.E", "The.Man.from.U.N.C.L.E.2015.1080p.BluRay.mp4");
        var uncle = FileNames.Parse(unclePath);
        check(uncle.Series is null && uncle.Episode is null && uncle.Year == 2015 && uncle.Title.EndsWith("E"), "acronym before film year is not an episode");
        store.Save(uncle with { Kind = "Show", Series = "The Man from U N C L", Season = 1, Episode = 2015 });
        check(FileNames.Parse(Path.Combine(anime, "Example - EP 2015.mkv"), "Anime").Episode == 2015 &&
            FileNames.Parse(Path.Combine(anime, "Example - 1000.mkv"), "Anime").AbsoluteEpisode == 1000,
            "explicit large episode numbers remain supported");
        var mibPath = await FileAt("Movies", "MIB", "Men.in.Black.1997.REMASTERED.1080p.BluRay.mp4");
        var mib = FileNames.Parse(mibPath);
        store.Save(mib with { Kind = "Show", Series = "Men in Black: The Series", MetadataType = "tv", MetadataKind = "Show",
            MetadataProvider = "tvmaze", MetadataId = 9806, ProviderIds = new() { ["tvmaze"] = "9806" }, PosterSource = "https://www.tvmaze.com/shows/9806" });
        var extraPath = await FileAt("Anime", "Utopia", "Featurettes", "Deleted Scenes.mkv");
        var extra = FileNames.Parse(extraPath);
        store.Save(extra with { IsExtra = false, Available = false });
        var specialPath = await FileAt("Anime", "Special Show", "Featurettes", "Special Show S00E01.mkv");
        check(!FileNames.IsExtra(specialPath), "explicit S00 specials survive extras policy");
        var lockedPath = await FileAt("Anime", "Locked Show", "Season 2", "Locked Show - 01.mkv");
        var locked = FileNames.Parse(lockedPath, "Anime", anime) with { Season = 4, Episode = 99, AbsoluteEpisode = null, LockedFields = ["Season", "Episode"], ShowId = "user-identity" };
        store.Save(locked);
        var manualPath = await FileAt("Movies", "Manual", "Manual.2020.mp4");
        var manual = FileNames.Parse(manualPath) with { Series = "Chosen TV", MetadataType = "tv", Matched = true, ShowId = "manual-id" };
        store.Save(manual);
        var queue = Wire.Serialize(new SavedQueue[] { new("queue", "Keep IDs", [komi[0].Id, extra.Id, mib.Id]) });
        store.Setting("queues", queue); store.Setting("grouping-audit-repair", "0");
        store = new LibraryStore(dbDirectory);
        var repaired = store.All(); var repairedKomi = repaired.Where(x => komi.Any(k => k.Id == x.Id)).ToArray();
        check(repairedKomi.Select(x => x.ShowId).Distinct().Count() == 1 && repairedKomi.Count(x => x.Season == 1) == 12 && repairedKomi.Count(x => x.Season == 2) == 11,
            "existing historical Komi identities and overlapping season numbers repair together");
        var card = LibraryCatalog.Browse(repairedKomi).Single();
        check(card.Caption.Contains("2 seasons") && card.Caption.Contains("23 episodes") && LibraryCatalog.CardItems(repairedKomi, card).Count() == 23,
            "repaired show browse and queue include all 23 logical episodes");
        check(repaired.Single(x => x.Id == uncle.Id) is { Series: null, Episode: null, MetadataType: null } &&
            repaired.Single(x => x.Id == mib.Id) is { Series: null, MetadataProvider: null, MetadataId: null }, "historical film records lose erroneous automatic TV identities");
        check(repaired.Where(x => filmPaths.Contains(x.Path)).All(x => x.Series is null && x.ShowId is null && x.Title != "Old folder alias"),
            "legacy nested films regain file titles without dummy seasons");
        check(repaired.Single(x => x.Id == extra.Id) is { IsExtra: true, Available: true } && !LibraryCatalog.Browse(repaired).Any(x => x.Media.Id == extra.Id),
            "old excluded extra is available on disk and hidden from normal catalog");
        check(store.Setting("queues") == queue && File.Exists(extraPath) && File.Exists(Path.Combine(dbDirectory, "library-before-grouping-repair-v1.db")) &&
            File.ReadAllText(Path.Combine(dbDirectory, "grouping-repair-v1.json")).Contains(mib.Id), "migration backs up and logs repairs without losing queue IDs or files");
        check(repaired.Single(x => x.Id == locked.Id) is { Season: 4, Episode: 99, ShowId: "user-identity" } &&
            repaired.Single(x => x.Id == manual.Id) is { Series: "Chosen TV", ShowId: "manual-id" }, "migration preserves field locks and manual TV identity");
        await store.ScanAsync(roots); await store.ScanAsync(roots);
        repaired = new LibraryStore(dbDirectory).All();
        check(LibraryCatalog.CardItems(repaired, LibraryCatalog.Browse(repaired).Single(x => x.Media.Id == komi[0].Id || x.DisplayTitle == "Komi Can't Communicate")).Count() == 23,
            "two rescans and reopening do not reintroduce season collisions");
        check(repaired.Single(x => x.Id == extra.Id) is { IsExtra: true, Available: true } && repaired.Any(x => x.Path == specialPath && !x.IsExtra),
            "rescan distinguishes excluded extras from numbered specials");
        check(repaired.Single(x => x.Id == locked.Id) is { Season: 4, Episode: 99, ShowId: "user-identity" },
            "locked season-relative numbers survive rescans without an absolute-number marker");
        var uncertain = komi[0] with { Id = "uncertain", Path = "uncertain.mkv", Season = null, NumberingConflict = "Unresolved" };
        check(LibraryCatalog.PreferredFiles([uncertain, uncertain with { Id = "uncertain-two" }]).Count() == 2,
            "unresolved episode identity never collapses distinct files as versions");
        var candidates = new MetadataMatch[] { new(607, "movie", "Men in Black", "1997", null, ""), new(9806, "tv", "Men in Black", "1997", null, "") };
        check(MetadataMatching.Choose(candidates, mib)?.Type == "movie" && MetadataMatching.Choose(candidates, mib with { Path = "unknown.mkv" }) is null,
            "movie path disambiguates same-title same-year film and TV while uncertain ties stay unresolved");
        check(MetadataMatching.Choose(candidates, mib with { Series = "Men in Black", Season = 1, Episode = 1 })?.Type == "tv",
            "explicit episodes take precedence over movie-folder hints");
        var providerStore = new LibraryStore(Path.Combine(directory, "provider-db")); providerStore.Save(mib);
        using var handler = new FilmProvider(); using var http = new HttpClient(handler);
        await AutomaticArtwork.FetchAsync(providerStore, directory, null, null, default, http);
        check(handler.TvRequests == 0 && providerStore.All().Single().MetadataType == "movie", "TV-only fallback cannot claim a strongly identified movie and film fallback still works");
    }
    private sealed class FilmProvider : HttpMessageHandler
    {
        public int TvRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.tvmaze.com") TvRequests++;
            var body = """{"query":{"pages":[{"pageid":607,"title":"Men in Black (1997 film)","extract":"Film plot","categories":[{"title":"Category:1997 films"}]}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
