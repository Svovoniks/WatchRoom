namespace Watchroom.Core;

public static class EpisodePlayback
{
    public static bool NearEnd(long positionMs, long durationMs) => durationMs > 0 && positionMs >= 0 &&
        durationMs - positionMs <= Math.Min(90000, durationMs / 10);

    public static MediaItem? Next(IEnumerable<MediaItem> library, MediaItem current, bool hasQueue,
        Func<MediaItem, bool> canPlay)
    {
        if (hasQueue || current.Series is null || current.Season is null || current.Episode is null ||
            current.NumberingConflict is not null) return null;
        var lastEpisode = current.EpisodeEnd ?? current.Episode.Value;
        var candidates = library.Where(item => LibraryIdentity.SameShow(item, current) &&
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
            if (LibraryCatalog.PreferredFiles(playable).Where(item => item.Season == slot.Key.Season && item.Episode == slot.Key.Episode)
                .OrderBy(item => item.Path).FirstOrDefault() is { } next) return next;
        }
        return null;
    }

    // The show's playable episodes in order; the current episode is always included.
    public static MediaItem[] Episodes(IEnumerable<MediaItem> library, MediaItem current) =>
        current.Series is null || current.Season is null || current.Episode is null ? [] :
        LibraryCatalog.PreferredFiles(library.Where(item => LibraryIdentity.SameShow(item, current) &&
                !item.IsExtra && !item.IsVirtual && item.Season is not null && item.Episode is not null &&
                (item.Id == current.Id || item.Available && item.NumberingConflict is null))
            .OrderBy(item => item.Id != current.Id))
            .OrderBy(item => item.Season).ThenBy(item => item.Episode).ThenBy(item => item.Path)
            .ToArray();
}
