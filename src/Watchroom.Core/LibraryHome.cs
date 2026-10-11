namespace Watchroom.Core;

public record HomeLibraryCard(LibraryCard Card, string Section, long PositionMs = 0);

public static class LibraryHome
{
    public static List<HomeLibraryCard> Browse(IEnumerable<MediaItem> source, IReadOnlyDictionary<string, string>? playback = null, int seed = 0)
    {
        playback ??= new Dictionary<string, string>();
        bool Watched(string id) => playback.TryGetValue("watched:" + id, out var value) && value == "true";
        long Position(string id) => playback.TryGetValue("position:" + id, out var value) && long.TryParse(value, out var position) ? Math.Max(0, position) : 0;
        var all = source.Where(item => !item.IsExtra && !item.IsVirtual).ToArray();
        var titles = LibraryCatalog.Browse(all.Where(item => item.Available));
        var groups = all.GroupBy(LibraryIdentity.ShowKey).ToDictionary(group => group.Key, group => group.ToArray());
        var continuing = new List<HomeLibraryCard>();
        var unseen = new List<LibraryCard>();
        foreach (var title in titles)
        {
            var members = groups[LibraryIdentity.ShowKey(title.Media)];
            bool Completed(MediaItem item) => LibraryWatchProgress.IsWatched(members,
                new("", "", null, item, item.Series is null ? "movie" : "episode"), Watched);
            var started = members.Any(item => Watched(item.Id) || Position(item.Id) >= 10000);
            if (!started) { unseen.Add(title); continue; }
            var remaining = LibraryCatalog.PreferredFiles(members.Where(item => item.Available))
                .Where(item => !Completed(item)).OrderBy(item => item.Season ?? int.MaxValue)
                .ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id).ToArray();
            var next = remaining.FirstOrDefault(item => Position(item.Id) >= 10000) ?? remaining.FirstOrDefault();
            if (next is null) continue;
            var card = next.Series is null ? title : new LibraryCard(next.Series + " · " + next.EpisodeDisplayTitle,
                next.Caption, LibraryCatalog.EpisodeArtwork(next), next, "episode", next.Season);
            continuing.Add(new(card, "continue", Position(next.Id)));
        }
        // Mix categories, with one chance per title rather than per episode.
        // Rank stable identities so metadata/title sorting cannot reshuffle a row.
        string Rank(LibraryCard card, string section) => LibraryIdentity.Hash($"{seed}:{section}:{LibraryIdentity.ShowKey(card.Media)}");
        var suggestions = unseen.OrderBy(card => Rank(card, "suggestions"), StringComparer.Ordinal).GroupBy(card => card.Media.Kind)
            .Select(group => new Queue<LibraryCard>(group)).ToArray();
        var result = continuing.Take(8).ToList();
        while (result.Count < Math.Min(8, continuing.Count) + 8 && suggestions.Any(queue => queue.Count > 0))
            foreach (var queue in suggestions.Where(queue => queue.Count > 0))
            {
                if (result.Count >= Math.Min(8, continuing.Count) + 8) break;
                result.Add(new(queue.Dequeue(), "suggestions"));
            }
        if (titles.Count > 0) result.Add(new(titles.MinBy(card => Rank(card, "mystery"), StringComparer.Ordinal)!, "mystery"));
        return result;
    }
}
