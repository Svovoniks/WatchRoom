using System.Net;
using System.Text;
using System.Text.Json;
using BlackPearl.Core;

// Files that name their own episodes choose the provider's numbering order, and contradict a match that can't be their show.
static class EpisodeOrderFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        check(EpisodeOrder.Same("Jupiter Jazz, Part I", "Jupiter Jazz (1)") && EpisodeOrder.Same("Manager And Salesman", "The Manager and the Salesman") &&
              EpisodeOrder.Same("Our Bodies,Our Elves", "Our Bodies, Our Elves") && EpisodeOrder.Same("Pda", "PDA"),
            "episode titles match across punctuation, articles and part numerals");
        check(!EpisodeOrder.Same("Pink Pajamas", "Pinky in Paradise") && !EpisodeOrder.Same("Pink Punch", "Pinkus Pantherus") && !EpisodeOrder.Same("Asteroid Blues", "Stray Dog Strut"),
            "a shared or similar word doesn't make two titles the same");

        // Netflix-style parts: the files' S02 is the provider's second half of season 1, which a "Parts" episode group lists as season 2.
        string[] aired1 = ["Pilot", "Second", "Third", "Fourth", "Fifth", "Sixth"], aired2 = ["Seventh", "Eighth", "Ninth"];
        var handler = new OrderHandler();
        handler.Add("/3/tv/10/season/1", Season(1, aired1));
        handler.Add("/3/tv/10/season/2", Season(2, aired2));
        handler.Add("/3/tv/10", """{"id":10,"name":"Parts Show","seasons":[{"season_number":1},{"season_number":2}]}""");
        handler.Add("/3/tv/10/episode_groups", """{"results":[{"id":"prod","type":6,"name":"Production"},{"id":"parts","type":1,"name":"Parts"}]}""");
        var airedAt = aired1.Select((t, i) => (t, (1, i + 1))).Concat(aired2.Select((t, i) => (t, (2, i + 1)))).ToDictionary(x => x.t, x => x.Item2);
        handler.Add("/3/tv/episode_group/prod", Group(airedAt, (1, ["Pilot", "Third", "Second", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth"])));
        handler.Add("/3/tv/episode_group/parts", Group(airedAt, (1, ["Pilot", "Second", "Third"]), (2, ["Fourth", "Fifth", "Sixth"]), (3, ["Seventh", "Eighth", "Ninth"])));
        var parts = Store(Path.Combine(directory, "parts"));
        var files = new[] { (1, 1, "Pilot"), (1, 2, "Second"), (1, 3, "Third"), (2, 1, "Fourth"), (2, 2, "Fifth"), (2, 3, "Sixth") };
        foreach (var (s, e, title) in files) parts.Save(Episode(directory, "parts", "Parts Show", 10, s, e, title));
        await Fetch(parts, directory, handler);
        var placed = parts.All().Where(x => !x.IsVirtual).OrderBy(x => x.Season).ThenBy(x => x.Episode).ToArray();
        check(placed.All(x => x.NumberingOrder == "group:parts"), $"titles choose the episode group the files are numbered by (got {string.Join(",", placed.Select(x => x.NumberingOrder).Distinct())})");
        check(placed.Single(x => x.Season == 2 && x.Episode == 1).EpisodeOverview == "Plot of Fourth" && placed.Single(x => x.Season == 2 && x.Episode == 1).EpisodeTitle == "Fourth",
            "a file gets the plot of the episode its title names, not of the aired episode at its number");
        check(placed.Single(x => x.Season == 1 && x.Episode == 2).EpisodeOverview == "Plot of Second", "files the group numbers as aired keep their episodes");
        check(parts.Shows().Single().NumberingOrder == "group:parts", "the show records the chosen order");
        check(placed.Single(x => x.Season == 2 && x.Episode == 1).EpisodeSource == "https://www.themoviedb.org/tv/10/season/1/episode/4",
            "a reordered episode links to its place in the provider's aired numbering, which later still lookups read");

        // A library fetched before titles could choose orders is checked once, and the moved files lose the text their old numbers gave them.
        var upgraded = Store(Path.Combine(directory, "upgraded"));
        foreach (var (s, e, title) in files) upgraded.Save(Episode(directory, "upgraded", "Parts Show", 10, s, e, title) with { MetadataFetchedAt = Wire.Now, EpisodeOverview = "Old plot", EpisodeSource = "https://www.themoviedb.org/tv/10/season/" + s + "/episode/" + e });
        // An untitled file whose title the aired list gave: the old order's S02E03 is "Ninth", the group's is "Sixth".
        var untitled = upgraded.All().Single(x => x.Season == 2 && x.Episode == 3);
        upgraded.Save(TmdbRetention.Record(untitled with { EpisodeTitle = null }, untitled with { EpisodeTitle = "Ninth", EpisodeOverview = "Plot of Ninth" }, "tmdb", "episodeTitle", "episodeOverview"));
        await Fetch(upgraded, directory, handler, force: false);
        var retitled = upgraded.All().Single(x => !x.IsVirtual && x.Season == 2 && x.Episode == 3);
        check(retitled is { EpisodeTitle: "Sixth", EpisodeOverview: "Plot of Sixth" }, $"a moved file whose title the provider gave takes its new episode's title with its plot (got {retitled.EpisodeTitle}, {retitled.EpisodeOverview})");
        var moved = upgraded.All().Single(x => !x.IsVirtual && x.Season == 2 && x.Episode == 1);
        check(moved.NumberingOrder == "group:parts" && moved.EpisodeOverview == "Plot of Fourth" && moved.EpisodeTitle == "Fourth",
            $"an existing library's shows are checked once after the update and moved files get their own episode's text (got {moved.NumberingOrder}, {moved.EpisodeOverview})");
        check(upgraded.Setting("episode-order-checked") == "1", "the one-time order check is recorded");

        // The chosen order stays through a rescan and is read straight from the group next time.
        handler.Requests.Clear();
        await Fetch(parts, directory, handler);
        check(handler.Requests.Contains("/3/tv/episode_group/parts") && !handler.Requests.Contains("/3/tv/10/episode_groups"),
            "a show in a chosen order reads its group without weighing orders again");
        var partsRoot = Path.Combine(directory, "parts-media");
        var rescanned = Path.Combine(partsRoot, "Parts Show", "Season 2", "Parts Show - S02E01 - Fourth.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(rescanned)!); await File.WriteAllBytesAsync(rescanned, [0]);
        var rescanStore = Store(Path.Combine(directory, "parts-rescan"));
        await rescanStore.ScanAsync([new(partsRoot, "Show")]);
        var scannedItem = rescanStore.All().Single();
        rescanStore.Save(scannedItem with { MetadataProvider = "tmdb", MetadataId = 10, ProviderIds = new() { ["tmdb"] = "10" }, NumberingOrder = "group:parts" });
        await rescanStore.ScanAsync([new(partsRoot, "Show")]);
        check(rescanStore.All().Single().NumberingOrder == "group:parts", "a rescan keeps an order chosen from the provider's groups");

        // Files that agree with the aired list never cost a group lookup.
        var aired = Store(Path.Combine(directory, "aired"));
        foreach (var (s, e, title) in new[] { (1, 1, "Pilot"), (1, 2, "Second"), (1, 3, "Third"), (1, 4, "Fourth") }) aired.Save(Episode(directory, "aired", "Parts Show", 10, s, e, title));
        handler.Requests.Clear();
        await Fetch(aired, directory, handler);
        check(aired.All().All(x => x.NumberingOrder == "aired") && !handler.Requests.Any(x => x.Contains("episode_group")), "files that match the aired list keep it without asking for other orders");

        // An order someone picked is theirs, whatever the titles say.
        var locked = Store(Path.Combine(directory, "locked"));
        foreach (var (s, e, title) in files) locked.Save(Episode(directory, "locked", "Parts Show", 10, s, e, title) with { LockedFields = ["NumberingOrder"] });
        await Fetch(locked, directory, handler);
        check(locked.All().All(x => x.NumberingOrder == "aired"), "a numbering order someone chose isn't replaced");

        // Too few titled files can't outvote the aired list.
        var few = Store(Path.Combine(directory, "few"));
        few.Save(Episode(directory, "few", "Parts Show", 10, 2, 1, "Fourth")); few.Save(Episode(directory, "few", "Parts Show", 10, 2, 2, "Fifth"));
        await Fetch(few, directory, handler);
        check(few.All().All(x => x.NumberingOrder == "aired"), "two titled files don't change a show's order");

        // A wrong match: 10 classic shorts numbered past the matched show's 6 episodes, their titles nowhere in it.
        var wrong = new OrderHandler();
        wrong.Add("/3/tv/20/season/1", Season(1, ["Down on the Antfarm", "Pinky in Paradise", "Pinkenstein", "Cleopanthra", "Pinkus Pantherus", "Big Top Pinky"]));
        wrong.Add("/3/tv/20", """{"id":20,"name":"The Pink Panther","seasons":[{"season_number":1}]}""");
        wrong.Add("/3/tv/20/episode_groups", """{"results":[]}""");
        string[] shorts = ["Pink Pajamas", "We Give Pink Stamps", "Dial P for Pink", "Sink Pink", "Pickled Pink", "Pinkfinger", "Shocking Pink", "Pink Ice", "The Pink Tail Fly", "Pink Panzer"];
        var contradicted = Store(Path.Combine(directory, "wrong"));
        for (var i = 0; i < shorts.Length; i++) contradicted.Save(Episode(directory, "wrong", "The Pink Panther", 20, 1, i + 1, shorts[i]) with { Overview = "1993 series plot", Poster = Path.Combine(directory, "1993.jpg") });
        await Fetch(contradicted, directory, wrong);
        var dropped = contradicted.All().Where(x => !x.IsVirtual).ToArray();
        check(dropped.All(x => x.MetadataId is null && x.MetadataProvider is null && x.Overview is null && x.EpisodeOverview is null && (x.ProviderIds?.Count ?? 0) == 0),
            "a match the files' titles and numbers both contradict is dropped");
        check(dropped.OrderBy(x => x.Episode).Select(x => x.EpisodeTitle).SequenceEqual(shorts), "the files keep their own episode titles to judge the next match");
        check(contradicted.Setting("metadata-rejected:" + LibraryIdentity.ShowKey(dropped[0])) is { } rejectedJson && rejectedJson.Contains("tmdb:tv:20"),
            "the dropped match is remembered for the show");
        check(contradicted.Setting("metadata-outstanding-v2")?.Contains("episode-titles-contradict-match") == true, "the dropped match is reported as an issue");

        // A show whose tvshow.nfo names it keeps that match: the next read of the NFO would only bring it back.
        var sidecar = Store(Path.Combine(directory, "wrong-nfo"));
        for (var i = 0; i < shorts.Length; i++) sidecar.Save(Episode(directory, "wrong-nfo", "The Pink Panther", 20, 1, i + 1, shorts[i]));
        await File.WriteAllTextAsync(Path.Combine(directory, "wrong-nfo-files", "tvshow.nfo"), "https://www.themoviedb.org/tv/20");
        await Fetch(sidecar, directory, wrong);
        check(sidecar.All().Where(x => !x.IsVirtual).All(x => x.MetadataId == 20), "a match a show's NFO file gives isn't dropped for its titles");

        // The next lookup leaves the dropped match out and takes the next candidate.
        var search = new OrderHandler();
        search.Add("/3/search/tv", """{"results":[{"id":20,"name":"The Pink Panther","first_air_date":"1993-09-11","overview":"1993"},{"id":21,"name":"The Pink Panther","first_air_date":"1964-12-18","overview":"Shorts"}]}""");
        search.Add("/3/tv/21", """{"id":21,"name":"The Pink Panther","first_air_date":"1964-12-18","overview":"Shorts","seasons":[]}""");
        search.Add("/3/tv/20", """{"id":20,"name":"The Pink Panther","first_air_date":"1993-09-11","overview":"1993","seasons":[]}""");
        using (var client = new MetadataClient("fixture", search))
        {
            var probe = new MediaItem("probe", Path.Combine(directory, "probe.mkv"), "The Pink Panther", "Show", Series: "The Pink Panther", Season: 1, Episode: 1);
            var without = await client.MatchAsync(probe, default, rejected: new HashSet<string> { MetadataRejections.Key("tmdb", "tv", 20) });
            check(without?.Id == 21, $"a rejected match is left out of the next lookup (got {without?.Id})");
        }

        // Titles alone don't drop a match: romanized titles against an English list, with numbers that fit.
        var romaji = new OrderHandler();
        romaji.Add("/3/tv/30/season/1", Season(1, ["The Beginning", "The Journey", "The Rival", "The Storm", "The Return", "The End"]));
        romaji.Add("/3/tv/30", """{"id":30,"name":"Anime","seasons":[{"season_number":1}]}""");
        romaji.Add("/3/tv/30/episode_groups", """{"results":[]}""");
        var kept = Store(Path.Combine(directory, "romaji"));
        string[] romanized = ["Hajimari", "Tabidachi", "Raibaru", "Arashi", "Kikan", "Owari"];
        for (var i = 0; i < romanized.Length; i++) kept.Save(Episode(directory, "romaji", "Anime", 30, 1, i + 1, romanized[i]));
        await Fetch(kept, directory, romaji);
        check(kept.All().All(x => x.MetadataId == 30 && x.EpisodeOverview is not null), "titles that differ while numbers fit don't drop a match");

        // Shows that share a name: the files' seasons and air dates pick the one they belong to, and nothing else does.
        var twins = new OrderHandler();
        twins.Add("/3/search/tv", """{"results":[{"id":40,"name":"Miracle Workers","first_air_date":"2006-03-01","overview":"Old"},{"id":41,"name":"Miracle Workers","first_air_date":"2019-02-12","overview":"New"}]}""");
        twins.Add("/3/tv/40", """{"id":40,"name":"Miracle Workers","first_air_date":"2006-03-01","last_air_date":"2006-04-01","in_production":false,"number_of_seasons":1,"overview":"Old","seasons":[]}""");
        twins.Add("/3/tv/41", """{"id":41,"name":"Miracle Workers","first_air_date":"2019-02-12","last_air_date":"2023-08-01","in_production":false,"number_of_seasons":4,"overview":"New","seasons":[]}""");
        twins.Add("/3/tv/40/alternative_titles", """{"results":[]}"""); twins.Add("/3/tv/41/alternative_titles", """{"results":[]}""");
        twins.Add("/3/search/movie", """{"results":[]}""");
        using (var client = new MetadataClient("fixture", twins))
        {
            MediaItem Twin(int season, int episode) => new($"twin-{season}-{episode}", Path.Combine(directory, $"Miracle.Workers.S{season:00}E{episode:00}.mkv"), "Miracle Workers", "Show",
                Series: "Miracle Workers", Season: season, Episode: episode, NumberingSource: "filename", ShowId: "show-twins");
            var second = await client.MatchAsync(Twin(2, 6), default, null, [Twin(1, 1), Twin(2, 6)]);
            check(second?.Id == 41, $"a second-season file picks the only same-named show with two seasons (got {second?.Id})");
            var first = await client.MatchAsync(Twin(1, 1), default, null, [Twin(1, 1)]);
            check(first is null, "a first-season file can't tell same-named shows apart, so it waits for a choice");
        }
        var daily = new OrderHandler();
        daily.Add("/3/search/tv", """{"results":[{"id":50,"name":"The Price Is Right","first_air_date":"1972-09-04","overview":"Daytime"},{"id":51,"name":"The Price Is Right","first_air_date":"1985-09-06","overview":"Night"}]}""");
        daily.Add("/3/tv/50", """{"id":50,"name":"The Price Is Right","first_air_date":"1972-09-04","last_air_date":"2026-10-30","in_production":true,"number_of_seasons":55,"overview":"Daytime","seasons":[]}""");
        daily.Add("/3/tv/51", """{"id":51,"name":"The Price Is Right","first_air_date":"1985-09-06","last_air_date":"1986-01-01","in_production":false,"number_of_seasons":1,"overview":"Night","seasons":[]}""");
        daily.Add("/3/tv/50/alternative_titles", """{"results":[]}"""); daily.Add("/3/tv/51/alternative_titles", """{"results":[]}"""); daily.Add("/3/search/movie", """{"results":[]}""");
        using (var client = new MetadataClient("fixture", daily))
        {
            var dated = new MediaItem("dated", Path.Combine(directory, "The.Price.Is.Right.2026.10.30.mkv"), "The Price Is Right", "Show", Series: "The Price Is Right",
                AirDate: "2026-10-30", NumberingSource: "date", ShowId: "show-daily");
            check(await client.MatchAsync(dated, default, null, [dated]) is null, "an air date alone doesn't pick between same-named shows, since the right one may not be among them");
        }
        check(MetadataTitles.SeasonlessTitle("Jaku Chara Tomozaki-kun 2nd Stage") == "Jaku Chara Tomozaki-kun" && MetadataTitles.SeasonlessTitle("Nevskij-3") == "Nevskij" &&
              MetadataTitles.SeasonlessTitle("Panty And Stocking TV 2") == "Panty And Stocking" && MetadataTitles.SeasonlessTitle("Yami Shibai 11") == "Yami Shibai" &&
              MetadataTitles.SeasonlessTitle("Mob Psycho 100") is null && MetadataTitles.SeasonlessTitle("Steins Gate") is null,
            "season markers come off a show's name for a last search, and three-digit names stay whole");
        var sequel = new MediaItem("sequel", Path.Combine(directory, "Chitose-kun wa Ramune Bin no Naka 2 - 01.mkv"), "Chitose-kun wa Ramune Bin no Naka 2", "Show",
            Series: "Chitose-kun wa Ramune Bin no Naka 2", Season: 1, Episode: 1);
        var queries = MetadataTitles.Queries(sequel);
        check(queries[0] == "Chitose-kun wa Ramune Bin no Naka 2" && queries.Contains("Chitose-kun wa Ramune Bin no Naka"), "the full name is searched before the name without its season number");

        // A folder holding the same episodes under two unrelated show names holds two shows; copies of one show under two spellings stay one.
        var folders = Path.Combine(directory, "folders");
        string Place(params string[] parts) { var path = Path.Combine(new[] { folders }.Concat(parts).ToArray()); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [0]); return path; }
        for (var e = 1; e <= 3; e++)
        {
            Place("Desktop", "hand_brake", "Season 1-handbraked", $"Villainess Doom - S01e{e:00}.mp4");
            Place("Desktop", "hand_brake", "Season 1-handbraked", $"[Group] Komi Communicate - S01e{e:00}.mp4");
            Place("Desktop", "hand_brake", "Season 2-handbraked", $"[Group] Komi Communicate - S02e{e:00}.mp4");
            Place("Anime", "Ushio to Tora (2015)", "Season 1", $"[VCB] Ushio to Tora [{e:00}].mkv");
            Place("Anime", "Ushio to Tora (2015)", "Season 1", $"[FLsnow][Ushitora][{e:00}].mkv");
            Place("Anime", "Example Show", "Season 1", $"Alias Title S01E{e:00}.mkv");
        }
        var komi = FileNames.Parse(Path.Combine(folders, "Desktop", "hand_brake", "Season 2-handbraked", "[Group] Komi Communicate - S02e01.mp4"), "Mixed", Path.Combine(folders, "Desktop"));
        var villainess = FileNames.Parse(Path.Combine(folders, "Desktop", "hand_brake", "Season 1-handbraked", "Villainess Doom - S01e01.mp4"), "Mixed", Path.Combine(folders, "Desktop"));
        check(komi.Series == "Komi Communicate" && villainess.Series == "Villainess Doom" && komi.ShowId != villainess.ShowId,
            $"files of unrelated shows in one folder keep their own shows (got {komi.Series}, {villainess.Series})");
        var versions = new[] { "[VCB] Ushio to Tora [01].mkv", "[FLsnow][Ushitora][01].mkv" }.Select(name => FileNames.Parse(Path.Combine(folders, "Anime", "Ushio to Tora (2015)", "Season 1", name), "Mixed", Path.Combine(folders, "Anime"))).ToArray();
        check(versions.Select(x => x.ShowId).Distinct().Count() == 1, "two spellings of one show in its folder stay one show");
        var alias = FileNames.Parse(Path.Combine(folders, "Anime", "Example Show", "Season 1", "Alias Title S01E01.mkv"), "Mixed", Path.Combine(folders, "Anime"));
        check(alias.Series == "Example Show", "a show folder still names files that call the show something else");

        // Another copy's match settles a tie between same-named shows that nothing else separates.
        var copies = new OrderHandler();
        copies.Add("/3/search/tv", """{"results":[{"id":60,"name":"Komi Communicate","first_air_date":"2021-10-06","overview":"Anime"},{"id":61,"name":"Komi Communicate","first_air_date":"2021-09-06","overview":"Drama"}]}""");
        copies.Add("/3/tv/60", """{"id":60,"name":"Komi Communicate","first_air_date":"2021-10-06","number_of_seasons":1,"overview":"Anime","seasons":[]}""");
        copies.Add("/3/tv/61", """{"id":61,"name":"Komi Communicate","first_air_date":"2021-09-06","number_of_seasons":1,"overview":"Drama","seasons":[]}""");
        copies.Add("/3/tv/60/alternative_titles", """{"results":[]}"""); copies.Add("/3/tv/61/alternative_titles", """{"results":[]}"""); copies.Add("/3/search/movie", """{"results":[]}""");
        var matchedCopy = new MediaItem("copy-a", Path.Combine(directory, "a", "Komi Communicate - S01E01.mkv"), "Komi Communicate", "Show", Series: "Komi Communicate", Season: 1, Episode: 1,
            ShowId: "show-copy-a", MetadataProvider: "tmdb", MetadataId: 60, MetadataType: "tv");
        var newCopy = new MediaItem("copy-b", Path.Combine(directory, "b", "Komi Communicate - S01E02.mkv"), "Komi Communicate", "Show", Series: "Komi Communicate", Season: 1, Episode: 2,
            ShowId: "show-copy-b", NumberingSource: "filename");
        var knownShows = new KnownShows([matchedCopy, newCopy]);
        check(knownShows.For(newCopy).SetEquals([MetadataRejections.Key("tmdb", "tv", 60)]) && knownShows.For(matchedCopy).Count == 0, "the library's matches for a show's name are known to its other copies");
        using (var client = new MetadataClient("fixture", copies))
        {
            check((await client.MatchAsync(newCopy, default, null, [newCopy], knownShows: knownShows.For(newCopy)))?.Id == 60, "a tie between same-named shows goes to the one another copy is matched to");
            check(await client.MatchAsync(newCopy, default, null, [newCopy]) is null, "without another copy, the tie still waits for a choice");
        }

        // "Extras-handbraked" is an extras folder, and beside several shows each clip stays with the show its own name gives.
        Place("Desktop", "hand_brake", "Extras-handbraked", "[Group] Komi Communicate - Nced.mp4");
        Place("Desktop", "hand_brake", "Extras-handbraked", "[Group] Komi Communicate - S01e03 Nced.mp4");
        var clip = FileNames.Parse(Path.Combine(folders, "Desktop", "hand_brake", "Extras-handbraked", "[Group] Komi Communicate - Nced.mp4"), "Mixed", Path.Combine(folders, "Desktop"));
        var tied = FileNames.Parse(Path.Combine(folders, "Desktop", "hand_brake", "Extras-handbraked", "[Group] Komi Communicate - S01e03 Nced.mp4"), "Mixed", Path.Combine(folders, "Desktop"));
        check(clip.IsBonus && clip.Series == "Komi Communicate" && tied.IsBonus && tied.Series == "Komi Communicate",
            $"clips in a suffixed extras folder are their own show's extras (got {clip.Kind}/{clip.Series}/{clip.IsBonus}, {tied.Series}/{tied.IsBonus})");
        var film = Place("Movies", "Extra-Terrestrial (1982)", "E.T. the Extra-Terrestrial (1982).mkv");
        check(FileNames.Parse(film, "Mixed", Path.Combine(folders, "Movies")) is { IsBonus: false, Series: null }, "a film folder that starts with Extra is still a film's folder");

        // A folder of shorts no provider lists as a show: each file is a film of its own, found by its title with its year or its length.
        string[] cartoons = ["The Pink Phink", "Pink Pajamas", "We Give Pink Stamps", "Dial P for Pink", "Sink Pink", "Pickled Pink"];
        var shortsApi = new OrderHandler();
        shortsApi.Add("/3/search/tv", """{"results":[]}"""); shortsApi.Add("/3/search/movie", """{"results":[]}""");
        for (var i = 0; i < cartoons.Length; i++)
        {
            // The search filtered by year returns only that year's film; an older one of the same name is ruled out by its length too.
            shortsApi.Search("movie", cartoons[i], $$"""{"results":[{"id":{{300 + i}},"title":"{{cartoons[i]}}","release_date":"1965-01-01","overview":"Short {{i}}","poster_path":"/short{{i}}.jpg"},{"id":{{400 + i}},"title":"{{cartoons[i]}}","release_date":"1929-01-01","overview":"Old film","poster_path":null}]}""");
            shortsApi.Add($"/3/movie/{300 + i}", $$"""{"id":{{300 + i}},"title":"{{cartoons[i]}}","release_date":"1965-01-01","runtime":7,"overview":"Short {{i}}","poster_path":"/short{{i}}.jpg"}""");
            shortsApi.Add($"/3/movie/{400 + i}", $$"""{"id":{{400 + i}},"title":"{{cartoons[i]}}","release_date":"1929-01-01","runtime":88,"overview":"Old film"}""");
        }
        MediaItem Cartoon(string set, int number, string name)
        {
            var folder = Path.Combine(directory, set, "Pink Shorts"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{number:000} {name}.mp4"); File.WriteAllBytes(path, [0]);
            return new MediaItem(LibraryIdentity.Hash(path), path, "Pink Shorts", "Show", Series: "Pink Shorts", Season: 1, Episode: number, EpisodeTitle: cartoons[number - 1],
                ShowId: "show-" + LibraryIdentity.Hash(set), FileRuntimeMinutes: 7);
        }
        var datedShorts = Store(Path.Combine(directory, "shorts"));
        for (var i = 0; i < cartoons.Length; i++) datedShorts.Save(Cartoon("shorts", i + 1, $"{cartoons[i]} (Jan 1, 1965)"));
        await Fetch(datedShorts, directory, shortsApi);
        var identified = datedShorts.All().Where(x => !x.IsVirtual).OrderBy(x => x.Episode).ToArray();
        check(identified.All(x => x.EpisodeSource == $"https://www.themoviedb.org/movie/{300 + x.Episode - 1}" && x.EpisodeOverview == $"Short {x.Episode - 1}" && x.RuntimeMinutes == 7),
            $"each short in the folder gets its own film's details (got {string.Join(", ", identified.Select(x => x.EpisodeSource?.Split('/').Last()))})");
        check(identified.All(x => x.Series == "Pink Shorts" && x.ShowId == identified[0].ShowId) && ArtworkCache.IsUsable(identified[0].Poster), "the shorts stay one title, whose card takes the first short's image");
        check(LibraryCatalog.UnmatchedTitles(datedShorts.All()).Count == 0, "a folder of identified shorts leaves Unmatched");
        // The next lookup must not read the card's image as the title's identity and turn the folder into its first short.
        using (var again = new MetadataClient("fixture", shortsApi)) using (var againHttp = new HttpClient(shortsApi))
            await AutomaticArtwork.FetchAsync(datedShorts, Path.Combine(directory, "shorts-data"), "fixture", null, default, againHttp, force: true, metadata: again);
        check(datedShorts.All().Where(x => !x.IsVirtual).All(x => x.Series == "Pink Shorts" && x.MetadataType is null && x.Episode is not null && x.EpisodeSource?.Contains("/movie/") == true),
            $"a full fetch keeps the shorts one title of separate films (got {string.Join(", ", datedShorts.All().Select(x => x.MetadataType + "/" + x.Series).Distinct())})");
        // Without years, the files' length picks the short over the feature that shares its name.
        var undated = Store(Path.Combine(directory, "shorts-undated"));
        for (var i = 0; i < cartoons.Length; i++) undated.Save(Cartoon("shorts-undated", i + 1, cartoons[i]));
        var lengthApi = new OrderHandler();
        lengthApi.Add("/3/search/tv", """{"results":[]}"""); lengthApi.Add("/3/search/movie", """{"results":[]}""");
        for (var i = 0; i < cartoons.Length; i++)
        {
            lengthApi.Search("movie", cartoons[i], $$"""{"results":[{"id":{{300 + i}},"title":"{{cartoons[i]}}","release_date":"1965-01-01"},{"id":{{400 + i}},"title":"{{cartoons[i]}}","release_date":"1929-01-01"}]}""");
            lengthApi.Add($"/3/movie/{300 + i}", $$"""{"id":{{300 + i}},"title":"{{cartoons[i]}}","release_date":"1965-01-01","runtime":6,"overview":"Short {{i}}"}""");
            lengthApi.Add($"/3/movie/{400 + i}", $$"""{"id":{{400 + i}},"title":"{{cartoons[i]}}","release_date":"1929-01-01","runtime":88,"overview":"Old film"}""");
        }
        await Fetch(undated, directory, lengthApi);
        check(undated.All().Where(x => !x.IsVirtual).All(x => x.EpisodeSource?.Contains("/movie/3") == true), "a short with no year is told from a same-named feature by its length");
        // Episode titles that only happen to name films, with neither a year nor a matching length, stay a show's episodes.
        string[] office = ["Pilot", "Diversity Day", "Health Care", "The Alliance", "Basketball", "Hot Girl"];
        var plain = Store(Path.Combine(directory, "episodes"));
        var featureApi = new OrderHandler();
        featureApi.Add("/3/search/tv", """{"results":[]}"""); featureApi.Add("/3/search/movie", """{"results":[]}""");
        for (var i = 0; i < office.Length; i++)
        {
            featureApi.Search("movie", office[i], $$"""{"results":[{"id":{{500 + i}},"title":"{{office[i]}}","release_date":"2010-01-01"}]}""");
            featureApi.Add($"/3/movie/{500 + i}", $$"""{"id":{{500 + i}},"title":"{{office[i]}}","release_date":"2010-01-01","runtime":95,"overview":"Feature"}""");
            var folder = Path.Combine(directory, "episodes", "Office Show"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Office Show - S01E{i + 1:00} - {office[i]}.mkv"); File.WriteAllBytes(path, [0]);
            plain.Save(new MediaItem(LibraryIdentity.Hash(path), path, "Office Show", "Show", Series: "Office Show", Season: 1, Episode: i + 1, EpisodeTitle: office[i], ShowId: "show-office", FileRuntimeMinutes: 22));
        }
        await Fetch(plain, directory, featureApi);
        check(plain.All().All(x => x.EpisodeSource is null && x.EpisodeOverview is null), "episode titles that name unrelated films don't turn a show's episodes into those films");
        featureApi.Requests.Clear();
        await Fetch(plain, directory, featureApi, force: false);
        check(!featureApi.Requests.Contains("/3/search/movie"), "a folder whose titles weren't films isn't searched film by film again until its files change");

        // Once per matcher version, an automatic match is looked up again from the files' own reading. It changes only
        // when that lookup sees the stored match and ranks another above it.
        var recheckApi = new OrderHandler();
        recheckApi.Add("/3/search/tv", """{"results":[]}"""); recheckApi.Add("/3/search/movie", """{"results":[]}""");
        recheckApi.Search("movie", "Example Film", """{"results":[{"id":700,"title":"Example Film","release_date":"2015-01-01"},{"id":701,"title":"Example Film","release_date":"2020-01-01"}]}""");
        recheckApi.Search("movie", "Lone Film", """{"results":[{"id":711,"title":"Lone Film","release_date":"2020-01-01"}]}""");
        recheckApi.Search("movie", "Twin Film", """{"results":[{"id":720,"title":"Twin Film","release_date":"2020-01-01"},{"id":721,"title":"Twin Film","release_date":"2020-01-01"}]}""");
        foreach (var (id, title, year) in new[] { (700, "Example Film", 2015), (701, "Example Film", 2020), (710, "Lone Film", 2020), (711, "Lone Film", 2020), (720, "Twin Film", 2020), (721, "Twin Film", 2020) })
        {
            recheckApi.Add($"/3/movie/{id}", $$"""{"id":{{id}},"title":"{{title}}","release_date":"{{year}}-01-01","overview":"Film {{id}}","poster_path":"/film{{id}}.jpg"}""");
            recheckApi.Add($"/3/movie/{id}/alternative_titles", """{"titles":[]}""");
        }
        var recheckStore = Store(Path.Combine(directory, "recheck"));
        MediaItem Film(string set, string title, int stored, int storedYear, bool matched = false)
        {
            var folder = Path.Combine(directory, "recheck-files", set); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{title} (2020).mkv"); File.WriteAllBytes(path, [0]);
            var poster = Path.Combine(folder, "poster.png"); File.WriteAllBytes(poster, ArtworkFixture.Png);
            // The stored match renamed the file's reading, year included, as a fetch does.
            var read = new MediaItem(LibraryIdentity.Hash(path), path, title, "Movie", Year: 2020);
            var found = LibraryIdentity.WithProvider(read with { Year = storedYear, MetadataKind = "Movie", MetadataType = "movie", Overview = "Stored plot", Poster = poster,
                PosterSource = $"https://www.themoviedb.org/movie/{stored}", ArtworkFetchedAt = Wire.Now, MetadataFetchedAt = Wire.Now, Matched = matched }, "tmdb", stored);
            return TmdbRetention.Record(read, found, "tmdb", TmdbRetention.TitleFields);
        }
        var outranked = Film("outranked", "Example Film", 700, 2015); var unseen = Film("unseen", "Lone Film", 710, 2020);
        var twin = Film("twin", "Twin Film", 720, 2020); var picked = Film("picked", "Example Film", 700, 2015, matched: true);
        foreach (var item in new[] { outranked, unseen, twin, picked }) recheckStore.Save(item);
        check(MetadataFetchPolicy.AutomaticMatch(outranked) && !MetadataFetchPolicy.AutomaticMatch(picked), "a match a lookup made is automatic and one someone chose isn't");
        new MetadataOptions(Providers: ["tmdb"]).Save(recheckStore);
        async Task Refresh()
        {
            using var tmdb = new MetadataClient("fixture", recheckApi); using var http = new HttpClient(recheckApi);
            await AutomaticArtwork.FetchAsync(recheckStore, Path.Combine(directory, "recheck-data"), "fixture", null, default, http, metadata: tmdb);
        }
        await Refresh();
        MediaItem Now(MediaItem item) => recheckStore.Get(item.Id)!;
        check(Now(outranked) is { MetadataId: 701, Year: 2020, Overview: "Film 701" }, $"a match the new lookup ranks below another is replaced (got {Now(outranked).MetadataId}, {Now(outranked).Year}, {Now(outranked).Overview})");
        check(Now(unseen).MetadataId == 710, "a match the new lookup doesn't see stays");
        check(Now(twin).MetadataId == 720, "a match the new lookup ties with another stays");
        check(Now(picked).MetadataId == 700 && Now(picked).Overview == "Stored plot", "a match someone chose isn't looked up again");
        check(!MetadataFetchPolicy.RecheckPending(recheckStore), "the recheck is recorded once it has run");
        recheckApi.Requests.Clear();
        await Refresh();
        check(!recheckApi.Requests.Any(x => x.StartsWith("/3/search", StringComparison.Ordinal)), "the next fetch doesn't look the matches up again");
        // While TMDB is down, a recheck keeps the match and waits for the next fetch; another provider's title search can't replace it.
        var outage = new OrderHandler { TmdbDown = true };
        outage.Add("/search/shows", """[{"score":1,"show":{"id":900,"name":"Example Film","type":"Scripted","premiered":"2020-01-01","summary":"Another show","image":{"medium":"https://static.tvmaze.com/900.jpg"}}}]""");
        var outageStore = Store(Path.Combine(directory, "recheck-outage"));
        var stored = Film("outage", "Example Film", 700, 2015); outageStore.Save(stored);
        new MetadataOptions(Providers: ["tmdb", "tvmaze"]).Save(outageStore);
        using (var down = new MetadataClient("fixture", outage)) using (var downHttp = new HttpClient(outage))
            await AutomaticArtwork.FetchAsync(outageStore, Path.Combine(directory, "recheck-outage-data"), "fixture", null, default, downHttp, metadata: down);
        check(outageStore.Get(stored.Id) is { MetadataProvider: "tmdb", MetadataId: 700 } && !outage.Requests.Contains("/search/shows"), "a recheck during a TMDB outage asks no other provider and keeps the match");
        check(MetadataFetchPolicy.RecheckPending(outageStore), "a recheck that couldn't reach TMDB runs again next time");

        // A match someone chose stays, whatever the files say.
        var chosen = Store(Path.Combine(directory, "chosen"));
        for (var i = 0; i < shorts.Length; i++) chosen.Save(Episode(directory, "chosen", "The Pink Panther", 20, 1, i + 1, shorts[i]) with { Matched = true });
        await Fetch(chosen, directory, wrong);
        check(chosen.All().All(x => x.MetadataId == 20), "a match someone chose isn't dropped");
    }

    static LibraryStore Store(string path) { Directory.CreateDirectory(path); return new LibraryStore(path); }

    static MediaItem Episode(string directory, string set, string show, int tmdb, int season, int episode, string title)
    {
        var folder = Path.Combine(directory, set + "-files"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{show} - S{season:00}E{episode:00} - {title}.mkv");
        File.WriteAllBytes(path, [0]);
        return new MediaItem(LibraryIdentity.Hash(path), path, show, "Show", Series: show, Season: season, Episode: episode, EpisodeTitle: title,
            ShowId: "show-" + LibraryIdentity.Hash(set), MetadataProvider: "tmdb", MetadataId: tmdb, ProviderIds: new() { ["tmdb"] = tmdb.ToString() },
            NumberingSource: "filename", MetadataType: "tv", MetadataKind: "Show");
    }

    static async Task Fetch(LibraryStore store, string directory, OrderHandler handler, bool force = true)
    {
        new MetadataOptions(Providers: ["tmdb"]).Save(store);
        using var tmdb = new MetadataClient("fixture", handler);
        using var http = new HttpClient(handler);
        var data = Path.Combine(directory, Path.GetFileName(store.DataDirectory) + "-data");
        Directory.CreateDirectory(data);
        await SeriesMetadata.FetchAsync(store, data, "fixture", null, default, http, force: force, metadata: tmdb);
    }

    static string Season(int number, string[] titles) => JsonSerializer.Serialize(new
    {
        name = $"Season {number}", overview = "", poster_path = (string?)null,
        episodes = titles.Select((t, i) => new { episode_number = i + 1, name = t, overview = "Plot of " + t, still_path = (string?)null, air_date = "2020-01-01", runtime = 20 })
    });

    static string Group(Dictionary<string, (int, int)> aired, params (int Order, string[] Titles)[] seasons) => JsonSerializer.Serialize(new
    {
        groups = seasons.Select(s => new
        {
            order = s.Order, name = $"Part {s.Order}",
            episodes = s.Titles.Select((t, i) => new { order = i, name = t, overview = "Plot of " + t, still_path = (string?)null, air_date = "2020-01-01", runtime = 20, season_number = aired[t].Item1, episode_number = aired[t].Item2 })
        })
    });

    sealed class OrderHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> routes = [];
        public List<string> Requests { get; } = [];
        public bool TmdbDown { get; init; }
        public void Add(string path, string body) => routes[path] = body;
        // A search answered by what it searched for: "/3/search/movie?query=Pink Pajamas".
        public void Search(string type, string query, string body) => routes[$"/3/search/{type}|{query.ToLowerInvariant()}"] = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            if (request.RequestUri.Host == "image.tmdb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) });
            if (TmdbDown && request.RequestUri.Host == "api.themoviedb.org") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["query"];
            if (query is not null && routes.TryGetValue(path + "|" + query.ToLowerInvariant(), out var searched))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searched, Encoding.UTF8, "application/json") });
            return Task.FromResult(routes.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }
}
