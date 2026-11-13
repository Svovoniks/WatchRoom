using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

public record LibraryShow(string Id, string Title, string Kind, int? Year, string? Poster, string? Overview,
    Dictionary<string, string> ProviderIds, string NumberingOrder, Dictionary<string, TmdbField>? TmdbFields = null);
public record LibrarySeason(string Id, string ShowId, int? Number, string? Title, string? Overview, string? Poster, Dictionary<string, TmdbField>? TmdbFields = null);

public static class LibraryIdentity
{
    public static string Normalize(string value) => Regex.Replace(value.Normalize(), @"[^\p{L}\p{N}\p{M}]", "").ToLowerInvariant();
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];
    public static string ShowKey(MediaItem item)
    {
        if (item.Series is null) return item.Id;
        if (item.ShowId is not null) return item.ShowId;
        foreach (var provider in new[] { "tmdb", "tvdb", "imdb", "tvmaze" })
            if (ProviderId(item, provider) is { } id) return "show-" + Hash(provider + ":" + id);
        return "show-" + Hash($"{Normalize(item.Series)}:{item.Year}");
    }
    // The card a file lists under: its show's, or its film's. Copies of one identified film, such as a 1080p and a 2160p
    // release, share a card as versions; matching and metadata still treat each file on its own, keyed by ShowKey. Parts of
    // a film numbered in their file names, as "Film-1" and "Film-2", are different videos and keep cards of their own.
    public static string TitleKey(MediaItem item)
    {
        if (item.Series is not null) return ShowKey(item);
        if (FilmIdentity(item) is not { } identity) return item.Id;
        // Records never change, so a film's key, which hashes and reads its file name, is worked out once per record.
        return filmKeys.GetValue(item, film => new("film-" + Hash($"{identity}:{FilmPart(film) ?? 0}"))).Value;
    }
    private sealed record FilmKey(string Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MediaItem, FilmKey> filmKeys = new();
    // The provider identity a film's copies share, before parts are told apart; null for anything else.
    internal static string? FilmIdentity(MediaItem item) =>
        item.Series is not null || item.IsVirtual || item.IsMusic || item.IsExtra || item.IsBonus || item.ExtraOf is not null ? null
        : ProviderId(item, "tmdb") is { } tmdb ? $"tmdb:{item.MetadataType ?? "movie"}:{tmdb}"
        : ProviderId(item, "imdb") is { } imdb ? "imdb:" + imdb : null;
    public static bool SameFilm(MediaItem left, MediaItem right) => left.Series is null && right.Series is null && TitleKey(left) == TitleKey(right);
    // The part a film's file name numbers, as "Part 2", "CD1" or a trailing "-2". A number its provider title ends with
    // too, as in Apollo 13, names the film rather than a part.
    public static int? FilmPart(MediaItem item)
    {
        var name = Path.GetFileNameWithoutExtension(item.Path);
        var match = Regex.Match(name, @"(?i)(?<![\p{L}\p{N}])(?:part|pt|cd|dis[ck]|часть|серия)[\s._-]*(\d{1,2})(?!\p{N})");
        // Audio channels such as "DTS 5.1" end a name with a number too.
        if (!match.Success) match = Regex.Match(name, @"(?<=^|[\s._-])(?<!\p{N}[._])(\d{1,2})$");
        if (!match.Success || int.Parse(match.Groups[1].Value) is not (> 0 and <= 12 and var part)) return null;
        return new[] { item.ProviderTitle, item.OriginalTitle }.Any(title => title is not null &&
            Regex.IsMatch(title, $@"(?<!\p{{N}}){part}\s*$")) ? null : part;
    }
    public static bool SameShow(MediaItem left, MediaItem right) => left.Series is not null && right.Series is not null && ShowKey(left) == ShowKey(right);
    public static bool InShow(MediaItem item, string? identity, string? kind = null) => item.Series is not null &&
        (ShowKey(item) == identity || string.Equals(item.Series, identity, StringComparison.OrdinalIgnoreCase) && (kind is null || item.Kind == kind));
    public static MediaItem Ensure(MediaItem item)
    {
        if (item.Series is null) return item with { ShowId = null, SeasonId = null };
        var showId = ShowKey(item);
        return item with { ShowId = showId, SeasonId = "season-" + Hash($"{showId}:{item.Season?.ToString() ?? "unknown"}") };
    }
    public static string? ProviderId(MediaItem item, string provider) => item.ProviderIds?.FirstOrDefault(p => p.Key.Equals(provider, StringComparison.OrdinalIgnoreCase)).Value
        ?? (item.MetadataProvider == provider ? item.MetadataId?.ToString() : null);
    public static MediaItem WithProvider(MediaItem item, string provider, int? id, bool primary = true)
    {
        var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (item.MetadataProvider is { } previous && item.MetadataId is > 0) ids[previous] = item.MetadataId.Value.ToString();
        if (id is > 0) ids[provider] = id.Value.ToString();
        // A fallback enriches the item without changing a manually chosen identity.
        return item with { ProviderIds = ids, MetadataProvider = primary || item.MetadataProvider is null ? provider : item.MetadataProvider,
            MetadataId = primary || item.MetadataProvider is null ? id : item.MetadataId };
    }
    public static bool Locked(MediaItem item, string field) => item.MetadataLocked ||
        (item.LockedFields ?? []).Concat(item.LocalMetadataFields ?? []).Contains(field, StringComparer.OrdinalIgnoreCase);
    public static IEnumerable<int> EpisodeNumbers(MediaItem item) => item.Episode is { } start
        ? Enumerable.Range(start, Math.Clamp((item.EpisodeEnd ?? start) - start + 1, 1, 100)) : [];
    public static int EpisodeCount(IEnumerable<MediaItem> items) => items.Where(x => !x.IsBonus).SelectMany(item => item.Season is null || item.NumberingConflict is not null
        ? ["file:" + item.Id] : EpisodeNumbers(item).Select(n => $"{item.Season}:{n}").DefaultIfEmpty("file:" + item.Id)).Distinct().Count();
}
