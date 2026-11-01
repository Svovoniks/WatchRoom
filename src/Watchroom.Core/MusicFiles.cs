using System.Text.RegularExpressions;

namespace Watchroom.Core;

// Audio files from Music library folders. A track joins the library as an episode of its album: the album is the
// track's Series, the disc its Season and the track number its Episode, so album pages, Play all, queues and
// playing on to the next track use the same paths as shows. Albums never take part in film or show metadata lookups.
public static partial class MusicFiles
{
    public const string Kind = "Music";
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".wav", ".wma", ".aif", ".aiff", ".ape", ".wv", ".mka", ".mpc" };
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart", "albumartsmall"];
    private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png"];

    public static bool IsAudio(string path) => Extensions.Contains(Path.GetExtension(path));
    public static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    // A disc folder ("CD 2", "Disc 1") belongs to the album folder above it.
    [GeneratedRegex(@"(?i)^(?:cd|dis[ck])[ ._-]*(\d{1,2})\b")]
    private static partial Regex DiscFolder();
    // "01 - Title", "01. Title", "01 Title", "1-03 Title" (disc 1, track 3). "A1 Title" is left alone, and so is a number that is
    // part of the title, such as "99 Luftballons": a number followed only by a space counts only when zero-padded.
    [GeneratedRegex(@"^(?:(?<disc>\d)[-.](?<track>\d{2})(?:\s*[-._)]\s*|\s+)|(?<track>\d{1,3})\s*[-._)]\s*|(?<track>0\d{1,2})\s+)(?<title>.+)$")]
    private static partial Regex NumberedName();

    public static MediaItem Parse(string path, string? libraryRoot = null, string? artworkDirectory = null)
    {
        path = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(path)!;
        var albumFolder = folder; int? folderDisc = null;
        if (DiscFolder().Match(Path.GetFileName(folder)) is { Success: true } disc && Path.GetDirectoryName(folder) is { } parent &&
            (libraryRoot is null || LibraryStore.IsWithin(parent, libraryRoot)))
        { albumFolder = parent; folderDisc = int.Parse(disc.Groups[1].Value); }
        var atRoot = libraryRoot is not null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot))
            .Equals(Path.TrimEndingDirectorySeparator(albumFolder), StringComparison.OrdinalIgnoreCase);

        var stem = Path.GetFileNameWithoutExtension(path);
        var numbered = NumberedName().Match(stem);
        string? title = null, artist = null, albumArtist = null, album = null; string[]? genres = null;
        int? track = null, discNumber = null, year = null, duration = null; TagLib.IPicture? picture = null;
        try
        {
            using var file = TagLib.File.Create(path, TagLib.ReadStyle.Average);
            var tag = file.Tag;
            title = Clean(tag.Title); album = Clean(tag.Album);
            artist = Clean(tag.FirstPerformer) ?? Clean(tag.FirstAlbumArtist); albumArtist = Clean(tag.FirstAlbumArtist);
            track = tag.Track is > 0 and < 1000 ? (int)tag.Track : null; discNumber = tag.Disc is > 0 and < 100 ? (int)tag.Disc : null;
            year = tag.Year is >= 1000 and <= 9999 ? (int)tag.Year : null;
            genres = tag.Genres?.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } list ? list : null;
            duration = file.Properties?.Duration is { TotalSeconds: >= 1 } length ? (int)Math.Round(length.TotalSeconds) : null;
            picture = tag.Pictures?.OrderByDescending(p => p.Type == TagLib.PictureType.FrontCover).FirstOrDefault(p => p.Data?.Count > 0);
        }
        // A damaged or unusual file keeps its name-based details; one bad file must not stop a library scan.
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        // Untagged files fall back to their names and folders: Artist\Album\01 - Title.
        if (numbered.Success)
        {
            track ??= int.Parse(numbered.Groups["track"].Value);
            if (numbered.Groups["disc"].Success) discNumber ??= int.Parse(numbered.Groups["disc"].Value);
        }
        title ??= numbered.Success ? Clean(numbered.Groups["title"].Value) : null;
        title ??= stem;
        // Unnumbered files play in name order.
        track ??= FolderPosition(path);
        album ??= atRoot ? null : Path.GetFileName(albumFolder);
        if (artist is null && !atRoot && Path.GetDirectoryName(albumFolder) is { } artistFolder &&
            (libraryRoot is null || LibraryStore.IsWithin(artistFolder, libraryRoot) && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot))
                .Equals(Path.TrimEndingDirectorySeparator(artistFolder), StringComparison.OrdinalIgnoreCase)))
            artist = Path.GetFileName(artistFolder);
        var albumTitle = album ?? (artist is null ? "Unknown album" : artist + " · Singles");
        // Tracks of one album share a folder, so a compilation whose tracks name different artists stays one album.
        var showId = "album-" + LibraryIdentity.Hash(LibraryIdentity.Normalize(albumTitle) + ":" + (OperatingSystem.IsWindows() ? albumFolder.ToUpperInvariant() : albumFolder));
        var cover = FolderCover(albumFolder) ?? (albumFolder != folder ? FolderCover(folder) : null) ?? SaveEmbedded(picture, artworkDirectory, showId);
        return new MediaItem(LibraryIdentity.Hash(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path), path, title, Kind, year,
            Series: albumTitle, Season: discNumber ?? folderDisc ?? 1, Episode: track, Poster: cover, SeriesPoster: cover,
            PosterSource: cover is null ? null : "Local artwork", ShowId: showId, SeriesPath: albumFolder, Genres: genres,
            SourceLibraryKind: Kind, Artist: albumArtist is not null && artist is null ? albumArtist : artist, DurationSeconds: duration);
    }

    // A rescanned track keeps its record and anything the user set by hand.
    public static MediaItem Merge(MediaItem parsed, MediaItem previous)
    {
        var item = parsed with { Id = previous.Id, LockedFields = previous.LockedFields, MetadataLocked = previous.MetadataLocked,
            Matched = previous.Matched, NumberingOrder = previous.NumberingOrder };
        if (LibraryIdentity.Locked(previous, "Title")) item = item with { Title = previous.Title, Series = previous.Series };
        if (LibraryIdentity.Locked(previous, "Poster")) item = item with { Poster = previous.Poster, SeriesPoster = previous.SeriesPoster, PosterSource = previous.PosterSource };
        if (LibraryIdentity.Locked(previous, "ShowId")) item = item with { ShowId = previous.ShowId };
        if (LibraryIdentity.Locked(previous, "Episode")) item = item with { Episode = previous.Episode };
        if (LibraryIdentity.Locked(previous, "Season")) item = item with { Season = previous.Season };
        return item;
    }

    private static int? FolderPosition(string path)
    {
        try
        {
            var files = Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Where(IsAudio).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var index = files.FindIndex(file => file.Equals(path, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : index + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    // A cover saved beside a track after it was scanned, which a rescan should pick up.
    public static bool CoverAdded(MediaItem stored) => stored.Poster is null &&
        (stored.SeriesPath is { } album && FolderCover(album) is not null || Path.GetDirectoryName(stored.Path) is { } folder && folder != stored.SeriesPath && FolderCover(folder) is not null);
    private static string? FolderCover(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (CoverExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) &&
                    CoverNames.Any(cover => name.Equals(cover, StringComparison.OrdinalIgnoreCase) || name.StartsWith("AlbumArt_", StringComparison.OrdinalIgnoreCase)))
                    return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
    // Artwork inside the file is written once per album to the data folder.
    private static string? SaveEmbedded(TagLib.IPicture? picture, string? directory, string showId)
    {
        if (picture is null || directory is null) return null;
        var extension = picture.MimeType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true ? ".png" : ".jpg";
        var target = Path.Combine(directory, "music-artwork", showId + extension);
        try
        {
            if (File.Exists(target)) return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, picture.Data.Data);
            File.Move(temporary, target, true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return File.Exists(target) ? target : null; }
    }
}
