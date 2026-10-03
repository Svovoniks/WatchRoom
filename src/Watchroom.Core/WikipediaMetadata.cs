using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

internal static class WikipediaMetadata
{
    private sealed class Availability { public bool ActionLimited; }
    private static readonly ConditionalWeakTable<HttpClient, Availability> Sessions = new();
    public static bool ActionAvailable(HttpClient http) => !Sessions.GetOrCreateValue(http).ActionLimited;
    public static void LimitAction(HttpClient http) => Sessions.GetOrCreateValue(http).ActionLimited = true;
    public static int? LeadYear(string? text)
    {
        var match = Regex.Match(text ?? "", @"\b(?:is|was)\s+(?:an?\s+)?((?:19|20)\d{2})\b", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
    public static bool MatchesYear(JsonElement page, MediaItem item)
    {
        if (item.Matched || item.Year is null) return true;
        var extract = page.TryGetProperty("extract", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
        if (LeadYear(extract) is { } year) return Math.Abs(year - item.Year.Value) <= 1;
        var years = page.GetProperty("categories").EnumerateArray().Select(c => Regex.Match(c.GetProperty("title").GetString() ?? "", @"Category:((?:19|20)\d{2}) films$")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        return years.Length > 0 && years.Any(y => Math.Abs(y - item.Year.Value) <= 1);
    }

    public static async Task<AutomaticArtwork.Resolved?> SummaryAsync(HttpClient http, MediaItem item, IEnumerable<string> queries, CancellationToken ct)
    {
        var candidates = new Dictionary<int, AutomaticArtwork.Resolved>();
        foreach (var query in queries)
        {
            var title = Regex.Replace(query, @"\b(?:Of|The|And|In|From|To|At|On|A|An)\b", m => m.Index == 0 ? m.Value : m.Value.ToLowerInvariant());
            var titles = item.Year is { } year ? new[] { $"{title} ({year} film)", $"{title} (film)", title } : new[] { $"{title} (film)", title };
            foreach (var pageTitle in titles.Distinct())
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(await MetadataHttp.GetString(http, "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_')).Replace("%28", "(").Replace("%29", ")"), ct)); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { continue; }
                using var json = document;
                var page = json.RootElement;
                string? Text(string key) => page.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var canonical = Text("title"); var description = Text("description") ?? "";
                if (Text("type") == "disambiguation" || canonical is null || !Regex.IsMatch(description, @"(?i)\b(?:film|movie)\b") ||
                    Regex.IsMatch(description, @"(?i)\b(?:series|franchise|soundtrack|novel|film director|actor)\b")) continue;
                var canonicalTitle = Regex.Replace(canonical, @"\s*\([^)]*film\)\s*$", "", RegexOptions.IgnoreCase);
                if (MetadataTitles.Normalize(canonicalTitle) != MetadataTitles.Normalize(query)) continue;
                var date = Regex.Match(description, @"\b(?:19|20)\d{2}\b");
                if (item.Year is { } expected && (!date.Success || Math.Abs(int.Parse(date.Value) - expected) > 1)) continue;
                if (!page.TryGetProperty("pageid", out var id) || !id.TryGetInt32(out var pageId)) continue;
                var image = page.TryGetProperty("thumbnail", out var thumbnail) && thumbnail.TryGetProperty("source", out var source) ? source.GetString() : null;
                var kind = Regex.IsMatch(description + " " + Text("extract"), @"(?i)\b(?:anime|Japanese[^.]{0,60}animated)\b") ? "Anime" : "Movie";
                candidates[pageId] = new(image, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(canonical.Replace(' ', '_')),
                    Text("extract"), kind, "movie", "wikipedia", pageId, canonicalTitle);
                // A year-qualified page plus film description is sufficient; no search-rank guessing.
                if (item.Year is not null) return candidates[pageId];
            }
            if (candidates.Count > 0) break;
        }
        return candidates.Count == 1 ? candidates.Values.Single() : null;
    }
}
