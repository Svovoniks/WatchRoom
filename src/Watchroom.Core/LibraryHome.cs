namespace Watchroom.Core;

public record HomeLibraryCard(LibraryCard Card, string Section, long PositionMs = 0);
// An unstarted title with the facts Home uses to describe it and filter mystery draws.
public record HomeTitle(LibraryCard Card, int Episodes, int? Minutes);
public record HomeOverview(List<HomeLibraryCard> Entries, HomeTitle[] Unseen);
public enum MysteryLength { Any, Short, Evening, LongNight }

public static class LibraryHome
{
    public static readonly string[] Kinds = ["Anime", "Show", "Movie"];
    public const int ContinueLimit = 20, SuggestionsPerKind = 7;
    public static List<HomeLibraryCard> Browse(IEnumerable<MediaItem> source, IReadOnlyDictionary<string, string>? playback = null, int seed = 0) =>
        Overview(source, playback, seed).Entries;
    // Row seeds let one category reshuffle its suggestions without moving the others.
    public static HomeOverview Overview(IEnumerable<MediaItem> source, IReadOnlyDictionary<string, string>? playback = null, int seed = 0,
        IReadOnlyDictionary<string, int>? rowSeeds = null)
    {
        playback ??= new Dictionary<string, string>();
        bool Watched(string id) => playback.TryGetValue("watched:" + id, out var value) && value == "true";
        long Position(string id) => playback.TryGetValue("position:" + id, out var value) && long.TryParse(value, out var position) ? Math.Max(0, position) : 0;
        long Played(string id) => playback.TryGetValue("played:" + id, out var value) && long.TryParse(value, out var time) ? time : 0;
        var all = source.Where(item => !item.IsExtra && !item.IsVirtual).ToArray();
        var titles = LibraryCatalog.Browse(all.Where(item => item.Available));
        var groups = all.GroupBy(LibraryIdentity.ShowKey).ToDictionary(group => group.Key, group => group.ToArray());
        var continuing = new List<(HomeLibraryCard Entry, long Played)>();
        var unseen = new List<HomeTitle>();
        foreach (var title in titles)
        {
            var members = groups[LibraryIdentity.ShowKey(title.Media)];
            bool Completed(MediaItem item) => LibraryWatchProgress.IsWatched(members,
                new("", "", null, item, item.Series is null ? "movie" : "episode"), Watched);
            var started = members.Any(item => Watched(item.Id) || Position(item.Id) >= 10000);
            var files = LibraryCatalog.PreferredFiles(members.Where(item => item.Available)).ToArray();
            if (!started)
            {
                unseen.Add(new(title, title.Level == "series" ? files.Length : 0,
                    files.Select(item => item.RuntimeMinutes).FirstOrDefault(minutes => minutes > 0)));
                continue;
            }
            var remaining = files.Where(item => !Completed(item)).OrderBy(item => item.Season ?? int.MaxValue)
                .ThenBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Id).ToArray();
            var next = remaining.FirstOrDefault(item => Position(item.Id) >= 10000) ?? remaining.FirstOrDefault();
            if (next is null) continue;
            var card = next.Series is null ? title : new LibraryCard(next.Series + " · " + next.EpisodeDisplayTitle,
                next.Caption, LibraryCatalog.EpisodeArtwork(next), next, "episode", next.Season);
            continuing.Add((new(card, "continue", Position(next.Id)), members.Max(item => Played(item.Id))));
        }
        // Most recently played first; titles without a play time keep catalog order.
        var result = continuing.OrderByDescending(entry => entry.Played).Select(entry => entry.Entry).Take(ContinueLimit).ToList();
        // Rank stable identities so metadata/title sorting cannot reshuffle a row.
        string Rank(LibraryCard card, string section, int rankSeed) => LibraryIdentity.Hash($"{rankSeed}:{section}:{LibraryIdentity.ShowKey(card.Media)}");
        foreach (var kind in Kinds)
        {
            var rowSeed = rowSeeds?.GetValueOrDefault(kind, seed) ?? seed;
            result.AddRange(unseen.Where(title => title.Card.Media.Kind == kind).Select(title => title.Card)
                .OrderBy(card => Rank(card, "suggestions", rowSeed), StringComparer.Ordinal).Take(SuggestionsPerKind)
                .Select(card => new HomeLibraryCard(card, "suggestions")));
        }
        if (titles.Count > 0) result.Add(new(titles.MinBy(card => Rank(card, "mystery", seed), StringComparer.Ordinal)!, "mystery"));
        return new(result, unseen.ToArray());
    }
    public static bool Matches(HomeTitle title, string? kind, MysteryLength length)
    {
        if (kind is not null && title.Card.Media.Kind != kind) return false;
        var series = title.Card.Level == "series";
        return length switch
        {
            MysteryLength.Short => title.Minutes is > 0 and <= 30,
            MysteryLength.Evening => title.Minutes is > 0 and <= 120,
            MysteryLength.LongNight => series ? title.Episodes >= 6 : title.Minutes > 120,
            _ => true
        };
    }
    // Draws one unstarted title, avoiding the current pick when another match exists.
    public static HomeTitle? Draw(IEnumerable<HomeTitle> pool, string? kind, MysteryLength length, int seed, string? avoid = null)
    {
        var matches = pool.Where(title => Matches(title, kind, length)).ToArray();
        var choices = matches.Where(title => LibraryIdentity.ShowKey(title.Card.Media) != avoid).ToArray();
        if (choices.Length == 0) choices = matches;
        return choices.MinBy(title => LibraryIdentity.Hash($"{seed}:draw:{LibraryIdentity.ShowKey(title.Card.Media)}"), StringComparer.Ordinal);
    }
}
