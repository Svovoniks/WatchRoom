using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// A file that names its own episode says which episode it is, whatever its number. Those titles choose between a provider's
// numbering orders (aired, DVD, the parts a streaming service released) and tell when a match can't be the show the files are.
internal static class EpisodeOrder
{
    public const string GroupPrefix = "group:";
    // Fewer titles than this can't outvote a provider's own numbering.
    public const int MinimumEvidence = 3;

    internal sealed record Evidence(MediaItem Item, int Season, int Episode, string Title);
    internal sealed record Choice(string Order, MetadataSeason[] Seasons, int Agree, int Compared);

    // An order a provider defines beyond its aired list, which numbers the files differently from aired.
    public static bool ProviderDefined(string? order) => order == "dvd" || order?.StartsWith(GroupPrefix, StringComparison.Ordinal) == true;
    public static string? Group(string? order) => order?.StartsWith(GroupPrefix, StringComparison.Ordinal) == true ? order[GroupPrefix.Length..] : null;

    // The title the file itself gives, before any provider replaced it: a provider's title is the one retention tracks.
    public static string? OwnTitle(MediaItem item)
    {
        if (item.TmdbFields?.TryGetValue("episodeTitle", out var tracked) == true)
        {
            try { return JsonSerializer.Deserialize<string?>(tracked.Original); }
            catch (JsonException) { return null; }
        }
        // Another provider's titles aren't tracked, so a title it looked up can't be told from the file's.
        if (item.EpisodeSource is { } source && !source.Contains("themoviedb.org", StringComparison.OrdinalIgnoreCase)) return null;
        return string.IsNullOrWhiteSpace(item.EpisodeTitle) ? null : item.EpisodeTitle;
    }

    // Only numbering read from the files can be checked against titles. Running numbers, dates and parts map to a provider's list another way.
    public static Evidence[] Collect(IEnumerable<MediaItem> entries) => entries
        .Where(x => !x.IsBonus && !x.IsVirtual && x.Season is > 0 && x.Episode is not null && x.AbsoluteEpisode is null && x.SourcePart is null && x.NumberingSource != "date")
        .Select(x => (Item: x, Title: OwnTitle(x)))
        .Where(x => x.Title is not null && Words(x.Title).Length > 0 && !Generic.IsMatch(x.Title))
        .Select(x => new Evidence(x.Item, x.Item.Season!.Value, x.Item.Episode!.Value, x.Title!)).ToArray();

    public static bool CanChoose(IReadOnlyCollection<MediaItem> entries) => entries.Count > 0 &&
        entries.All(x => x.NumberingOrder == "aired" || Group(x.NumberingOrder) is not null) &&
        !entries.Any(x => LibraryIdentity.Locked(x, "NumberingOrder") || x.AbsoluteEpisode is not null || x.SourcePart is not null || x.NumberingSource == "date") &&
        !HoldsCopies(entries);
    // A show combined from copies of a season keeps one order for all of them. One copy's titles can't speak for another copy's numbers,
    // so the order stays as it was when they combined.
    public static bool HoldsCopies(IEnumerable<MediaItem> entries) => entries.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus && x.Season is > 0)
        .GroupBy(x => x.Season).Any(season => season.Select(x => x.SeriesPath ?? Path.GetDirectoryName(x.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
    // Which titles were weighed, so an order weighed before more titled files arrived isn't taken for theirs.
    public static string Weighed(string order, IEnumerable<Evidence> evidence) => order + "|" + LibraryIdentity.Hash(string.Join("\n",
        evidence.Select(x => $"{x.Season}:{x.Episode}:{MetadataTitles.Normalize(x.Title)}").Order(StringComparer.Ordinal)));

    // How many titled files the list puts on an episode of the same name, out of those it gives a comparable title for.
    public static (int Agree, int Compared) Agreement(IReadOnlyCollection<Evidence> evidence, MetadataSeason[] seasons)
    {
        var listing = Listing(seasons); var words = new Dictionary<string, Title>();
        int agree = 0, compared = 0;
        foreach (var file in evidence)
        {
            if (!listing.TryGetValue((file.Season, file.Episode), out var titles)) continue;
            var own = Read(words, file.Title);
            var comparable = titles.Select(t => Read(words, t)).Where(t => t.Script is not null && t.Script == own.Script).ToArray();
            if (comparable.Length == 0) continue;
            compared++;
            if (comparable.Any(t => Same(own.Words, t.Words))) agree++;
        }
        return (agree, compared);
    }

    // Of the titled files the current list has an episode for, how many name an episode anywhere in these lists, out of those any
    // list has a comparable title for. Files past the end of the list are left out: a season the provider hasn't filled in yet says nothing.
    public static (int Found, int Compared) Presence(IReadOnlyCollection<Evidence> evidence, MetadataSeason[] current, IEnumerable<MetadataSeason[]> lists)
    {
        var listed = current.SelectMany(s => s.Episodes.Select(e => (s.Number, e.Number))).ToHashSet();
        var words = new Dictionary<string, Title>();
        var titles = lists.Append(current).SelectMany(seasons => seasons.SelectMany(s => s.Episodes)).Select(e => e.Title).OfType<string>().Where(t => !Generic.IsMatch(t)).Distinct()
            .Select(t => Read(words, t)).Where(t => t.Script is not null).ToArray();
        int found = 0, compared = 0;
        foreach (var file in evidence.Where(x => listed.Contains((x.Season, x.Episode))))
        {
            var own = Read(words, file.Title);
            var comparable = titles.Where(t => t.Script == own.Script).ToArray();
            if (comparable.Length == 0) continue;
            compared++;
            if (comparable.Any(t => Same(own.Words, t.Words))) found++;
        }
        return (found, compared);
    }

    // Files numbered past the end of a season the list has episodes for. A season it doesn't list yet isn't counted against it.
    public static (int Misfits, int Regular) Fit(IEnumerable<MediaItem> entries, MetadataSeason[] seasons)
    {
        var listed = seasons.Where(s => s.Episodes.Length > 0).ToDictionary(s => s.Number, s => s.Episodes.Select(e => e.Number).ToHashSet());
        var regular = entries.Where(x => !x.IsBonus && !x.IsVirtual && x.Season is > 0 && x.Episode is not null && x.AbsoluteEpisode is null && listed.ContainsKey(x.Season!.Value)).ToArray();
        return (regular.Count(x => LibraryIdentity.EpisodeNumbers(x).Any(n => !listed[x.Season!.Value].Contains(n))), regular.Length);
    }

    // The current list fits when nearly every titled file sits on its own episode.
    public static bool Fits((int Agree, int Compared) agreement) => agreement.Compared < MinimumEvidence || agreement.Agree * 10 >= agreement.Compared * 9;

    // Another list replaces the current one only when nearly every titled file agrees with it and clearly more than with the current list.
    public static Choice? Best(IReadOnlyCollection<Evidence> evidence, (int Agree, int Compared) current, IEnumerable<(string Order, MetadataSeason[] Seasons)> candidates)
    {
        Choice? best = null;
        foreach (var (order, seasons) in candidates)
        {
            if (seasons.Length == 0 || seasons.Select(s => s.Number).Distinct().Count() != seasons.Length) continue;
            var (agree, compared) = Agreement(evidence, seasons);
            if (compared < MinimumEvidence || agree * 10 < compared * 9) continue;
            if (agree < current.Agree + Math.Max(2, (current.Compared + 9) / 10)) continue;
            if (best is null || agree > best.Agree) best = new(order, seasons, agree, compared);
        }
        return best;
    }

    // A match the files contradict twice over: their titles are nowhere in the show, and their numbers don't fit its episodes.
    // Either alone happens to the right show: titles in another language, or numbering the provider lists another way.
    public static bool Contradicts((int Found, int Compared) presence, (int Misfits, int Regular) fit) =>
        presence.Compared >= 5 && presence.Found * 5 <= presence.Compared && fit.Misfits >= 3 && fit.Misfits * 10 >= fit.Regular * 3;

    private static readonly Regex Generic = new(@"(?i)^\s*(?:episode|ep\.?|серия|эпизод|chapter|part|第)\s*\d+\s*(?:話|集)?\s*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Minor = ["the", "a", "an", "and", "of", "to", "in", "on", "part", "pt", "le", "la", "les", "der", "die", "das"];
    private static readonly Dictionary<string, string> Numerals = new() { ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["one"] = "1", ["two"] = "2", ["three"] = "3" };

    private static Dictionary<(int, int), string[]> Listing(MetadataSeason[] seasons) => seasons
        .SelectMany(s => s.Episodes.Select(e => (Key: (s.Number, e.Number), e.Title)))
        .Where(x => x.Title is not null && !Generic.IsMatch(x.Title))
        .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Select(x => x.Title!).ToArray());

    public static string[] Words(string title)
    {
        var plain = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plain.Append(char.ToLowerInvariant(c));
        return Regex.Split(plain.ToString().Replace('ё', 'е'), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0 && !Minor.Contains(w))
            .Select(w => Numerals.GetValueOrDefault(w, w)).ToArray();
    }

    // Titles in different scripts can't be compared: a Russian file name against an English episode list says nothing.
    private static string? Script(string text)
    {
        int latin = 0, cyrillic = 0, cjk = 0;
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'À' and <= 'ɏ') latin++;
            else if (c is >= 'Ѐ' and <= 'ӿ') cyrillic++;
            else if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯') cjk++;
        }
        var most = Math.Max(latin, Math.Max(cyrillic, cjk));
        return most == 0 ? null : most == latin ? "latin" : most == cyrillic ? "cyrillic" : "cjk";
    }

    private sealed record Title(string[] Words, string? Script);
    private static Title Read(Dictionary<string, Title> cache, string text)
    {
        if (!cache.TryGetValue(text, out var title)) cache[text] = title = new(Words(text), Script(text));
        return title;
    }
    public static bool Same(string left, string right) => left.Length > 0 && right.Length > 0 && Same(Words(left), Words(right));
    private static bool Same(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (string.Concat(a) == string.Concat(b)) return true;
        // One word on either side must be that same word: "Pink Pajamas" isn't "Pinky in Paradise".
        if (a.Length == 1 || b.Length == 1) return false;
        var shared = a.Intersect(b).Count();
        return shared * 10 >= Math.Max(a.Distinct().Count(), b.Distinct().Count()) * 6;
    }
}

// Matches the files contradicted, per show, so looking the show up again doesn't land on the same entry.
// Keys are provider:type:id. A match someone chooses by hand never goes through here.
internal static class MetadataRejections
{
    private static string Setting(MediaItem item) => "metadata-rejected:" + LibraryIdentity.ShowKey(item);
    public static string Key(string provider, string type, int id) => $"{provider}:{type}:{id}";
    public static IReadOnlySet<string> For(LibraryStore library, MediaItem item)
    {
        try { return library.Setting(Setting(item)) is { } json ? (Wire.Read<string[]>(json) ?? []).ToHashSet() : new HashSet<string>(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return new HashSet<string>(); }
    }
    public static void Add(LibraryStore library, MediaItem item, string key)
    {
        var known = For(library, item).Append(key).Distinct().Order().ToArray();
        library.Setting(Setting(item), Wire.Serialize(known));
    }
}

// Which provider entries the library already matched other shows of a name to. Copies of one show in several folders tie on the
// same same-named entries, and the one an earlier copy settled on is the one the rest are.
internal sealed class KnownShows
{
    private readonly Dictionary<string, HashSet<(string Show, string Key)>> names = new();
    public KnownShows(IEnumerable<MediaItem> catalog)
    {
        foreach (var item in catalog)
        {
            if (item.IsVirtual || item.Series is null || item.MetadataType != "tv" || item.MetadataProvider is not ("tmdb" or "tvmaze") || item.MetadataId is not > 0) continue;
            var entry = (LibraryIdentity.ShowKey(item), MetadataRejections.Key(item.MetadataProvider, "tv", item.MetadataId.Value));
            foreach (var name in new[] { item.Series, Original(item) }.OfType<string>().Select(x => MetadataTitles.Normalize(x)).Where(x => x.Length > 0).Distinct())
            {
                if (!names.TryGetValue(name, out var set)) names[name] = set = [];
                set.Add(entry);
            }
        }
    }
    public IReadOnlySet<string> For(MediaItem item)
    {
        var show = LibraryIdentity.ShowKey(item);
        return names.TryGetValue(MetadataTitles.Normalize(item.Series ?? item.Title), out var set)
            ? set.Where(x => x.Show != show).Select(x => x.Key).ToHashSet() : new HashSet<string>();
    }
    // The show name the scan gave a file, before a provider's title replaced it.
    private static string? Original(MediaItem item)
    {
        if (item.TmdbFields?.TryGetValue("series", out var tracked) != true) return null;
        try { return JsonSerializer.Deserialize<string?>(tracked!.Original); }
        catch (JsonException) { return null; }
    }
}
