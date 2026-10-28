using System.Net;
using System.Text;
using Watchroom.Core;

// Shows and films added before their files are here: placeholders, catalog badges, files joining them on a scan,
// uncertain files waiting for the user, and new seasons arriving from the provider.
static class WantedTitlesFixture
{
    static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");
    static MetadataSeason Season(int number, params int[] offsets) =>
        new(number, $"Season {number}", null, null, null, offsets.Select((offset, index) => new MetadataEpisode(index + 1, $"Episode {index + 1}", null, null, Day(offset), 24)).ToArray());
    static MetadataMatch Match(int id, string type, string title, int year, string kind, string? release = null) =>
        new(id, type, title, year.ToString(), null, "", kind, Provider: "tmdb", ReleaseDate: release);

    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var data = Path.Combine(directory, "data"); var shows = Path.Combine(directory, "Shows"); var movies = Path.Combine(directory, "Movies");
        async Task<string> FileAt(params string[] parts)
        {
            var path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [0]); return path;
        }
        Directory.CreateDirectory(shows); Directory.CreateDirectory(movies);
        var roots = new LibraryFolder[] { new(shows, "Anime"), new(movies, "Movie") };
        var store = new LibraryStore(data);
        // The library keeps its folders like the app does, so the app can open it as it stands.
        store.Setting("folders", Wire.Serialize(roots));
        bool Watched(string id) => store.Setting("watched:" + id) == "true";

        // A show with specials, an aired season and a season that hasn't started yet; the specials stay untracked.
        var frierenSeasons = new[] { Season(0, -400, -390), Season(1, -800, -793, -786, -779), Season(2, 20, 27) };
        var frieren = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(209867, "tv", "Frieren", 2023, "Anime"), "Anime", [1, 2], true, null), frierenSeasons, watchedThrough: 1);
        var placeholders = store.All().Where(x => LibraryIdentity.ShowKey(x) == frieren.Id).ToArray();
        check(placeholders.Length == 6 && placeholders.All(x => x.IsVirtual && !x.Available && x.Season is 1 or 2) && placeholders.All(x => x.Series == "Frieren" && x.Kind == "Anime"),
            "a wanted show gets a placeholder for every dated episode of its tracked seasons only");
        check(store.Wanted(frieren.Id)?.KnownSeasons?.Select(s => s.Number).SequenceEqual([0, 1, 2]) == true, "the untracked specials stay known so they can be tracked later");
        check(placeholders.Where(x => x.Season == 1).All(x => Watched(x.Id)) && placeholders.Where(x => x.Season == 2).All(x => !Watched(x.Id)), "seasons already watched start out watched");
        check(store.PruneAndLoad(roots).Count(x => LibraryIdentity.ShowKey(x) == frieren.Id) == 6, "a wanted show without files survives pruning");
        check(placeholders.All(x => x.TmdbFields is null || !x.TmdbFields.Keys.Any(key => key is "series" or "title" or "season" or "episode" or "kind")),
            "the retention limit never takes a placeholder's title, kind or episode numbers");
        var card = LibraryCatalog.Browse(store.All()).Single(x => LibraryIdentity.ShowKey(x.Media) == frieren.Id);
        check(card.Level == "series" && card.Media.IsVirtual && card.Status == "Missing" && !card.Caption.Contains("Unavailable"), "a wanted show's card is marked Missing, not Unavailable");
        var seasonCards = LibraryCatalog.Browse(store.All(), frieren.Id, "Anime");
        check(seasonCards.Single(x => x.Season == 1).Status == "Missing" && seasonCards.Single(x => x.Season == 2).Status == "Upcoming", "a season that hasn't aired is Upcoming");
        var progress = WantedTitles.Progress(frieren, store.All().Where(x => LibraryIdentity.ShowKey(x) == frieren.Id), Watched);
        check(progress is { OnDisk: 0, Missing: 4, Upcoming: 2, NextUpStatus: "Upcoming" } && progress.NextUp is { Season: 2, Episode: 1 }, "progress counts tracked episodes and next up skips watched seasons");

        // Films: one still to be released, one released.
        var dune = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(700, "movie", "Dune Part Three", 2026, "Movie", Day(50)), "Movie", [], false, null), []);
        var days = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(701, "movie", "Perfect Days", 2023, "Movie", "2023-12-21"), "Movie", [], false, null), [], seen: true);
        var films = LibraryCatalog.Browse(store.All()).Where(x => x.Level == "movie").ToDictionary(x => x.Media.Id);
        check(films[dune.Id].Status == "Upcoming" && films[dune.Id].Caption.Contains("Upcoming") && films[days.Id].Status == "Missing", "a wanted film is Upcoming before release and Missing after");
        var overview = LibraryHome.Overview(store.All());
        check(overview.Entries.Count == 0 && overview.Unseen.Length == 0 && SharedLibrary.Browse(store.All(), new()).Total == 0 && SharedLibrary.Browse(store.All(), new(Home: true)).Total == 0,
            "wanted titles stay out of Home, picks and the shared library");

        // Two episodes arrive in a folder that names the year: the folder joins the show for certain.
        var e1 = await FileAt(shows, "Frieren (2023)", "Season 1", "Frieren - S01E01.mkv"); await FileAt(shows, "Frieren (2023)", "Season 1", "Frieren - S01E02.mkv");
        await store.ScanAsync(roots);
        var members = store.All().Where(x => LibraryIdentity.ShowKey(x) == frieren.Id).ToArray();
        var arrived = members.Where(x => !x.IsVirtual).ToArray();
        check(arrived.Length == 2 && arrived.All(x => x.Kind == "Anime" && LibraryIdentity.ProviderId(x, "tmdb") == "209867"), "files that certainly belong to a wanted show join it with its identity");
        check(members.Count(x => x.IsVirtual) == 4 && !members.Any(x => x.IsVirtual && x.Season == 1 && x.Episode is 1 or 2), "the episodes that arrived lose their placeholders");
        check(arrived.All(x => Watched(x.Id)), "an episode marked watched before it arrived starts out watched");
        check(store.LastWantedArrivals.Any(x => x.StartsWith("Frieren · 2 episodes arrived")), "the scan reports what arrived");
        var showCard = LibraryCatalog.Browse(store.All()).Single(x => LibraryIdentity.ShowKey(x.Media) == frieren.Id);
        check(!showCard.Media.IsVirtual && showCard.Status == "2 missing", "a show with files names how many aired episodes are still missing");
        check(WantedTitles.Progress(frieren, members, Watched) is { OnDisk: 2, Missing: 2, Upcoming: 2 }, "progress follows the files that arrived");
        check(store.Get(arrived.Single(x => x.Path == e1).Id) is { Available: true }, "linked files stay playable");

        // Files with no year to confirm them wait for the user; Not this keeps them out for good.
        var severance = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(95396, "tv", "Severance", 2022, "Show"), "Show", [1], true, null), [Season(1, -1500, -1493)]);
        await FileAt(shows, "Severance", "Season 1", "Severance.S01E01.mkv");
        await store.ScanAsync(roots);
        var pending = store.Wanted(severance.Id)!;
        check(pending.Candidate is { Reason: "The files have no year to confirm it" } && pending.Candidate.Path.EndsWith("Severance") &&
            store.All().Count(x => LibraryIdentity.ShowKey(x) == severance.Id && !x.IsVirtual) == 0, "files that only look like a wanted show are offered, not linked");
        WantedTitles.Reject(store, pending);
        await store.ScanAsync(roots);
        check(store.Wanted(severance.Id) is { Candidate: null, Rejected.Length: 1 }, "files the user turned down are not offered again");
        var folder = WantedTitles.Folder(store.Wanted(severance.Id)!, store.All(), Path.Combine(shows, "Severance"));
        check(folder is { Likeness: WantedTitles.Likeness.Unconfirmed, Files.Length: 1 }, "a picked folder is checked against the title before linking");
        WantedTitles.Link(store, data, store.Wanted(severance.Id)!, folder!.Value.Files);
        check(store.All().Where(x => LibraryIdentity.ShowKey(x) == severance.Id).Count(x => !x.IsVirtual) == 1 && store.All().Count(x => LibraryIdentity.ShowKey(x) == severance.Id && x.IsVirtual) == 1,
            "linking a folder joins its files to the show");

        // Two folders of one name are never guessed between.
        var shogun = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(126308, "tv", "Shogun", 2024, "Show"), "Show", [1], true, null), [Season(1, -900)]);
        await FileAt(shows, "Shogun (2024)", "Season 1", "Shogun.S01E01.mkv"); await FileAt(directory, "Other", "Shogun (2024)", "Season 1", "Shogun.S01E01.1080p.mkv");
        await store.ScanAsync([.. roots, new(Path.Combine(directory, "Other"), "Show")]);
        check(store.Wanted(shogun.Id)?.Candidate?.Reason == "2 folders look like Shogun", "two candidate folders wait for the user to choose");

        // A released film arrives: its file takes the identity and the watched mark, and the title is done.
        var film = await FileAt(movies, "Perfect Days (2023)", "Perfect.Days.2023.1080p.mkv");
        await store.ScanAsync(roots);
        var days2 = store.All().Single(x => x.Path == film);
        check(store.Wanted(days.Id) is null && store.Get(days.Id) is null && LibraryIdentity.ProviderId(days2, "tmdb") == "701" && Watched(days2.Id),
            "a wanted film's file takes its identity and watched mark and the placeholder goes");

        // Stopping removes placeholders and keeps files.
        WantedTitles.Remove(store, data, store.Wanted(severance.Id)!);
        check(store.Wanted(severance.Id) is null && !store.All().Any(x => LibraryIdentity.ShowKey(x) == severance.Id && x.IsVirtual) &&
            store.All().Any(x => LibraryIdentity.ShowKey(x) == severance.Id && !x.IsVirtual), "stopping keeps the files and removes the placeholders");
        WantedTitles.Remove(store, data, store.Wanted(dune.Id)!);
        check(store.Get(dune.Id) is null && store.PruneAndLoad(roots).All(x => x.Id != dune.Id), "removing a wanted film with no file removes it entirely");

        // Untracking a season drops its placeholders without a network request.
        var fewer = store.Wanted(frieren.Id)! with { Seasons = [1] }; store.SaveWanted(fewer);
        WantedTitles.Reconcile(store, data, fewer);
        check(!store.All().Any(x => LibraryIdentity.ShowKey(x) == frieren.Id && x.Season == 2), "an untracked season's placeholders go");

        // A provider refresh adds a newly announced season when the title follows new seasons.
        using var handler = new Provider();
        using var tmdb = new MetadataClient(new string('a', 32), handler);
        using var http = new HttpClient(handler);
        var refreshing = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(500, "tv", "Refresh Show", 2020, "Show"), "Show", [1], true, null), [Season(1, -100)]);
        await WantedTitles.RefreshAsync(store, data, null, null, CancellationToken.None, force: true, metadata: tmdb, client: http);
        var refreshed = store.Wanted(refreshing.Id)!;
        check(refreshed.Seasons.SequenceEqual([1, 2]) && store.All().Count(x => LibraryIdentity.ShowKey(x) == refreshing.Id && x.Season == 2) == 1 && refreshed.Title == "Refresh Show",
            "a refresh tracks a newly announced season and adds its episodes");
        var catalog = store.All();
        var mixed = catalog.Where(x => LibraryIdentity.ShowKey(x) == frieren.Id).Append(catalog.First(x => LibraryIdentity.ShowKey(x) == frieren.Id && x.IsVirtual) with { Id = "s3", Season = 3, Episode = 1, AirDate = Day(-5) }).ToArray();
        check(LibraryCatalog.TitleStatus(mixed) == "3 missing" && LibraryCatalog.TitleStatus(mixed.Where(x => !x.IsVirtual || x.Season == 3).ToArray()) == "S3 missing",
            "a missing season of a show with files is named");
        var later = WantedTitles.Add(store, data, WantedTitles.FromMatch(Match(702, "movie", "Nightfall Express", 2027, "Movie", Day(45)), "Movie", [], false, null), []);
        check(LibraryCatalog.Browse(store.PruneAndLoad(roots)).Single(x => x.Media.Id == later.Id).Status == "Upcoming", "a film added after others stays listed as Upcoming");
    }

    private sealed class Provider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/3/tv/500" => """{"id":500,"name":"Refresh Show","first_air_date":"2020-01-01","overview":"Plot","genres":[],"seasons":[{"season_number":1},{"season_number":2}]}""",
                "/3/tv/500/season/1" => $$"""{"name":"Season 1","episodes":[{"episode_number":1,"name":"One","air_date":"{{Day(-100)}}"}]}""",
                "/3/tv/500/season/2" => $$"""{"name":"Season 2","episodes":[{"episode_number":1,"name":"New","air_date":"{{Day(10)}}"}]}""",
                _ => """{"results":[]}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
