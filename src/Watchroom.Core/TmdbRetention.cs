using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchroom.Core;

public record TmdbField(string Value, string Original, long FetchedAt);

// An early cutoff leaves room for maintenance intervals before the six-month
// contractual limit. A lock does not turn downloaded content into local data.
public static class TmdbRetention
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(175);
    internal static readonly string[] TitleFields = ["title", "series", "kind", "year", "overview", "originalTitle", "genres", "cast", "rating", "metadataKind", "metadataType", "metadataProvider", "metadataId"];
    internal static readonly string[] EpisodeFields = ["season", "episode", "episodeEnd", "absoluteEpisode", "absoluteEpisodeEnd", "seasonTitle", "seasonOverview", "episodeTitle", "episodeOverview", "airDate", "runtimeMinutes", "seasonSource", "episodeSource", "numberingSource", "numberingOrder", "airsBeforeSeason", "airsBeforeEpisode", "airsAfterSeason"];
    internal static readonly string[] PosterFields = ["poster", "seriesPoster", "posterSource", "seasonPoster", "episodePoster", "episodePosterQualityPath"];

    internal static MediaItem Record(MediaItem before, MediaItem after, string provider, params string[] names)
    {
        var old = JsonSerializer.SerializeToNode(before, Wire.Json)!.AsObject();
        var updated = JsonSerializer.SerializeToNode(after, Wire.Json)!.AsObject();
        var fields = new Dictionary<string, TmdbField>(after.TmdbFields ?? []);
        foreach (var name in names.Concat(ProviderNames(old, updated)))
        {
            var value = Read(updated, name); var previous = Read(old, name);
            if (value == previous) continue;
            var original = fields.TryGetValue(name, out var prior) && prior.Value == previous ? prior.Original : previous;
            fields.Remove(name);
            if (provider == "tmdb" && value != "null") fields[name] = new(value, original, Wire.Now);
        }
        return after with { TmdbFields = fields.Count == 0 ? null : fields, TmdbRetentionVersion = 1 };
    }

    private static IEnumerable<string> ProviderNames(JsonObject before, JsonObject after) =>
        new[] { before["providerIds"] as JsonObject, after["providerIds"] as JsonObject }.OfType<JsonObject>()
            .SelectMany(x => x.Select(p => "providerIds." + p.Key)).Distinct();
    private static string Read(JsonObject node, string name)
    {
        var parts = name.Split('.', 2);
        return (parts.Length == 1 ? node[parts[0]] : (node[parts[0]] as JsonObject)?[parts[1]])?.ToJsonString() ?? "null";
    }
    private static void Write(JsonObject node, string name, string value)
    {
        var parts = name.Split('.', 2);
        if (parts.Length == 1) node[parts[0]] = JsonNode.Parse(value);
        else
        {
            if (node[parts[0]] is not JsonObject ids) node[parts[0]] = ids = new JsonObject();
            if (value == "null") ids.Remove(parts[1]); else ids[parts[1]] = JsonNode.Parse(value);
        }
    }
    internal static Dictionary<string, TmdbField>? Transfer(Dictionary<string, TmdbField>? fields, params (string From, string To)[] mappings)
    {
        var result = new Dictionary<string, TmdbField>();
        foreach (var (from, to) in mappings)
            if (fields?.TryGetValue(from, out var field) == true) result[to] = field;
        foreach (var field in fields ?? []) if (field.Key.StartsWith("providerIds.", StringComparison.Ordinal)) result[field.Key] = field.Value;
        return result.Count == 0 ? null : result;
    }
    internal static Dictionary<string, TmdbField>? Merge(Dictionary<string, TmdbField>? existing, Dictionary<string, TmdbField>? incoming)
    {
        var result = new Dictionary<string, TmdbField>(existing ?? []);
        foreach (var entry in incoming ?? []) result[entry.Key] = entry.Value;
        return result.Count == 0 ? null : result;
    }
    internal static string Expire(string json, long now, out bool changed)
    {
        var node = JsonNode.Parse(json)!.AsObject(); changed = false;
        var fields = node["tmdbFields"]?.Deserialize<Dictionary<string, TmdbField>>(Wire.Json);
        if (fields is null) return json;
        foreach (var (name, field) in fields.ToArray())
        {
            // User/provider replacements are retained and their old snapshots
            // discarded. Unchanged downloaded values expire even when locked.
            var same = Read(node, name) == field.Value;
            if (same && field.FetchedAt > now - (long)MaximumAge.TotalMilliseconds) continue;
            if (same) Write(node, name, field.Original);
            fields.Remove(name); changed = true;
        }
        if (!changed) return json;
        node["tmdbFields"] = fields.Count == 0 ? null : JsonSerializer.SerializeToNode(fields, Wire.Json);
        if (node.ContainsKey("artworkFetchedAt")) node["artworkFetchedAt"] = 0;
        if (node.ContainsKey("metadataFetchedAt")) node["metadataFetchedAt"] = 0;
        return node.ToJsonString(Wire.Json);
    }

    internal static MediaItem TrackLegacy(MediaItem item)
    {
        if (item.TmdbRetentionVersion > 0) return item;
        var fields = new Dictionary<string, TmdbField>(item.TmdbFields ?? []);
        var parsed = item.IsVirtual ? new MediaItem(item.Id, item.Path, "Unavailable episode", item.Kind) : FileNames.Parse(item.Path, item.SourceLibraryKind ?? "Mixed");
        var baseline = JsonSerializer.SerializeToNode(parsed, Wire.Json)!.AsObject();
        var node = JsonSerializer.SerializeToNode(item, Wire.Json)!.AsObject();
        void Track(IEnumerable<string> names, long date)
        {
            foreach (var name in names)
            {
                var property = char.ToUpperInvariant(name[0]) + name[1..];
                if ((item.LocalMetadataFields ?? []).Contains(property, StringComparer.OrdinalIgnoreCase)) continue;
                var value = Read(node, name);
                if (value != "null" && !fields.ContainsKey(name) && value != Read(baseline, name))
                    fields[name] = new(value, Read(baseline, name), date);
            }
        }
        if (item.MetadataProvider == "tmdb")
            Track(TitleFields.Concat((item.ProviderIds ?? []).Keys.Select(x => "providerIds." + x)), item.ArtworkFetchedAt);
        if (IsTmdb(item.SeasonSource) || IsTmdb(item.EpisodeSource)) Track(EpisodeFields, item.MetadataFetchedAt);
        if (IsTmdb(item.PosterSource)) Track(["poster", "seriesPoster", "posterSource"], item.ArtworkFetchedAt);
        if (IsTmdb(item.SeasonSource)) Track(["seasonPoster"], item.MetadataFetchedAt);
        if (IsTmdb(item.EpisodeSource)) Track(["episodePoster", "episodePosterQualityPath"], item.MetadataFetchedAt);
        return item with { TmdbFields = fields.Count == 0 ? null : fields, TmdbRetentionVersion = 1 };
    }
    internal static bool IsTmdb(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host is "www.themoviedb.org" or "themoviedb.org" or "image.tmdb.org";
}
