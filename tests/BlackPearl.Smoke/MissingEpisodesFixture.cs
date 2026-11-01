using BlackPearl.Core;

// Missing-episode placeholders for shows with files: only what the files' numbering shows is absent, never specials.
static class MissingEpisodesFixture
{
    public static void Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var options = new MetadataOptions(ImportMissing: true, ImportUpcoming: true);
        MetadataEpisode Episode(int number, string? title = null, string? aired = null) => new(number, title ?? $"Episode {number}", null, null, aired ?? $"2020-01-{number:00}", 24);
        MetadataSeason Season(int number, params MetadataEpisode[] episodes) => new(number, null, null, null, null, episodes);
        MetadataSeason Run(int number, int from, int to) => Season(number, Enumerable.Range(from, to - from + 1).Select(n => Episode(n, aired: $"20{number + 10}-01-01")).ToArray());
        string[] Missing(string name, MetadataSeason[] seasons, string provider, params (int? Season, int? Episode, string File)[] files)
        {
            var store = new LibraryStore(Path.Combine(directory, name));
            MediaItem? show = null;
            foreach (var (season, episode, file) in files)
            {
                var item = new MediaItem(Guid.NewGuid().ToString("N"), Path.Combine(directory, name, file), name, "Show", 2020, name, season, episode, MetadataProvider: provider, MetadataId: 1, MetadataType: "tv");
                store.Save(item); show ??= store.Get(item.Id);
            }
            SeriesMetadata.ReconcileMissing(store, directory, show!, seasons, "tmdb", 1, options);
            return store.All().Where(x => x.IsVirtual).OrderBy(x => x.Season).ThenBy(x => x.Episode).Select(x => $"S{x.Season}E{x.Episode}").ToArray();
        }

        var plain = Missing("plain", [Season(0, Episode(1), Episode(2)), Season(1, Episode(1), Episode(2), Episode(3)), Season(2, Episode(1))], "tmdb",
            (1, 1, "S01E01.mkv"), (1, 3, "S01E03.mkv"));
        check(plain.SequenceEqual(["S1E2", "S2E1"]), "episodes absent from the files are missing, and provider specials are not");

        // Files numbered on across seasons (S2 holding 13-25) don't fit a provider whose seasons restart at 1.
        var running = Missing("running", [Run(1, 1, 25), Run(2, 1, 12)], "tmdb",
            [.. Enumerable.Range(1, 12).Select(n => ((int?)1, (int?)n, $"S1 {n}.mkv")), .. Enumerable.Range(13, 13).Select(n => ((int?)2, (int?)n, $"S2 {n}.mkv"))]);
        check(running.Length == 0, "files numbered differently from the provider get no placeholders");
        var other = Missing("other", [Run(1, 1, 3), Run(2, 1, 3)], "tvmaze", (1, 1, "a.mkv"), (1, 2, "b.mkv"), (1, 3, "c.mkv"));
        check(other.Length == 0, "files matched with another provider aren't compared with this one's seasons");
        var dated = Missing("dated", [Run(1, 1, 3)], "tmdb", (null, null, "Show (01) 2007-03-26.avi"), (null, null, "Show (02) 2007-03-27.avi"));
        check(dated.Length == 0, "files the scan couldn't number make the show's missing episodes unknown");
        var pilot = Missing("pilot", [Run(1, 1, 3), Run(2, 1, 2)], "tmdb", (1, null, "HAZBIN HOTEL (PILOT).mp4"), (1, 1, "S01E01.mkv"), (1, 2, "S01E02.mkv"), (1, 3, "S01E03.mkv"));
        check(pilot.SequenceEqual(["S2E1", "S2E2"]), "one odd file such as an unnumbered pilot doesn't hide a missing season");

        // One Piece numbers episodes absolutely; a split release the scan can't number still holds its episode.
        var absolute = Missing("absolute", [Run(13, 478, 482)], "tmdb",
            [.. new[] { 478, 479, 481, 482 }.Select(n => ((int?)13, (int?)n, $"One Piece - {n}.mkv")), ((int?)13, (int?)null, "One Piece - 480A.mkv")]);
        check(absolute.Length == 0, "a file whose number the scan couldn't read still counts as its episode");
        var twice = Missing("twice", [Season(8, Episode(1, "The Impossible Stream", "2023-07-24")), Season(11, Episode(1, "The Impossible Stream", "2023-07-24"))], "tmdb", (11, 1, "S11E01.mkv"));
        var store = new LibraryStore(Path.Combine(directory, "twice"));
        check(twice.Length == 1 && store.All().Count(x => !x.IsVirtual) == 1, "fixture keeps one file of the listed-twice episode");
        var titled = store.All().Single(x => !x.IsVirtual);
        store.Save(titled with { EpisodeTitle = "The Impossible Stream", AirDate = "2023-07-24" });
        SeriesMetadata.ReconcileMissing(store, directory, titled, [Season(8, Episode(1, "The Impossible Stream", "2023-07-24")), Season(11, Episode(1, "The Impossible Stream", "2023-07-24"))], "tmdb", 1, options);
        check(store.All().All(x => !x.IsVirtual), "an episode the provider lists in two seasons isn't missing once either file is here");

        // Filename numbers the scan used to drop or misread.
        foreach (var number in new[] { 480, 720, 1080 })
            check(FileNames.Parse(Path.Combine(directory, "Anime", "One Piece", $"[AnimeRG] One Piece - {number} [1080p] [Dual-Audio] [x265].mkv")).Episode == number,
                $"episode {number} isn't mistaken for a video resolution");
        var part = FileNames.Parse(Path.Combine(directory, "Anime", "Смешарики", "Пин-код", "30. Прививка От Кроша (Серия 1).mp4"));
        check(part.Episode == 30, "a bracketed part label after a leading episode number doesn't replace it");
        check(FileNames.Parse(Path.Combine(directory, "Anime", "Example Серия 3 Pilot.mkv")).Episode == 3, "a labelled episode number still counts");
    }
}
