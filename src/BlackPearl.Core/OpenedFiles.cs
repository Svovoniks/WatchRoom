namespace BlackPearl.Core;

// A video opened from Explorer or dropped on the window. Library is the catalog record it is recognised as,
// so playback keeps that record's progress, watched state, artwork and episode order; Item is what plays.
public sealed record OpenedFile(MediaItem Item, MediaItem? Library, string Match);

public static class OpenedFiles
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsVideo(string path) => FileNames.Extensions.Contains(Path.GetExtension(path));

    public static OpenedFile Resolve(IReadOnlyCollection<MediaItem> library, string path, long? size = null)
    {
        path = Path.GetFullPath(path);
        if (size is null) try { size = new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        var candidates = library.Where(item => !item.IsExtra).ToArray();
        if (candidates.FirstOrDefault(item => !item.IsVirtual && PathComparer.Equals(item.Path, path)) is { } exact)
            return new(exact with { Available = true }, exact, "path");
        // A copy elsewhere, or a library file moved out of its folders: the same name and size.
        var name = Path.GetFileName(path);
        var copies = size is null ? [] : candidates.Where(item => !item.IsVirtual && item.FileSize == size && PathComparer.Equals(Path.GetFileName(item.Path), name)).ToArray();
        if (copies.Length == 1) return Known(copies[0], path, "copy");
        var parsed = FileNames.Parse(path) with { FileSize = size };
        if (parsed.IsExtra) return new(parsed, null, "none");
        // Another release of a known episode or movie: the same title and numbering.
        var titled = candidates.Where(item => item.Series is null == parsed.Series is null && Titles(item).Contains(LibraryIdentity.Normalize(parsed.DisplayTitle))).ToArray();
        var same = parsed.Series is not null
            ? titled.Where(item => !item.IsBonus && SameEpisode(item, parsed)).ToArray()
            : parsed.Year is null ? [] : titled.Where(item => item.Year == parsed.Year).ToArray();
        if (same.Length > 0 && same.All(item => parsed.Series is null ? same[0].Id == item.Id : LibraryIdentity.SameShow(item, same[0])))
            return Known(same.OrderBy(item => item.IsVirtual).ThenBy(item => !item.Available).ThenBy(item => item.Path, PathComparer).First(), path, "title");
        return new(parsed, null, "none");
    }

    private static OpenedFile Known(MediaItem item, string path, string match) =>
        new(item with { Path = path, Available = true, IsVirtual = false }, item, match);

    private static HashSet<string> Titles(MediaItem item)
    {
        var titles = new[] { item.Series, item.Title, item.OriginalTitle, item.SeriesPath is null ? null : Path.GetFileName(item.SeriesPath) };
        return titles.Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => LibraryIdentity.Normalize(title!)).Where(title => title.Length > 0).ToHashSet();
    }

    private static bool SameEpisode(MediaItem item, MediaItem parsed) =>
        parsed.Season is not null && parsed.Episode is not null && item.Season == parsed.Season && item.Episode == parsed.Episode ||
        parsed.AbsoluteEpisode is not null && item.AbsoluteEpisode == parsed.AbsoluteEpisode;
}
