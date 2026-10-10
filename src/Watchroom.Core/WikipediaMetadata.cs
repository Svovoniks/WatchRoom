using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

internal static class WikipediaMetadata
{
    private sealed class Availability { public Dictionary<string, HttpRequestException> Limited { get; } = []; }
    private static readonly ConditionalWeakTable<HttpClient, Availability> Sessions = new();
    private static string Provider(string language) => language == "en" ? "wikipedia" : "wikipedia:" + language;
    private static string Edition(string? provider) => provider == "wikipedia:ru" ? "ru" : "en";
    private static string? Text(JsonElement page, string key) => page.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Title(string title) => Regex.Replace(title, @"\s*\([^)]*\b(?:film|фильм)[^)]*\)\s*$", "", RegexOptions.IgnoreCase);
    private static bool Russian(string title) => Regex.IsMatch(title, @"\p{IsCyrillic}");
    private static string Api(string language) => $"https://{language}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=pageimages%7Ccategories%7Cextracts%7Clanglinks%7Cpageprops&exintro=1&explaintext=1&exlimit=max&piprop=thumbnail&pithumbsize=500&pilicense=any&cllimit=50&lllang={(language == "ru" ? "en" : "ru")}&lllimit=20&";
    private static async Task<JsonDocument> Get(HttpClient http, string language, string query, CancellationToken ct)
    {
        var document = JsonDocument.Parse(await MetadataHttp.GetString(http, Api(language) + query, ct));
        if (document.RootElement.TryGetProperty("error", out var error))
        { var code = Text(error, "code"); document.Dispose(); throw new JsonException("Wikipedia returned an API error: " + code); }
        return document;
    }
    public static int? LeadYear(string? text)
    {
        var match = Regex.Match(text ?? "", @"\b(?:is|was)\s+(?:an?\s+)?((?:19|20)\d{2})\b", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(text ?? "", @"\b((?:19|20)\d{2})\s*(?:года|г\.)", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }
    private static int? Year(JsonElement page)
    {
        if (LeadYear(Text(page, "extract")) is { } lead) return lead;
        var categories = page.TryGetProperty("categories", out var list) ? list.EnumerateArray().Select(c => Text(c, "title") ?? "") : [];
        var years = categories.Select(c => Regex.Match(c, @"(?:Category:((?:19|20)\d{2}) films$|Категория:Фильмы ((?:19|20)\d{2}) года$)", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)).Distinct().ToArray();
        return years.Length == 1 ? years[0] : null;
    }
    public static bool MatchesYear(JsonElement page, MediaItem item) => item.Matched || item.Year is null || Year(page) is { } year && Math.Abs(year - item.Year.Value) <= 1;
    private static IEnumerable<JsonElement> Pages(JsonDocument document) => document.RootElement.TryGetProperty("query", out var query) &&
        query.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array ? pages.EnumerateArray() : [];
    private static MetadataMatch? Candidate(JsonElement page, string language, MediaItem item, IEnumerable<string> redirects)
    {
        var title = Text(page, "title");
        if (title is null || page.TryGetProperty("missing", out _) || !page.TryGetProperty("categories", out var categories)) return null;
        var names = categories.EnumerateArray().Select(c => Text(c, "title") ?? "").ToArray();
        var film = names.Any(c => Regex.IsMatch(c, @"(?i)\bfilms\b|^Категория:(?:Фильмы|Мультфильмы|Аниме-фильмы)"));
        var tv = names.Any(c => Regex.IsMatch(c, @"(?i)television (?:series|shows|characters)|sitcoms|телесериалы|телевизионные сериалы"));
        var year = Year(page);
        var reason = page.TryGetProperty("pageprops", out var props) && props.TryGetProperty("disambiguation", out _) ? "disambiguation" :
            !film || tv ? "not-a-film" : !MatchesYear(page, item) ? "year-mismatch" : null;
        var id = page.TryGetProperty("pageid", out var pageId) && pageId.TryGetInt32(out var number) ? number : 0;
        MetadataDiagnostics.Record("metadata-wikipedia-candidate", new { Provider = Provider(language), Id = id, Title = title, Year = year, Rejection = reason });
        if (reason is not null) return null;
        var aliases = redirects.Select(Title).ToList();
        if (page.TryGetProperty("langlinks", out var links)) aliases.AddRange(links.EnumerateArray().Select(l => Text(l, "title") ?? Text(l, "*")).OfType<string>().Select(Title));
        var overview = Text(page, "extract") ?? "";
        var anime = names.Any(c => Regex.IsMatch(c, @"(?i)anime films|Japanese animated|аниме-фильмы"));
        bool? animated = anime || names.Any(c => Regex.IsMatch(c, @"(?i)animated|мультфильмы")) ? true : null;
        var ids = new Dictionary<string, string>(); if (id > 0) ids[Provider(language)] = id.ToString();
        if (page.TryGetProperty("pageprops", out props) && Text(props, "wikibase_item") is { } wikidata) ids["wikidata"] = wikidata;
        var image = page.TryGetProperty("thumbnail", out var thumbnail) ? Text(thumbnail, "source") : null;
        return new(id, "movie", Title(title), year?.ToString(), image, overview, anime ? "Anime" : "Movie", ProviderIds: ids,
            Animated: animated, Aliases: aliases.Distinct().ToArray(), Provider: Provider(language), Source: $"https://{language}.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')));
    }
    private static MetadataMatch[] Candidates(JsonDocument document, string language, MediaItem item)
    {
        var redirects = new Dictionary<string, List<string>>();
        if (document.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("redirects", out var links))
            foreach (var link in links.EnumerateArray())
                if (Text(link, "to") is { } to && Text(link, "from") is { } from)
                { if (!redirects.TryGetValue(to, out var names)) redirects[to] = names = []; names.Add(from); }
        return Pages(document).Select(p => Candidate(p, language, item, redirects.GetValueOrDefault(Text(p, "title") ?? "") ?? []))
            .OfType<MetadataMatch>().ToArray();
    }
    internal static async Task<List<MetadataMatch>> SearchAsync(HttpClient http, MediaItem item, string query, CancellationToken ct)
    {
        var language = Russian(query) ? "ru" : "en";
        if (Sessions.GetOrCreateValue(http).Limited.TryGetValue(language, out var failure)) throw failure;
        using var document = await Get(http, language, "generator=search&gsrnamespace=0&gsrlimit=8&gsrsearch=" + Uri.EscapeDataString(query), ct);
        return Candidates(document, language, item).ToList();
    }
    private static MetadataMatch? Choose(IEnumerable<MetadataMatch> candidates, MediaItem item, string query) =>
        MetadataMatching.Choose(candidates, item with { Title = query });
    internal static async Task<AutomaticArtwork.Resolved?> MatchAsync(HttpClient http, MediaItem item, IReadOnlyList<string> queries, MetadataOptions options, CancellationToken ct)
    {
        if (item.Episode is not null || item.AbsoluteEpisode is not null || item.SourcePartEpisode is not null || item.Series is not null) return null;
        Exception? failure = null;
        // A known edition-qualified ID is never sent to a different Wikipedia.
        var preferredProvider = Provider(options.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en");
        var known = (item.ProviderIds ?? []).Where(p => p.Key is "wikipedia" or "wikipedia:ru")
            .OrderByDescending(p => p.Key == preferredProvider).FirstOrDefault();
        if (known.Key is null && (item.MetadataProvider is "wikipedia" or "wikipedia:ru") && item.MetadataId is > 0) known = new(item.MetadataProvider, item.MetadataId.ToString()!);
        if (int.TryParse(known.Value, out var knownId) && knownId > 0)
        {
            using var page = await Get(http, Edition(known.Key), "pageids=" + knownId, ct);
            if (Candidates(page, Edition(known.Key), item) is [var match]) return await Resolve(http, match, options, ct);
        }
        foreach (var query in queries.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(Russian).Take(8))
        {
            var language = Russian(query) ? "ru" : "en";
            if (Sessions.GetOrCreateValue(http).Limited.TryGetValue(language, out var limited)) { failure = limited; continue; }
            try
            {
                var titles = language == "ru" ? new[] { item.Year is { } y ? $"{query} (фильм, {y})" : query, $"{query} (фильм)", query } :
                    new[] { item.Year is { } enYear ? $"{query} ({enYear} film)" : query, $"{query} (film)", query };
                using var exact = await Get(http, language, "titles=" + Uri.EscapeDataString(string.Join('|', titles.Distinct())), ct);
                var exactCandidates = Candidates(exact, language, item);
                if (item.Year is not null && Choose(exactCandidates, item, query) is { } direct) return await Resolve(http, direct, options, ct);
                var candidates = await SearchAsync(http, item, query, ct);
                if (Choose(exactCandidates.Concat(candidates), item, query) is { } searched) return await Resolve(http, searched, options, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            { Sessions.GetOrCreateValue(http).Limited[language] = ex; failure = ex; }
        }
        // Preserve failed discovery when the independent fallback finds nothing.
        if (failure is not null)
        {
            if (await SummaryAsync(http, item, queries.Take(3), ct) is { } summary) return summary;
            throw failure;
        }
        return null;
    }
    internal static async Task<AutomaticArtwork.Resolved> Resolve(HttpClient http, MetadataMatch match, MetadataOptions options, CancellationToken ct)
    {
        var preferred = options.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        var language = Edition(match.Provider);
        if (language != preferred && match.Id > 0)
        {
            try
            {
                using var page = await Get(http, language, "pageids=" + match.Id, ct);
                var current = Pages(page).FirstOrDefault();
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("langlinks", out var links))
                {
                    var link = links.EnumerateArray().FirstOrDefault(l => Text(l, "lang") == preferred);
                    var title = link.ValueKind == JsonValueKind.Object ? Text(link, "title") ?? Text(link, "*") : null;
                    if (title is not null)
                    {
                        using var translated = await Get(http, preferred, "titles=" + Uri.EscapeDataString(title), ct);
                        var item = new MediaItem("language-link", "", match.Title, match.Kind ?? "Movie", int.TryParse(match.Year, out var year) ? year : null);
                        if (Candidates(translated, preferred, item) is [var localized])
                        {
                            var ids = new Dictionary<string, string>(match.ProviderIds ?? []);
                            foreach (var id in localized.ProviderIds ?? []) ids[id.Key] = id.Value;
                            match = localized with { ProviderIds = ids, OriginalTitle = match.Title, Aliases = [match.Title, .. match.Aliases ?? []] };
                        }
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            { MetadataDiagnostics.Failure("metadata-language-link", match.Title, match.Provider, ex); }
        }
        return FromMatch(match);
    }
    internal static AutomaticArtwork.Resolved FromMatch(MetadataMatch match) => new(match.Poster, match.Source ?? "", match.Overview,
        match.Kind ?? "Movie", match.Type, match.Provider, match.Id > 0 ? match.Id : null, match.Title, match,
        Year: int.TryParse(match.Year, out var year) ? year : null, Animated: match.Animated);
    public static async Task<AutomaticArtwork.Resolved?> SummaryAsync(HttpClient http, MediaItem item, IEnumerable<string> queries, CancellationToken ct)
    {
        var candidates = new Dictionary<int, MetadataMatch>();
        foreach (var query in queries.Where(q => !Russian(q)))
        {
            var title = Regex.Replace(query, @"\b(?:Of|The|And|In|From|To|At|On|A|An)\b", m => m.Index == 0 ? m.Value : m.Value.ToLowerInvariant());
            var titles = item.Year is { } year ? new[] { $"{title} ({year} film)", $"{title} (film)", title } : new[] { $"{title} (film)", title };
            foreach (var pageTitle in titles.Distinct())
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(await MetadataHttp.GetString(http, "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_')), ct)); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { continue; }
                using var json = document; var page = json.RootElement;
                var canonical = Text(page, "title"); var description = Text(page, "description") ?? "";
                if (Text(page, "type") == "disambiguation" || canonical is null || !Regex.IsMatch(description, @"(?i)\b(?:film|movie)\b") ||
                    Regex.IsMatch(description, @"(?i)\b(?:series|franchise|soundtrack|novel|film director|actor)\b")) continue;
                var date = Regex.Match(description, @"\b(?:19|20)\d{2}\b");
                if (item.Year is { } expected && (!date.Success || Math.Abs(int.Parse(date.Value) - expected) > 1)) continue;
                if (!page.TryGetProperty("pageid", out var id) || !id.TryGetInt32(out var pageId)) continue;
                var image = page.TryGetProperty("thumbnail", out var thumbnail) ? Text(thumbnail, "source") : null;
                candidates[pageId] = new(pageId, "movie", Title(canonical), date.Success ? date.Value : null, image, Text(page, "extract") ?? "",
                    Regex.IsMatch(description, @"(?i)anime|Japanese[^.]{0,60}animated") ? "Anime" : "Movie", ProviderIds: new() { ["wikipedia"] = pageId.ToString() },
                    Provider: "wikipedia", Source: "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(canonical.Replace(' ', '_')));
            }
            if (Choose(candidates.Values, item, query) is { } chosen) return FromMatch(chosen);
        }
        return null;
    }
}
