using BlackPearl.Core;

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
        check(FilenameEpisodes.AnalyzeFolder("Doctor Who (2005)") is { Title: "Doctor Who", Year: 2005 } && FilenameEpisodes.AnalyzeFolder("[Group] Show Name (BD 1080p)").Title == "Show Name" && FilenameEpisodes.AnalyzeFolder("[Only Tags]").Title == "Only Tags",
            "a folder's reading keeps its year apart and drops release tags, and a name made only of tags still titles it");
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
        // A show's clips are sorted by kind, read from their titles, and their captions name it.
        var azumanga = Path.Combine(directory, "Azumanga");
        async Task<MediaItem> Azumanga(string name) => FileNames.Parse(await FileAt(azumanga, "Azumanga Daioh", name), "Mixed", azumanga);
        await FileAt(azumanga, "Azumanga Daioh", "Azumanga Daioh - 02 - Chiyo [KM-Dual-DVDRIP].mkv");
        var azumangaEpisode = await Azumanga("Azumanga Daioh - 01 - Miss Yukari [KM-Dual-DVDRIP].mkv");
        var opening = await Azumanga("Azumanga Daioh - XX - Original Opening Sequence [KM-Dual-DVDRIP].mkv");
        var closing = await Azumanga("Azumanga Daioh - XX - Original Closing Sequence [KM-Dual-DVDRIP].mkv");
        var gallery = await Azumanga("Azumanga Daioh - XX - Movie Character Art Gallery [KM-Dual-DVDRIP].mkv");
        var shortFilm = await Azumanga("Azumanga Daioh - XX - The Very Short Azumanga Daioh Movie [KM-Dual-DVDRIP].mkv");
        var nced = await Azumanga("Azumanga Daioh - NCED [KM-Dual-DVDRIP].mkv");
        var pv = await Azumanga("Azumanga Daioh - PV.mkv");
        var azumangaClips = new[] { gallery, pv, closing, nced, opening, shortFilm };
        check(azumangaClips.All(x => x is { IsBonus: true, Season: 0, Episode: null } && x.ShowId == azumangaEpisode.ShowId), "clips beside the show's episodes are its bonus clips");
        check(opening.BonusKind == "opening" && closing.BonusKind == "ending" && gallery.BonusKind is null && shortFilm.BonusKind == "special" && nced.BonusKind == "ending" && pv.BonusKind == "trailer" &&
            ncop.BonusKind == "opening" && clips[1].BonusKind == "opening" && beside.BonusKind == "trailer" && storyboard.BonusKind is null && azumangaEpisode.BonusKind is null,
            "a clip's kind comes from a clip word or the words of its title");
        check(nced.Caption == "Azumanga Daioh · Ending" && gallery.Caption == "Azumanga Daioh · Bonus content" && storyboard.Caption == "Bonus content", "a clip's caption names its kind");
        var azumangaExtras = LibraryCatalog.Browse(azumangaClips.Append(azumangaEpisode).ToArray(), azumangaEpisode.ShowId, azumangaEpisode.Kind, 0);
        check(azumangaExtras.Count == 6 && azumangaExtras.Select(card => card.Media.BonusKind ?? "other").SequenceEqual(["special", "opening", "ending", "ending", "trailer", "other"]),
            "extras list specials first, then openings, endings, trailers and the rest");
        var azumangaStore = new LibraryStore(Path.Combine(directory, "azumanga-db"));
        azumangaStore.Save(opening with { BonusKind = null });
        await azumangaStore.ScanAsync([new(azumanga)]);
        check(azumangaStore.All().Single(x => x.Id == opening.Id).BonusKind == "opening", "a rescan gives clips indexed earlier their kind");
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
        MediaItem[] gaps = [episode, placeholder, placeholder with { Id = "missing-2", Episode = 2 }, placeholder with { Id = "soon-1", Season = 4, AirDate = future },
            placeholder with { Id = "partial", Season = 1, Episode = 2 }, placeholder with { Id = "partial-soon", Season = 1, Episode = 3, AirDate = future }];
        check(LibraryCatalog.MissingOnly(LibraryCatalog.Browse(gaps, episode.ShowId, "Show")).Select(card => card.Season).Order().SequenceEqual([1, 3, 4]),
            "Missing lists a show's seasons with something missing or upcoming");
        check(LibraryCatalog.MissingOnly(LibraryCatalog.Browse(gaps, episode.ShowId, "Show", 1)).Select(card => card.Media.Id).SequenceEqual(["partial"]),
            "Missing lists a season's aired gaps, not its files or the episodes still to air");
        check(LibraryCatalog.MissingOnly(LibraryCatalog.Browse(gaps, episode.ShowId, "Show", 4)).Select(card => card.Media.Id).SequenceEqual(["soon-1"]),
            "Missing lists the episodes of a season that has not aired yet");
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
        FilmCopies(directory, check);
    }

    // Copies of one identified film, in different folders or drives, share a card and a version picker.
    static void FilmCopies(string directory, Action<bool, string> check)
    {
        MediaItem Film(string id, string path, int tmdb = 949, string? title = null, string? providerTitle = "Heat") => new(id, path, title ?? "Heat", "Movie", 1995,
            Available: true, MetadataProvider: "tmdb", MetadataId: tmdb, MetadataType: "movie", ProviderTitle: providerTitle,
            ProviderIds: new() { ["tmdb"] = tmdb.ToString(), ["imdb"] = "tt" + tmdb });
        var hd = Film("hd", Path.Combine(directory, "Movies", "Heat", "Heat.1995.1080p.BluRay.x265-RARBG", "Heat.1995.1080p.BluRay.x265-RARBG.mp4"));
        var uhd = Film("uhd", Path.Combine(directory, "Desktop", "Heat.1995.REPACK.2160p.UHD.BDRemux.DTS-HD.MA.5.1.mkv"));
        var other = Film("other", Path.Combine(directory, "Movies", "Ronin.1998.1080p.mkv"), 8195, "Ronin", "Ronin");
        var films = new[] { hd, uhd, other };
        var cards = LibraryCatalog.Browse(films);
        var heat = cards.Single(card => card.DisplayTitle == "Heat");
        check(cards.Count == 2 && heat is { Level: "movie", Versions: 2 } && heat.Caption.EndsWith("1995 · 2 versions") && cards.Single(card => card.DisplayTitle == "Ronin").Versions == 1,
            "copies of one identified film share a card that counts its versions");
        check(LibraryIdentity.TitleKey(hd) == LibraryIdentity.TitleKey(uhd) && LibraryIdentity.ShowKey(hd) == hd.Id && LibraryIdentity.ShowKey(uhd) == uhd.Id,
            "a film's copies share a title key while matching still keys each file on its own");
        var versions = LibraryCatalog.Versions(films, uhd);
        check(versions.Select(x => x.Id).SequenceEqual(["uhd", "hd"]) && LibraryCatalog.VersionLabel(hd, versions) == "1080p BluRay x265" && LibraryCatalog.VersionLabel(uhd, versions) == "REPACK 2160p UHD",
            "film copies on different drives are named by what differs in their file names, not by their folders");
        MediaItem[] unknown = [hd with { ProviderIds = null, MetadataId = null }, uhd with { ProviderIds = null, MetadataId = null }];
        check(LibraryCatalog.Versions(films, other).Single() == other && LibraryCatalog.Versions(unknown, unknown[0]).Length == 1 && LibraryCatalog.Browse(unknown).Count == 2,
            "films without an identity stay apart, however alike their names");
        check(LibraryCatalog.Browse([hd, uhd with { Available = false }]).Single().Versions == 2 && LibraryCatalog.CurrentVersion(LibraryCatalog.Versions([hd, uhd with { Available = false }], hd), null).Id == "hd",
            "a copy whose drive is away never leads its film's card");

        // Parts of one film share its identity but are different videos.
        var parts = new[] { Film("p1", Path.Combine(directory, "Movies", "Chudo", "Chudo-1.mp4"), 27935, "Chudo-1", "An Ordinary Miracle"),
            Film("p2", Path.Combine(directory, "Movies", "Chudo", "Chudo-2.mp4"), 27935, "Chudo-2", "An Ordinary Miracle"),
            Film("q1", Path.Combine(directory, "Movies", "Ivan Vasilievich (1973) 1.mkv"), 1, "Ivan", "Ivan"), Film("q2", Path.Combine(directory, "Movies", "Ivan Vasilievich (1973) 2.mkv"), 1, "Ivan", "Ivan"),
            Film("c1", Path.Combine(directory, "Movies", "Seven.Samurai.CD1.avi"), 2, "Seven Samurai", "Seven Samurai"), Film("c2", Path.Combine(directory, "Movies", "Seven.Samurai.CD2.avi"), 2, "Seven Samurai", "Seven Samurai") };
        check(LibraryCatalog.Browse(parts).Count == 6 && LibraryCatalog.Browse(parts).All(card => card.Versions == 1), "numbered parts of a film keep cards of their own");
        var partCopies = new[] { parts[0], parts[0] with { Id = "p1b", Path = Path.Combine(directory, "Desktop", "Chudo-1.720p.mkv") } };
        check(LibraryIdentity.FilmPart(partCopies[1]) is null && LibraryCatalog.Browse(partCopies).Count == 2, "a part number only counts at the end of a name or after Part, CD or Disc");
        var numbered = new[] { Film("a1", Path.Combine(directory, "Movies", "Oceans 8.mkv"), 402900, "Oceans 8", "Ocean's 8"), Film("a2", Path.Combine(directory, "Desktop", "Oceans.8.2018.2160p.mkv"), 402900, "Oceans 8", "Ocean's 8"),
            Film("s1", Path.Combine(directory, "Movies", "Se7en.1995.1080p.DTS.5.1.mkv"), 807, "Se7en", "Se7en"), Film("s2", Path.Combine(directory, "Desktop", "Se7en.1995.720p.mkv"), 807, "Se7en", "Se7en") };
        check(LibraryCatalog.Browse(numbered).Count == 2 && LibraryCatalog.Browse(numbered).All(card => card.Versions == 2),
            "a number the film's own title ends with, or audio channels like 5.1, are not part numbers");

        // The card and page lead with the copy chosen for the film, otherwise the one played last.
        var key = LibraryIdentity.TitleKey(hd);
        check(LibraryCatalog.CurrentVersion(versions, null).Id == "uhd", "with nothing played, a film leads with its first available copy");
        var played = new Dictionary<string, string> { ["played:hd"] = "200", ["played:uhd"] = "100", ["position:hd"] = "61000" };
        check(LibraryCatalog.CurrentVersion(versions, played).Id == "hd" && LibraryCatalog.WithCurrentVersions(films, cards, played).Single(card => card.DisplayTitle == "Heat").Media.Id == "hd",
            "a film card leads with the copy played last");
        var chosen = new Dictionary<string, string>(played) { ["version:" + key] = "REPACK 2160p UHD" };
        check(LibraryCatalog.WithCurrentVersions(films, cards, chosen).Single(card => card.DisplayTitle == "Heat").Media.Id == "uhd", "a chosen version leads the film's card over the copy played last");

        // A clip that came with one copy belongs to the film.
        var clip = new MediaItem("clip", Path.Combine(directory, "Desktop", "Heat Extras", "Making Of.mkv"), "Making Of", "Movie", Available: true, IsBonus: true, ExtraOf: "uhd");
        check(LibraryCatalog.Browse([hd, uhd, clip]).Single().Caption.EndsWith("1995 · 1 extra · 2 versions") && LibraryCatalog.Extras([hd, uhd, clip], hd).Single().Id == "clip",
            "every copy of a film lists the clips that came with any of them");

        // Home and Search list the film once.
        var home = LibraryHome.Overview(films, played);
        check(home.Entries.Count(entry => entry.Section == "continue") == 1 && home.Entries.Single(entry => entry.Section == "continue") is { PositionMs: 61000 } resume && resume.Card.Media.Id == "hd" &&
            !home.Unseen.Any(title => title.Card.DisplayTitle == "Heat"), "Home continues a film once, with the copy left part-way");
        var watched = LibraryHome.Overview(films, new Dictionary<string, string> { ["watched:hd"] = "true" });
        check(!watched.Entries.Any(entry => entry.Section == "continue") && !watched.Unseen.Any(title => title.Card.DisplayTitle == "Heat") && LibraryHome.Overview(films).Unseen.Count(title => title.Card.DisplayTitle == "Heat") == 1,
            "a film watched in any copy is done, and an unstarted one is suggested once");
        check(LibrarySearch.Search(films, "heat", new(), new Dictionary<string, FileLanguages>()).Single().Versions == 2, "Search finds a film's copies as one title");
        var store = new LibraryStore(Path.Combine(directory, "film-copies-db"));
        store.Setting("version:" + key, "1080p BluRay x265");
        check(store.PlaybackSettings().GetValueOrDefault("version:" + key) == "1080p BluRay x265", "playback settings carry the versions chosen for titles");
    }
}
