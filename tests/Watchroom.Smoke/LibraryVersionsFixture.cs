using Watchroom.Core;

// Several releases of one episode stay reachable, and a show's extras folder joins the show as bonus clips.
static class LibraryVersionsFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var shows = Path.Combine(directory, "Shows"); var desktop = Path.Combine(directory, "Desktop"); var movies = Path.Combine(directory, "Movies");
        async Task<string> FileAt(params string[] parts)
        {
            var path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [0]); return path;
        }

        // A black-and-white cut of every episode sits beside the colour one.
        var files = new List<string>();
        foreach (var number in new[] { 1, 2 })
        {
            files.Add(await FileAt(shows, "Spider Noir", "S01 BW", $"Spider-Noir.S01e{number:00}.Bw.1080P.Webrip.X265-Kontrast.mp4"));
            files.Add(await FileAt(shows, "Spider Noir", "S01 Color", $"Spider-Noir.S01e{number:00}.1080P.Webrip.X265-Kontrast.mp4"));
        }
        var noir = files.Select(path => FileNames.Parse(path, "Mixed", shows)).ToArray();
        check(noir.Select(x => x.ShowId).Distinct().Count() == 1 && noir.All(x => x.Season == 1 && x.Episode is 1 or 2), "both releases of a show parse into one show");
        var showKey = LibraryIdentity.ShowKey(noir[0]);
        var versions = LibraryCatalog.Versions(noir, noir[0]);
        check(versions.Length == 2 && LibraryCatalog.VersionLabel(noir[0], versions) == "BW" && LibraryCatalog.VersionLabel(noir[1], versions) == "Color",
            "version labels come from the words that differ between the releases");
        check(LibraryCatalog.Versions(noir, noir[0] with { Series = null, Season = null, Episode = null }).Length == 1, "a standalone video has no other versions");
        var season = LibraryCatalog.Browse(noir, showKey, "Show", 1);
        check(season.Count == 2 && season.All(card => card.Versions == 2 && card.Version == "BW" && card.Media.Path.Contains("S01 BW")) && season[0].Caption.Contains("BW · 2 versions"),
            "the season shows one card per episode and names the release it leads with");
        var colour = LibraryCatalog.Browse(noir, showKey, "Show", 1, preferVersion: "Color");
        check(colour.Count == 2 && colour.All(card => card.Version == "Color" && card.Media.Path.Contains("S01 Color")), "a preferred version swaps every episode card to that release");
        var showCard = LibraryCatalog.Browse(noir).Single();
        check(showCard.Caption.Contains("2 episodes") && LibraryCatalog.CardItems(noir, showCard, preferVersion: "Color").All(x => x.Path.Contains("S01 Color")) && LibraryCatalog.CardItems(noir, showCard).Count() == 2,
            "show queues follow the preferred release and still hold one file per episode");
        var bwFirst = noir.Single(x => x.Episode == 1 && x.Path.Contains("S01 BW")); var colourFirst = noir.Single(x => x.Episode == 1 && x.Path.Contains("S01 Color"));
        check(EpisodePlayback.Next(noir, bwFirst, false, _ => true)?.Path.Contains("S01 BW") == true && EpisodePlayback.Next(noir, colourFirst, false, _ => true)?.Path.Contains("S01 Color") == true,
            "the next episode keeps to the release being watched");
        check(EpisodePlayback.Next(noir.Where(x => x.Episode == 1 || x.Path.Contains("S01 BW")), colourFirst, false, _ => true)?.Episode == 2,
            "a release missing the next episode falls back to the other release");
        check(EpisodePlayback.Episodes(noir, colourFirst).Select(x => x.Path).All(path => path.Contains("S01 Color")) && EpisodePlayback.Episodes(noir, colourFirst).Length == 2,
            "the player's episode list follows the current release");
        var twins = new[] { noir[0], noir[0] with { Id = "twin", Path = Path.Combine(shows, "Spider Noir", "Other", Path.GetFileName(noir[0].Path)) } };
        check(LibraryCatalog.VersionLabel(twins[0], twins) == "S01" && LibraryCatalog.VersionLabel(twins[1], twins) == "Other", "identical file names are told apart by their folders");
        var revised = new[] { noir[0], noir[0] with { Id = "v2", Path = noir[0].Path.Replace(".mp4", " v2.mp4") } };
        check(LibraryCatalog.VersionLabel(revised[1], revised) == "v2" && LibraryCatalog.VersionLabel(revised[0], revised) == Path.GetFileNameWithoutExtension(noir[0].Path),
            "a release with nothing of its own is named by its file");

        // A release that disappears while another file of the episode remains was replaced, not lost.
        var noirStore = new LibraryStore(Path.Combine(directory, "noir-db"));
        await noirStore.ScanAsync([new(shows)]);
        var bwRecord = noirStore.All().Single(x => x.Path == bwFirst.Path); var colourRecord = noirStore.All().Single(x => x.Path == colourFirst.Path);
        noirStore.Setting("watched:" + bwRecord.Id, "true"); noirStore.Setting("position:" + bwRecord.Id, "123000");
        File.Delete(bwFirst.Path);
        await noirStore.ScanAsync([new(shows)]);
        check(noirStore.All().All(x => x.Path != bwFirst.Path) && noirStore.All().Count == 3 && noirStore.Setting("watched:" + colourRecord.Id) == "true" && noirStore.Setting("position:" + colourRecord.Id) == "123000",
            "a replaced release drops out of the library and hands its watched state and position to the remaining file");
        File.Delete(noir.Single(x => x.Episode == 2 && x.Path.Contains("S01 BW")).Path); File.Delete(noir.Single(x => x.Episode == 2 && x.Path.Contains("S01 Color")).Path);
        await noirStore.ScanAsync([new(shows)]);
        check(noirStore.All().Count(x => x.Episode == 2 && !x.Available) == 2, "an episode whose every file is gone stays listed as unavailable");

        // A show's extras folder joins the show as bonus clips, numbered or not.
        var komi = "Komi Can't Communicate [Season 1 + 2 + Extras] [JPBD 1080p HEVC AAC OPUS] [Dual Audio-EngSubs] (Batch)";
        var episodePath = await FileAt(desktop, komi, "Season 1", "[DiabloTripleA] Komi Can't Communicate - S01E01.mkv");
        var clipPaths = new[] {
            await FileAt(desktop, komi, "Season 1", "Extras", "[DiabloTripleA] Komi Can't Communicate - NCED.mkv"),
            await FileAt(desktop, komi, "Season 1", "Extras", "[DiabloTripleA] Komi Can't Communicate - NCOP.mkv"),
            await FileAt(desktop, komi, "Season 1", "Extras", "[DiabloTripleA] Komi Can't Communicate - S01E12 NCED.mkv") };
        var episode = FileNames.Parse(episodePath, "Mixed", desktop);
        var clips = clipPaths.Select(path => FileNames.Parse(path, "Mixed", desktop)).ToArray();
        check(clips.All(x => x.IsBonus && !x.IsExtra && x.Season == 0 && x.Episode is null && x.EpisodeEnd is null && x.NumberingSource is null && x.ShowId == episode.ShowId && x.Series == episode.Series),
            "extras under a season folder become the show's bonus clips, numbered or not");
        check(clips[0].EpisodeDisplayTitle == "NCED" && clips[2].EpisodeDisplayTitle == "S01E12 NCED" && clips[2].Caption.EndsWith("Ending"), "a bonus clip is titled by its file name without the show's name");
        check(episode.Series == "Komi Can't Communicate" && episode.Title == "Komi Can't Communicate", "bracketed release tags and batch notes drop out of a show folder's title");
        check(FileNames.StripReleaseTags("Doctor Who (2005)") == "Doctor Who (2005)" && FileNames.StripReleaseTags("[Group] Show Name (BD 1080p)") == "Show Name" && FileNames.StripReleaseTags("[Only Tags]") == "[Only Tags]",
            "release tag stripping keeps years, plain names, and names made only of tags");
        check(clips.All(x => x.AirsAfterSeason == 1), "clips from a season's extras folder remember that season");
        var beside = FileNames.Parse(await FileAt(desktop, komi, "Extras", "[DiabloTripleA] Komi Can't Communicate - Preview.mkv"), "Mixed", desktop);
        check(beside.IsBonus && !beside.IsExtra && beside.ShowId == episode.ShowId && beside.AirsAfterSeason is null, "an extras folder beside the season folders belongs to the show as a whole");
        var special = FileNames.Parse(await FileAt(desktop, komi, "Extras", "[DiabloTripleA] Komi Can't Communicate - S00E01.mkv"), "Mixed", desktop);
        check(!special.IsBonus && !special.IsExtra && special.Season == 0 && special.Episode == 1 && special.ShowId == episode.ShowId, "an explicit special inside an extras folder keeps its number");
        var making = FileNames.Parse(await FileAt(movies, "Inception (2010)", "Extras", "Making Of.mkv"), "Mixed", movies);
        var trailer = FileNames.Parse(await FileAt(movies, "Inception (2010)", "Inception-trailer.mkv"), "Mixed", movies);
        var sample = FileNames.Parse(await FileAt(desktop, komi, "Season 1", "Extras", "sample.mkv"), "Mixed", desktop);
        check(making.IsExtra && !making.IsBonus && trailer.IsExtra && sample.IsExtra && !sample.IsBonus, "a film's extras and trailers and any sample stay out of the catalog");
        // A film inside an anime library keeps its featurettes to itself: there is a film beside the folder, not episodes.
        var anime = Path.Combine(directory, "Anime");
        var kiki = FileNames.Parse(await FileAt(anime, "Kiki's Delivery Service", "Kiki's Delivery Service (1989) (1080p BluRay x265).mkv"), "Mixed", anime);
        var storyboards = await FileAt(anime, "Kiki's Delivery Service", "Featurettes", "Original Japanese Storyboards.mkv");
        var storyboard = FileNames.Parse(storyboards, "Mixed", anime);
        check(storyboard is { IsExtra: false, IsBonus: true, Series: null, ShowId: null, Title: "Original Japanese Storyboards", Caption: "Bonus content" } && storyboard.ExtraOf == kiki.Id && kiki.ExtraOf is null,
            "a film's featurettes in an anime library are the film's own clips rather than a show");
        var kikiCards = LibraryCatalog.Browse([kiki, storyboard]);
        check(kikiCards.Count == 1 && kikiCards[0].Media.Id == kiki.Id && kikiCards[0].Caption.EndsWith("1989 · 1 extra") && LibraryCatalog.Extras([kiki, storyboard], kiki).Single().Id == storyboard.Id,
            "a film card counts its clips and its page lists them");
        var making2 = FileNames.Parse(await FileAt(movies, "Inception (2010)", "Inception.2010.1080p.mkv"), "Mixed", movies);
        check(FileNames.Parse(Path.Combine(movies, "Inception (2010)", "Extras", "Making Of.mkv"), "Mixed", movies).ExtraOf == making2.Id, "a film's extras folder attaches to the film beside it in a movie library");
        await FileAt(anime, "Outlaw Star", "Outlaw Star - 01 (BDRip 1080p).mkv");
        var ncop = FileNames.Parse(await FileAt(anime, "Outlaw Star", "Extras", "Outlaw Star - NCOP1.mkv"), "Mixed", anime);
        check(ncop is { IsExtra: false, IsBonus: true, Series: "Outlaw Star", AirsAfterSeason: null }, "an extras folder beside numbered episodes belongs to the show");
        // "XX" stands in for an episode number, so these are the show's clips rather than films named after the whole file.
        var azumanga = Path.Combine(directory, "Azumanga");
        var azumangaEpisode = FileNames.Parse(await FileAt(azumanga, "Azumanga Daioh", "Azumanga Daioh - 01 - Miss Yukari [KM-Dual-DVDRIP].mkv"), "Mixed", azumanga);
        var azumangaClipPath = await FileAt(azumanga, "Azumanga Daioh", "Azumanga Daioh - XX - Original Opening Sequence [KM-Dual-DVDRIP].mkv");
        var azumangaClip = FileNames.Parse(azumangaClipPath, "Mixed", azumanga);
        check(azumangaClip is { IsBonus: true, Season: 0, Episode: null, Series: "Azumanga Daioh", EpisodeTitle: "Original Opening Sequence" } && azumangaClip.ShowId == azumangaEpisode.ShowId,
            "a clip numbered XX joins its show's extras under its own title");
        var azumangaStore = new LibraryStore(Path.Combine(directory, "azumanga-db"));
        azumangaStore.Save(azumangaClip with { Title = "Azumanga Daioh - XX - Original Opening Sequence", Kind = "Movie", Series = null, ShowId = null, Season = null, EpisodeTitle = null, IsBonus = false });
        await azumangaStore.ScanAsync([new(azumanga)]);
        check(azumangaStore.All().Single(x => x.Path == azumangaClipPath) is { IsBonus: true, Series: "Azumanga Daioh", Season: 0, Kind: "Show" },
            "a rescan moves an XX clip indexed as a film into its show");
        // A folder's numbered episodes show how its files start, so any file that starts that way without an episode number is one of the show's clips.
        async Task<MediaItem> Mixed(string name) => FileNames.Parse(await FileAt(azumanga, "Azumanga Daioh", name), "Mixed", azumanga);
        var gallery = await Mixed("Azumanga Daioh - XX - Movie Character Art Gallery [KM-Dual-DVDRIP].mkv");
        var closing = await Mixed("Azumanga Daioh - XX - Original Closing Sequence [KM-Dual-DVDRIP].mkv");
        var shortFilm = await Mixed("Azumanga Daioh - XX - The Very Short Azumanga Daioh Movie [KM-Dual-DVDRIP].mkv");
        var nced = await Mixed("Azumanga Daioh - NCED [KM-Dual-DVDRIP].mkv");
        var ova = await Mixed("Azumanga Daioh - OVA 2 [KM-Dual-DVDRIP].mkv");
        var op = await Mixed("Azumanga Daioh - OP 2 - Soramimi Cake.mkv");
        var pv = await Mixed("Azumanga Daioh - PV.mkv");
        var menu = await Mixed("Azumanga Daioh - Menu.mkv");
        var azumangaClips = new[] { azumangaClip, gallery, closing, shortFilm, nced, ova, op, pv, menu };
        check(azumangaClips.All(x => x is { IsBonus: true, Season: 0, Episode: null, Series: "Azumanga Daioh", Kind: "Show" } && x.ShowId == azumangaEpisode.ShowId),
            "files that start like the folder's episodes but carry no episode number are the show's clips");
        check(azumangaClip.BonusKind == "opening" && closing.BonusKind == "ending" && gallery.BonusKind is null && shortFilm.BonusKind == "special" &&
            nced.BonusKind == "ending" && ova.BonusKind == "special" && op.BonusKind == "opening" && pv.BonusKind == "trailer" && menu.BonusKind is null,
            "a clip's kind comes from its slot word or its title");
        check(nced.EpisodeTitle == "NCED" && ova.EpisodeTitle == "OVA 2" && op.EpisodeTitle == "Soramimi Cake" && menu.EpisodeTitle == "Menu" && gallery.EpisodeTitle == "Movie Character Art Gallery",
            "a clip is titled by its own name, or by its slot word when it has none");
        check(nced.Caption == "Azumanga Daioh · Ending" && menu.Caption == "Azumanga Daioh · Bonus content", "a clip's caption names its kind");
        var azumangaExtras = LibraryCatalog.Browse(azumangaClips.Append(azumangaEpisode).ToArray(), azumangaEpisode.ShowId, "Show", 0);
        check(azumangaExtras.Select(card => card.Media.BonusOrder).SequenceEqual(azumangaExtras.Select(card => card.Media.BonusOrder).Order()) && azumangaExtras[0].Media.BonusKind == "special",
            "extras list specials first, then openings, endings, trailers and the rest");
        // A file of another title that only shares the start of the name stays a title of its own.
        var sequel = await Mixed("Azumanga Daioh - The Movie (2001).mkv");
        check(sequel is { IsBonus: false, Series: null, Kind: "Movie" }, "a file that names a year after the show's name is a film, not a clip");
        var episodeTwo = await Mixed("Azumanga Daioh - 02 - Chiyo.mkv");
        check(episodeTwo is { IsBonus: false, Season: 1, Episode: 2 } && episodeTwo.ShowId == azumangaEpisode.ShowId, "an episode beside clips keeps its number");
        var films = Path.Combine(directory, "Films");
        var oceans = new[] { "Ocean's Eleven (2001).mkv", "Ocean's Twelve (2004).mkv", "Alien (1979).mkv" }.Select(name => FileNames.Parse(FileAt(films, name).Result, "Mixed", films)).ToArray();
        check(oceans.All(x => x is { IsBonus: false, Series: null, Kind: "Movie" }), "films in a folder without episodes stay films");
        var grouped = Path.Combine(directory, "Grouped");
        var groupEpisode = FileNames.Parse(await FileAt(grouped, "[Group] Mushishi - 01 [1080p].mkv"), "Mixed", grouped);
        var groupClip = FileNames.Parse(await FileAt(grouped, "[Group] Mushishi - NCOP [1080p].mkv"), "Mixed", grouped);
        check(groupClip is { IsBonus: true, BonusKind: "opening", Kind: "Anime" } && groupClip.ShowId == groupEpisode.ShowId && groupEpisode.Kind == "Anime",
            "a fansub group's clip joins its anime in a Mixed folder");
        var all = clips.Append(episode).Append(beside).ToArray();
        var seasons = LibraryCatalog.Browse(all, episode.ShowId, "Show");
        check(seasons.Select(x => x.DisplayTitle).SequenceEqual(["Season 1", "Extras"]) && seasons[1].Caption == "4 bonus clips", "bonus clips gather in an Extras season after the numbered seasons");
        check(seasons[0].Caption == "1 episodes · 3 extras", "a season card counts the extras from its own folder");
        var seasonOne = LibraryCatalog.Browse(all, episode.ShowId, "Show", 1);
        check(seasonOne.Count == 4 && seasonOne[0].Media.Id == episode.Id && seasonOne.Skip(1).All(card => card.Media.IsBonus) && LibraryCatalog.CardItems(all, seasons[0]).Count() == 1,
            "a season lists its own extras after its episodes while its queue holds only episodes");
        check(LibraryCatalog.Browse(all.Append(special).ToArray(), episode.ShowId, "Show")[1] is { DisplayTitle: "Specials", Caption: "1 episodes · 4 bonus clips" }, "a numbered special keeps the season called Specials");
        var extras = LibraryCatalog.Browse(all, episode.ShowId, "Show", 0);
        check(extras.Count == 4 && extras.Any(card => card.DisplayTitle == "NCOP") && extras.All(card => card.Versions == 1), "extras cards carry readable clip titles");
        check(LibraryCatalog.SeasonName(0, all.Where(x => x.Season == 0)) == "Extras" && LibraryCatalog.SeasonName(0, all.Append(special).Where(x => x.Season == 0)) == "Specials" && LibraryCatalog.SeasonName(0) == "Specials",
            "season names follow the season's content");
        var store = new LibraryStore(Path.Combine(directory, "db"));
        store.Save(clips[0] with { AirsAfterSeason = null });
        await store.ScanAsync([new(desktop), new(movies)]);
        var scanned = store.All();
        check(scanned.Count == 8 && scanned.Count(x => x.IsBonus) == 5 && scanned.All(x => !x.IsExtra) && scanned.Count(x => x.Series is null) == 2 && scanned.Count(x => x.ExtraOf is not null) == 1,
            "a scan indexes the show's extras and the film's extras and leaves samples and trailers out");
        check(scanned.Single(x => x.Id == clips[0].Id).AirsAfterSeason == 1, "a rescan gives clips indexed earlier their home season");
        File.Delete(clipPaths[0]);
        await store.ScanAsync([new(desktop), new(movies)]);
        check(store.All().Single(x => x.Path == clipPaths[0]) is { IsBonus: true, IsExtra: false, Available: false }, "a bonus clip whose file is gone turns unavailable instead of hidden");
        // A season with no file at all is missing as a whole, a season that has not aired is upcoming, and a season with files counts its aired gaps.
        var placeholder = episode with { Id = "missing-1", Path = "missing-1.mkv", Season = 3, Episode = 1, IsVirtual = true, AirDate = "2020-01-01" };
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1).ToString("yyyy-MM-dd");
        var seasonCards = LibraryCatalog.Browse([episode, placeholder, placeholder with { Id = "missing-2", Episode = 2 },
            placeholder with { Id = "soon-1", Season = 4, AirDate = future }, placeholder with { Id = "partial", Season = 1, Episode = 2 }], episode.ShowId, "Show");
        check(seasonCards.Single(card => card.Season == 3) is { Status: "Missing" } missingSeason && missingSeason.Caption.EndsWith("2 episodes · Missing") &&
            seasonCards.Single(card => card.Season == 4) is { Status: "Upcoming" } && seasonCards.Single(card => card.Season == 1) is { Status: "1 missing" } partialSeason && partialSeason.Caption.EndsWith("2 episodes · 1 missing"),
            "seasons made only of placeholders are marked missing or upcoming, and a season with files shows how many of its episodes are missing");
        // A second copy of the show in another library folder is the same show: the batch joins the anime copy, extras included.
        await FileAt(anime, "Komi Can't Communicate", "Season 1", "[EMBER] Komi Can't Communicate - S01E02.mkv");
        var mergeStore = new LibraryStore(Path.Combine(directory, "merge-db"));
        await mergeStore.ScanAsync([new(desktop), new(anime, "Anime")]);
        var merged = mergeStore.All().Where(x => x.Series == "Komi Can't Communicate").ToArray();
        check(merged.Length == 6 && merged.Select(x => x.ShowId).Distinct().Count() == 2, "two unidentified folders of one name stay separate shows");
        var identified = merged.Single(x => x.Kind == "Anime");
        mergeStore.Save(LibraryIdentity.WithProvider(identified, "tvmaze", 32543));
        await mergeStore.ScanAsync([new(desktop), new(anime, "Anime")]);
        merged = mergeStore.All().Where(x => x.Series == "Komi Can't Communicate").ToArray();
        check(merged.Length == 6 && merged.Select(x => x.ShowId).Distinct().Count() == 1 && merged.All(x => x.Kind == "Anime"),
            "once one copy is identified, the other folder of the show joins it and takes its kind");
        var mergedCards = LibraryCatalog.Browse(mergeStore.All());
        check(mergedCards.Count(card => card.DisplayTitle == "Komi Can't Communicate") == 1 && LibraryCatalog.Browse(mergeStore.All(), merged[0].ShowId, "Anime", 1).Count(card => card.Media.IsBonus) == 2,
            "the merged show has one card and its season still ends with the batch's extras");
        await mergeStore.ScanAsync([new(desktop), new(anime, "Anime")]);
        check(mergeStore.All().Where(x => x.Series == "Komi Can't Communicate").Select(x => x.ShowId).Distinct().Count() == 1, "a rescan keeps the merged show together");
        // A clip indexed as a show's by an earlier rule becomes a hidden extra again on rescan rather than an unavailable show.
        var kikiStore = new LibraryStore(Path.Combine(directory, "kiki-db"));
        kikiStore.Save(FileNames.Parse(storyboards, "Mixed", anime) with { IsExtra = false, IsBonus = true, Season = 0, Series = "Kiki's Delivery Service", ShowId = "stale-show" });
        await kikiStore.ScanAsync([new(anime)]);
        check(kikiStore.All().Single(x => x.Path == storyboards) is { IsExtra: false, IsBonus: true, Series: null, ShowId: null, Available: true } clip && clip.ExtraOf == kiki.Id &&
            LibraryCatalog.Browse(kikiStore.All()).Count(card => card.Level == "movie") == 1 && !LibraryCatalog.Browse(kikiStore.All()).Any(card => card.Level == "series" && card.DisplayTitle.StartsWith("Kiki")),
            "a rescan moves a film's featurettes that an earlier scan had indexed as a show back to the film");
    }
}
