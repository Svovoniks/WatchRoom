namespace BlackPearl.Core;

public static class EpisodePlayback
{
    public static bool NearEnd(long positionMs, long durationMs) => durationMs > 0 && positionMs >= 0 &&
        durationMs - positionMs <= Math.Min(90000, durationMs / 10);

    // The release being watched carries on: after a black-and-white episode comes the next black-and-white one.
    public static string? Version(IEnumerable<MediaItem> library, MediaItem current, string? fallback = null) =>
        LibraryCatalog.VersionLabel(current, LibraryCatalog.Versions(library, current)) is { Length: > 0 } label ? label : fallback;

    public static MediaItem? Next(IEnumerable<MediaItem> library, MediaItem current, bool hasQueue,
        Func<MediaItem, bool> canPlay, string? preferVersion = null)
    {
        if (hasQueue || current.Series is null || current.Season is null || current.Episode is null ||
            current.NumberingConflict is not null) return null;
        var all = library as IReadOnlyCollection<MediaItem> ?? library.ToArray();
        // An album plays its tracks in disc, number and file order, so tracks that share a number all play.
        if (current.IsMusic)
            return all.Where(item => LibraryIdentity.SameShow(item, current) && !item.IsExtra && !item.IsVirtual && (item.Id == current.Id || item.Available))
                .OrderBy(item => item.Season ?? int.MaxValue).ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .SkipWhile(item => item.Id != current.Id).Skip(1).FirstOrDefault(canPlay);
        preferVersion = Version(all, current, preferVersion);
        var lastEpisode = current.EpisodeEnd ?? current.Episode.Value;
        var candidates = all.Where(item => LibraryIdentity.SameShow(item, current) &&
                !item.IsExtra && !item.IsVirtual && item.Available && item.NumberingConflict is null &&
                item.Season is not null && item.Episode is not null &&
                (item.Season > current.Season || item.Season == current.Season && item.Episode > lastEpisode))
            .GroupBy(item => (item.Season, item.Episode)).OrderBy(slot => slot.Key.Season).ThenBy(slot => slot.Key.Episode);
        // canPlay can touch the disk, so check episodes in order and stop at the first slot that has a playable file.
        // A file only competes with files that start at or before it, so the files seen so far decide each slot.
        var playable = new List<MediaItem>();
        foreach (var slot in candidates)
        {
            var found = slot.Where(canPlay).ToArray();
            if (found.Length == 0) continue;
            playable.AddRange(found);
            if (LibraryCatalog.PreferredFiles(playable, preferVersion).Where(item => item.Season == slot.Key.Season && item.Episode == slot.Key.Episode)
                .FirstOrDefault() is { } next) return next;
        }
        return null;
    }

    // The show's playable episodes in order; the current episode is always included.
    public static MediaItem[] Episodes(IEnumerable<MediaItem> library, MediaItem current, string? preferVersion = null)
    {
        if (current.Series is null || current.Season is null || current.Episode is null) return [];
        var all = library as IReadOnlyCollection<MediaItem> ?? library.ToArray();
        return LibraryCatalog.PreferredFiles(all.Where(item => LibraryIdentity.SameShow(item, current) &&
                !item.IsExtra && !item.IsVirtual && item.Season is not null && item.Episode is not null &&
                (item.Id == current.Id || item.Available && item.NumberingConflict is null))
            .OrderBy(item => item.Id != current.Id), Version(all, current, preferVersion))
            .OrderBy(item => item.Season).ThenBy(item => item.Episode).ThenBy(item => item.Path)
            .ToArray();
    }
}
