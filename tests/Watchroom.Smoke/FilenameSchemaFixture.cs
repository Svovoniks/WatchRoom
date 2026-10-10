using Watchroom.Core;

static class FilenameSchemaFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        foreach (var marker in new[] { "S02E03", "s2e3", "S02-E03", "S02.E03", "S02_E03", "S02 E03", "S02xE03", "2x03", "Season 2 Episode 3", "Season_02_Episode_03", "Сезон 2 Серия 3", "2 сезон 3 серия", "С02Э03", "[S02E03]", "Saison 2 Épisode 3", "Temporada 2 Capítulo 3", "Staffel 2 Folge 3", "Stagione 2 Episodio 3" })
        {
            var item = FileNames.Parse(Path.Combine(directory, $"Example {marker} - Pilot [1080p x265].mkv"));
            check(item.Series == "Example" && item.Season == 2 && item.Episode == 3 && item.EpisodeTitle == "Pilot" && item.NumberingSource == "filename",
                "season schema extracts identity and subtitle: " + marker);
            check(MetadataTitles.FilenameAlias(item.Path) == "Example", "metadata alias agrees with season schema: " + marker);
        }
        foreach (var marker in new[] { "- 03 -", "– 03 –", "— 03 —", "EP03", "Episode 03", "Ep.03", "E03v2", "Episode #03", "#03", "[E03]", "[1x03]", "03 серия", "[03]", "(03)", "03" })
        {
            var path = Path.Combine(directory, "Anime", $"Example {marker} Pilot [KM-Dual-DVDRIP].mkv");
            var item = FileNames.Parse(path);
            check(item.Series == "Example" && item.Season == 1 && item.Episode == 3 && item.EpisodeTitle == "Pilot", "absolute schema extracts subtitle: " + marker);
            check(MetadataTitles.FilenameAlias(path) == "Example", "metadata alias agrees with absolute schema: " + marker);
        }
        var tight = FileNames.Parse(Path.Combine(directory, "Anime", "Example-03-Pilot.mkv"));
        check(tight.Series == "Example" && tight.Episode == 3 && tight.EpisodeTitle == "Pilot", "unspaced hyphens in anime episode names retain the subtitle");
        foreach (var episode in Enumerable.Range(1, 26))
        {
            var item = FileNames.Parse(Path.Combine(directory, $"Azumanga Daioh - {episode:00} - Episode subtitle [KM-Dual-DVDRIP].mkv"));
            check(item.Title == "Azumanga Daioh" && item.Series == "Azumanga Daioh" && item.Episode == episode && item.Kind == "Show" && item.EpisodeTitle == "Episode subtitle",
                "Azumanga filename groups and retains episode " + episode);
        }
        foreach (var (marker, first, last) in new[] { ("S01E02-E04", 2, 4), ("S01E02-S01E04", 2, 4), ("S01E02E03", 2, 3), ("S01E02E03E04", 2, 4), ("1x02-1x04", 2, 4), ("1x02x03", 2, 3), ("EP02-EP04", 2, 4), ("- 02-04", 2, 4) })
        {
            var item = FileNames.Parse(Path.Combine(directory, $"Example {marker} - Combined.mkv"));
            check(item.Series == "Example" && item.Episode == first && item.EpisodeEnd == last && item.EpisodeTitle == "Combined" && item.NumberingConflict is null,
                "multi-episode schema retains the full range: " + marker);
        }
        var bracketRange = FileNames.Parse(Path.Combine(directory, "Anime", "Example [02-04] - Combined.mkv"));
        check(bracketRange.Episode == 2 && bracketRange.EpisodeEnd == 4 && bracketRange.EpisodeTitle == "Combined", "numeric brackets preserve episode ranges");
        var relativeRange = FileNames.Parse(Path.Combine(directory, "Shows", "Example", "Season 02", "Example EP02-EP04.mkv"));
        check(relativeRange.Season == 2 && relativeRange.Episode == 2 && relativeRange.EpisodeEnd == 4 && relativeRange.AbsoluteEpisode is null && relativeRange.NumberingOrder == "aired",
            "season folders keep unlabelled ranges relative to their season");
        foreach (var marker in new[] { "S01E02E04", "S01E02-S02E03", "S01E04-E02", "S01E01-E200" })
        {
            var item = FileNames.Parse(Path.Combine(directory, $"Example {marker}.mkv"));
            check(item.EpisodeEnd is null && item.NumberingConflict is not null, "ambiguous range never invents included episodes: " + marker);
        }
        foreach (var name in new[] { "Example.2024.mkv", "Example - 2024.mkv", "Blade.Runner.2049.2017.2160p.mkv", "W.A.L.L.E.2008.mkv", "The.Man.from.U.N.C.L.E.2015.1080p.mkv", "2001 A Space Odyssey.mkv", "12 Monkeys (1995).mkv", "John Wick - 3 - Parabellum.mkv", "Movie.2024.DVDRip.AC3.2.0x3.mkv", "Movie 2024-03-05.mkv" })
        {
            var item = FileNames.Parse(Path.Combine(directory, "Movies", name));
            check(item.Series is null && item.Episode is null && item.AbsoluteEpisode is null, "movie title, date or release number stays a movie: " + name);
            check(MetadataTitles.FilenameAlias(item.Path) is null, "movie name does not leak a show alias: " + name);
        }
        foreach (var name in new[] { "03 - Pilot.mkv", "003 Pilot.mkv", "03.mkv", "E03.mkv", "S02E03.mkv" })
        {
            var item = FileNames.Parse(Path.Combine(directory, "Shows", "Example", "Season 02", name));
            check(item.Series == "Example" && item.Season == 2 && item.Episode == 3, "episode-only schema borrows the show folder: " + name);
            check(MetadataTitles.FilenameAlias(item.Path) is null, "episode-only name is not a show search alias: " + name);
        }
        foreach (var number in new[] { "203", "0203" })
        {
            var compact = FileNames.Parse(Path.Combine(directory, "Shows", "Example", "Season 02", $"Example {number}.mkv"));
            check(compact.Season == 2 && compact.Episode == 3, "season folder disambiguates compact numbering: " + number);
        }
        var absolute = FileNames.Parse(Path.Combine(directory, "Anime", "Example", "Example 203.mkv"));
        check(absolute.Episode == 203, "compact-looking anime numbering stays absolute without a season folder");
        foreach (var marker in new[] { "S00E01", "S00-E01", "S00.E01", "0x01", "Season 0 Episode 1" })
            check(!FileNames.IsExtra(Path.Combine(directory, "extras", $"Example {marker}.mkv")), "explicit specials survive extras filtering: " + marker);
        foreach (var date in new[] { "2021.01.01", "2021-01-01", "01.01.2021" })
        {
            var dated = FileNames.Parse(Path.Combine(directory, "Shows", "Example Show", $"Example Show {date} - Daily episode.mkv"));
            check(dated.AirDate == "2021-01-01" && dated.Episode is null && dated.Series == "Example Show" && dated.Year is null,
                "daily-show schema records its air date without inventing numbering: " + date);
        }
        foreach (var marker in new[] { "2021-01-01", "- 3 - Daily episode" })
        {
            var collection = Path.Combine(directory, "Collection");
            var localized = FileNames.Parse(Path.Combine(collection, "Localized Folder", $"Original Show {marker}.mkv"), "Show", collection);
            check(localized.Series == "Localized Folder" && MetadataTitles.Queries(localized).Contains("Original Show"),
                "configured show roots retain filename aliases without requiring a category folder name: " + marker);
        }
        var root = Path.Combine(directory, "media"); Directory.CreateDirectory(root);
        var video = Path.Combine(root, "Azumanga Daioh - 01 - Miss Yukari [KM-Dual-DVDRIP].mkv"); await File.WriteAllBytesAsync(video, [0]);
        var store = new LibraryStore(Path.Combine(directory, "scan-db"));
        var parsed = FileNames.Parse(video); store.Save(parsed with { Title = "Azumanga Daioh - 01 - Miss Yukari", Series = null, Kind = "Movie", Season = null, Episode = null, EpisodeTitle = null });
        var queue = Wire.Serialize(new SavedQueue[] { new("fixture", "Preserve queue", [parsed.Id]) }); store.Setting("queues", queue);
        await store.ScanAsync([new(root)]); var repaired = store.All().Single();
        check(repaired.Title == "Azumanga Daioh" && repaired.Episode == 1 && repaired.EpisodeTitle == "Miss Yukari" && repaired.Id == parsed.Id && store.Setting("queues") == queue,
            "rescan repairs old unrecognized filenames without changing file IDs or saved queues");
        store.Save(repaired with { Series = "Manual title", Title = "Manual title", Season = 4, Episode = 8, EpisodeTitle = "Manual subtitle", LockedFields = ["Title", "Season", "Episode", "EpisodeTitle"] });
        await store.ScanAsync([new(root)]); var locked = store.All().Single();
        check(locked.Title == "Manual title" && locked.Season == 4 && locked.Episode == 8 && locked.EpisodeTitle == "Manual subtitle", "broader filename parsing preserves manually locked identities");
        var dates = new LibraryStore(Path.Combine(directory, "date-db"));
        var dateItem = FileNames.Parse(Path.Combine(directory, "Shows", "Example Show", "Example Show 2021-01-01.mkv")) with { Matched = true, MetadataProvider = "tvmaze", MetadataId = 1, ProviderIds = new() { ["tvmaze"] = "1" } };
        dates.Save(dateItem);
        using var http = new HttpClient(new ArtworkFixture());
        await SeriesMetadata.FetchAsync(dates, directory, null, null, CancellationToken.None, http, force: true);
        var mapped = dates.All().Single(x => x.Id == dateItem.Id);
        check(mapped.Season == 2 && mapped.Episode == 1 && mapped.EpisodeTitle == "Return", "daily-show air date resolves to the unique provider season and episode");
        var ranges = new LibraryStore(Path.Combine(directory, "invalid-range-db"));
        var invalidRange = FileNames.Parse(Path.Combine(directory, "Anime", "Example Show", "Example Show - 01-200.mkv")) with
            { Matched = true, MetadataProvider = "tvmaze", MetadataId = 1, ProviderIds = new() { ["tvmaze"] = "1" } };
        ranges.Save(invalidRange);
        await SeriesMetadata.FetchAsync(ranges, directory, null, null, CancellationToken.None, http, force: true);
        var unchangedRange = ranges.All().Single(x => x.Id == invalidRange.Id);
        check(unchangedRange.NumberingConflict == invalidRange.NumberingConflict && unchangedRange.EpisodeSource is null,
            "provider mapping preserves ambiguous range warnings without assigning first-episode details");
        var stale = dateItem with { Id = "stale-date", Path = "stale-date.mkv", Season = 1, Episode = 1, EpisodeEnd = 2,
            AbsoluteEpisode = 1, AbsoluteEpisodeEnd = 2, NumberingOrder = "absolute", SourcePart = 2, SourcePartEpisode = 1 };
        dates.Save(stale);
        await SeriesMetadata.FetchAsync(dates, directory, null, null, CancellationToken.None, http, force: true);
        var remapped = dates.All().Single(x => x.Id == stale.Id);
        check(remapped.Season == 2 && remapped.Episode == 1 && remapped.EpisodeEnd is null && remapped.AbsoluteEpisode is null && remapped.SourcePart is null && remapped.NumberingOrder == "aired",
            "date identity replaces stale absolute and part numbering without inventing a range");
        var absent = dateItem with { Id = "unmapped-date", Path = "unmapped-date.mkv", AirDate = "2021-06-01" }; dates.Save(absent);
        await SeriesMetadata.FetchAsync(dates, directory, null, null, CancellationToken.None, http, force: true);
        var unresolved = dates.All().Single(x => x.Id == absent.Id);
        check(unresolved.Episode is null && unresolved.NumberingConflict is not null, "unmatched air dates remain unresolved with a correction hint");
        var collisions = new LibraryStore(Path.Combine(directory, "same-day-db")); collisions.Save(dateItem);
        using var collisionHttp = new HttpClient(new SameDayEpisodes());
        await SeriesMetadata.FetchAsync(collisions, directory, null, null, CancellationToken.None, collisionHttp, force: true);
        var collision = collisions.All().Single(x => x.Id == dateItem.Id);
        check(collision.Episode is null && collision.NumberingConflict is not null, "two episodes airing on the same date do not produce an arbitrary mapping");
    }

    private sealed class SameDayEpisodes : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(new ArtworkFixture());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await inner.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath.EndsWith("/episodes"))
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Content.Dispose();
                response.Content = new StringContent(body.Replace("2020-01-01", "2021-01-01"));
            }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
