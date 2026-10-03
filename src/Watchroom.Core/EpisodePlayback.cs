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
        return LibraryCatalog.PreferredFiles(library.Where(item => LibraryIdentity.SameShow(item, current) &&
                !item.IsExtra && !item.IsVirtual && item.Available && item.NumberingConflict is null &&
                item.Season is not null && item.Episode is not null &&
                (item.Season > current.Season || item.Season == current.Season && item.Episode > lastEpisode) && canPlay(item)))
            .OrderBy(item => item.Season).ThenBy(item => item.Episode).ThenBy(item => item.Path)
            .FirstOrDefault();
    }
}
