using Watchroom.Core;

static class MusicLibraryFixture
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var music = Path.Combine(root, "Music"); var films = Path.Combine(root, "Films");
        string Track(params string[] parts) { var path = Path.Combine([music, .. parts]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, Wave()); return path; }
        var first = Track("Artist A", "Album X", "01 - First Song.wav");
        var second = Track("Artist A", "Album X", "02 - Second Song.wav");
        File.WriteAllBytes(Path.Combine(music, "Artist A", "Album X", "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        var discOne = Track("Artist B", "Double", "CD1", "01 - Opening.wav");
        var discTwo = Track("Artist B", "Double", "CD 2", "01 - Closing.wav");
        var tagged = Track("Mixes", "untitled.wav");
        using (var file = TagLib.File.Create(tagged))
        {
            file.Tag.Title = "Tagged Song"; file.Tag.Performers = ["Singer"]; file.Tag.Album = "Hits"; file.Tag.Track = 5; file.Tag.Disc = 2; file.Tag.Year = 2001;
            file.Save();
        }
        Directory.CreateDirectory(films);
        File.WriteAllBytes(Path.Combine(music, "Artist A", "music video.mkv"), [0]);
        File.WriteAllBytes(Path.Combine(films, "stray song.mp3"), [0]);
        File.WriteAllBytes(Path.Combine(films, "Example Film (2001).mkv"), [0]);

        var data = Path.Combine(root, "data");
        var library = new LibraryStore(data);
        LibraryFolder[] folders = [new(music, MusicFiles.Kind), new(films, "Movie")];
        var items = await library.ScanAsync(folders);
        var songs = items.Where(x => x.IsMusic).ToArray();
        check(songs.Length == 5 && items.Count == 6, "Music folders list audio files only, and video folders list videos only");
        var one = songs.Single(x => x.Path == first); var two = songs.Single(x => x.Path == second);
        check(one.Title == "First Song" && one.Episode == 1 && two.Episode == 2 && one.Series == "Album X" && one.Artist == "Artist A",
            "untagged tracks take their number, title, album and artist from names and folders");
        check(LibraryIdentity.SameShow(one, two) && one.SeriesPoster == Path.Combine(music, "Artist A", "Album X", "cover.jpg"), "tracks of one folder form an album with its cover");
        var opening = songs.Single(x => x.Path == discOne); var closing = songs.Single(x => x.Path == discTwo);
        check(LibraryIdentity.SameShow(opening, closing) && opening.Series == "Double" && opening.Season == 1 && closing.Season == 2,
            "disc folders join their album as numbered discs");
        var hit = songs.Single(x => x.Path == tagged);
        check(hit is { Title: "Tagged Song", Artist: "Singer", Series: "Hits", Episode: 5, Season: 2, Year: 2001, DurationSeconds: 1 }, "tags name a track, its album, artist, disc, number, year and length");
        check(one.Caption == "Artist A · Album X · 0:01" && one.EpisodeDisplayTitle == "1. First Song" && one.QueueTitle == "First Song · Artist A", "tracks describe themselves as songs");

        var albums = await LibraryCatalog.SearchAsync(items, "", LibraryCatalog.Music);
        check(albums.Count == 3 && albums.All(card => card.Level == "series" && card.Media.IsMusic) &&
            albums.Single(card => card.DisplayTitle == "Album X").Caption == "Artist A · 2 tracks", "the Music category lists albums");
        check((await LibraryCatalog.SearchAsync(items, "", 1)).Single().Media.Kind == "Movie", "music stays out of the film category");
        check((await LibraryCatalog.SearchAsync(items, "Artist B", LibraryCatalog.Music)).Single().DisplayTitle == "Double", "music search finds albums by artist");
        var tracks = LibraryCatalog.Browse(items, LibraryIdentity.ShowKey(opening), MusicFiles.Kind);
        check(tracks.Select(card => card.Media.Path).SequenceEqual([discOne, discTwo]) && tracks.All(card => card.Level == "episode") && tracks[1].Caption.Contains("Disc 2"),
            "an album lists its tracks disc by disc without season cards");
        check(EpisodePlayback.Next(items, one, false, _ => true)?.Id == two.Id && EpisodePlayback.Next(items, opening, false, _ => true)?.Id == closing.Id,
            "an album plays on to its next track and disc");
        check(LibraryHome.Browse(items).All(entry => !entry.Card.Media.IsMusic), "Home suggests videos, not music");
        var guest = SharedLibrary.Browse(items, new LibraryBrowseRequest(Category: LibraryCatalog.Music), null);
        check(guest.Entries.Length == 3 && SharedLibrary.Browse(items, new LibraryBrowseRequest(), null).Entries.All(entry => entry.Kind != MusicFiles.Kind),
            "guests browse music in its own category");

        await library.ScanAsync(folders);
        check(library.LastScanChanges == 0, "a rescan leaves unchanged tracks as they are");
        var reloaded = library.PruneAndLoad([new(music, "Mixed"), new(films, "Movie")]);
        check(!reloaded.Any(x => x.IsMusic), "a folder that stops being a Music folder drops its tracks");

        var mixed = Path.Combine(root, "Mixed");
        Directory.CreateDirectory(Path.Combine(mixed, "Example Film (2001)", "Soundtrack"));
        File.WriteAllBytes(Path.Combine(mixed, "Example Film (2001)", "Example Film (2001).mkv"), [0]);
        var score = Path.Combine(mixed, "Example Film (2001)", "Soundtrack", "01 - Main Theme.wav"); File.WriteAllBytes(score, Wave());
        LibraryFolder[] mixedFolders = [new(mixed, "Mixed")];
        check(!library.MixedFoldersListMusic && !(await library.ScanAsync(mixedFolders)).Any(x => x.IsMusic), "Mixed folders leave audio files out by default");
        library.MixedFoldersListMusic = true;
        var withMusic = await library.ScanAsync(mixedFolders);
        check(withMusic.Count(x => x.IsMusic) == 1 && withMusic.Single(x => x.IsMusic) is { Title: "Main Theme", Series: "Soundtrack" } && withMusic.Any(x => !x.IsMusic),
            "Mixed folders list audio files as music when chosen, alongside their videos");
        library.MixedFoldersListMusic = false;
        check(!library.PruneAndLoad(mixedFolders).Any(x => x.IsMusic), "turning it off drops music found in Mixed folders");
    }

    // One second of 8 kHz mono silence.
    private static byte[] Wave()
    {
        const int samples = 8000; var data = samples * 2;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + data); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(data); writer.Write(new byte[data]);
        writer.Flush(); return stream.ToArray();
    }
}
