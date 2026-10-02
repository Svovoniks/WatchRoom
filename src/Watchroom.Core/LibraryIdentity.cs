using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public record LibraryShow(string Id, string Title, string Kind, int? Year, string? Poster, string? Overview,
    Dictionary<string, string> ProviderIds, string NumberingOrder);
public record LibrarySeason(string Id, string ShowId, int? Number, string? Title, string? Overview, string? Poster);

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
    public static int EpisodeCount(IEnumerable<MediaItem> items) => items.SelectMany(item => item.Season is null || item.NumberingConflict is not null
        ? ["file:" + item.Id] : EpisodeNumbers(item).Select(n => $"{item.Season}:{n}").DefaultIfEmpty("file:" + item.Id)).Distinct().Count();
}
