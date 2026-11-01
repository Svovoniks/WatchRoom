using BlackPearl.Core;

static class OpenedFileFixture
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "blackpearl-opened-" + Guid.NewGuid().ToString("N"));
        string In(params string[] parts) => Path.Combine([root, .. parts]);
        var episode = FileNames.Parse(In("Shows", "Breaking Bad", "Season 1", "Breaking.Bad.S01E02.720p.mkv"), "Show") with { FileSize = 1000 };
        var placeholder = episode with { Id = "virtual", Path = In("Shows", "Breaking Bad", "Season 1", "missing"), Episode = 3, IsVirtual = true, Available = false };
        var anime = FileNames.Parse(In("Anime", "Attack on Titan", "Season 1", "Attack on Titan - S01E03.mkv"), "Anime") with
        { Series = "Attack on Titan", OriginalTitle = "Shingeki no Kyojin", Matched = true, FileSize = 2000 };
        var movie = FileNames.Parse(In("Movies", "Inception (2010)", "Inception.2010.1080p.mkv"), "Movie") with { FileSize = 3000 };
        var untitled = FileNames.Parse(In("Movies", "Home Video.mkv"), "Movie") with { FileSize = 4000 };
        MediaItem[] library = [episode, placeholder, anime, movie, untitled];

        var exact = OpenedFiles.Resolve(library, episode.Path.ToUpperInvariant(), 1000);
        check(exact.Library?.Id == episode.Id && exact.Match == "path", "an opened library file plays as its own library record");
        var copy = OpenedFiles.Resolve(library, In("Downloads", "Breaking.Bad.S01E02.720p.mkv"), 1000);
        check(copy.Library?.Id == episode.Id && copy.Item.Id == episode.Id && copy.Item.Path == In("Downloads", "Breaking.Bad.S01E02.720p.mkv"),
            "a copy with the same name and size keeps the library record's progress while playing the opened file");
        var release = OpenedFiles.Resolve(library, In("Downloads", "Breaking.Bad.S01E02.1080p.WEB-DL.mkv"), 5555);
        check(release.Library?.Id == episode.Id && release.Match == "title", "another release of a known episode is recognised by show and numbering");
        var missing = OpenedFiles.Resolve(library, In("Downloads", "Breaking.Bad.S01E03.mkv"), 6000);
        check(missing.Library?.Id == placeholder.Id && missing.Item is { IsVirtual: false, Available: true },
            "a file for a missing episode plays as that episode");
        check(OpenedFiles.Resolve(library, In("Downloads", "Breaking.Bad.S09E01.mkv"), 7000).Library is null,
            "an episode the library does not know plays on its own");
        check(OpenedFiles.Resolve(library, In("Downloads", "Shingeki no Kyojin - S01E03.mkv"), 8000).Library?.Id == anime.Id,
            "an original title from matched metadata recognises the show");
        check(OpenedFiles.Resolve(library, In("Downloads", "Inception.2010.2160p.HEVC.mkv"), 9000).Library?.Id == movie.Id,
            "a movie with the same title and year is recognised");
        check(OpenedFiles.Resolve(library, In("Downloads", "Inception.mkv"), 9000).Library is null,
            "a movie title without a year is not guessed");
        var unknown = OpenedFiles.Resolve(library, In("Downloads", "Holiday 2024.mp4"), 10);
        check(unknown.Library is null && unknown.Item.Path == In("Downloads", "Holiday 2024.mp4") && unknown.Item.Available,
            "an unknown video plays as a standalone item");
        check(OpenedFiles.Resolve(library, In("Downloads", "Holiday 2024.mp4"), 10).Item.Id == unknown.Item.Id,
            "an unknown video keeps the same identity, so its position resumes next time");
        var remake = episode with { Id = "remake", Path = In("Shows", "Breaking Bad (2030)", "Season 1", "Breaking.Bad.S01E02.mkv"), ShowId = "show-remake", FileSize = 1 };
        check(OpenedFiles.Resolve([episode, remake], In("Downloads", "Breaking.Bad.S01E02.HDTV.mkv"), 42).Library is null,
            "an episode shared by two same-name shows is not guessed");
        check(OpenedFiles.IsVideo("clip.MKV") && !OpenedFiles.IsVideo("notes.txt"), "only video files can be opened");

        // A video opened at launch is matched against only the records it could belong to, with the same result as the catalog.
        var store = new LibraryStore(In("data"));
        try
        {
            foreach (var item in library.Append(remake)) store.Save(item);
            var catalog = store.All();
            (string Path, long Size)[] probes = [(episode.Path.ToUpperInvariant(), 1000), (In("Downloads", "Breaking.Bad.S01E02.720p.mkv"), 1000),
                (In("Downloads", "Breaking.Bad.S01E02.1080p.WEB-DL.mkv"), 5555), (In("Downloads", "Breaking.Bad.S01E03.mkv"), 6000),
                (In("Downloads", "Shingeki no Kyojin - S01E03.mkv"), 8000), (In("Downloads", "Inception.2010.2160p.HEVC.mkv"), 9000),
                (In("Downloads", "Home Video.mkv"), 4000), (In("Downloads", "Holiday 2024.mp4"), 10)];
            var same = probes.All(probe =>
            {
                var full = OpenedFiles.Resolve(catalog, probe.Path, probe.Size);
                var narrow = OpenedFiles.Resolve(store.OpenedFileCandidates(probe.Path), probe.Path, probe.Size);
                return full.Match == narrow.Match && full.Library?.Id == narrow.Library?.Id && full.Item.Id == narrow.Item.Id;
            });
            check(same && store.OpenedFileCandidates(In("Downloads", "Holiday 2024.mp4")).Count == 0,
                "a video opened at launch matches the library without reading the whole catalog");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
