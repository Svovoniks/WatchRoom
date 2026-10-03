namespace Watchroom.Core;

public static class LibraryWatchProgress
{
    public static bool IsWatched(IEnumerable<MediaItem> source, LibraryCard card, Func<string, bool> watched)
    {
        if (card.Media.Series is null) return watched(card.Media.Id);
        var episodes = source.Where(item => LibraryIdentity.SameShow(item, card.Media) && !item.IsExtra && !item.IsVirtual).ToArray();
        static IEnumerable<string> Keys(MediaItem item) => item.Season is not null && item.Episode is not null && item.NumberingConflict is null
            ? LibraryIdentity.EpisodeNumbers(item).Select(number => $"{item.Season}:{number}") : ["file:" + item.Id];
        var completed = episodes.Where(item => watched(item.Id)).SelectMany(Keys).ToHashSet();
        var required = card.Level switch
        {
            "series" => episodes,
            "season" => episodes.Where(item => (item.Season ?? -1) == card.Season).ToArray(),
            _ => card.Media.IsVirtual || card.Media.IsExtra ? [] : new[] { card.Media }
        };
        return required.Length > 0 && required.SelectMany(Keys).All(completed.Contains);
    }
}
