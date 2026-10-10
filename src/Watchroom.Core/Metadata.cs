using System.Net.Http.Headers;
using System.Text.Json;

namespace Watchroom.Core;

public record MetadataMatch(int Id, string Type, string Title, string? Year, string? Poster, string Overview, string? Kind = null,
    string? OriginalTitle = null, Dictionary<string, string>? ProviderIds = null, string[]? Genres = null, string[]? Cast = null, double? Rating = null, bool? Animated = null,
    string[]? Aliases = null)
{
    public override string ToString() => $"{Title} ({Year ?? "Unknown year"}) · {Type}";
}
public record MetadataEpisode(int Number, string? Title, string? Overview, string? Image, string? AirDate, int? Runtime);
public record MetadataSeason(int Number, string? Title, string? Overview, string? Image, string? AirDate, MetadataEpisode[] Episodes);
public sealed class MetadataClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpMessageHandler? imageHandler;
    private readonly MetadataOptions options;
    private readonly string? apiKey;
    public MetadataClient(string token, HttpMessageHandler? handler = null, MetadataOptions? options = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        http.BaseAddress = new Uri("https://api.themoviedb.org/3/"); http.Timeout = TimeSpan.FromSeconds(20);
        token = token.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-fA-F0-9]{32}$")) apiKey = token;
        else if (token.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        imageHandler = handler;
        this.options = options ?? new();
    }
    private async Task<JsonDocument> Get(string path, CancellationToken ct, string? language = null)
    {
        path += (path.Contains('?') ? "&" : "?") + "language=" + Uri.EscapeDataString(language ?? options.Language);
        if (apiKey is not null) path += "&api_key=" + Uri.EscapeDataString(apiKey);
        var body = await MetadataHttp.GetString(http, http.BaseAddress + path, ct);
        return JsonDocument.Parse(body);
    }
    public async Task<MetadataSeason?> SeasonAsync(int showId, int season, CancellationToken ct = default)
    {
        JsonDocument document;
        try { document = await Get($"tv/{showId}/season/{season}", ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        using var json = document;
        var root = json.RootElement;
        string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var episodes = root.GetProperty("episodes").EnumerateArray().Select(e => new MetadataEpisode(e.GetProperty("episode_number").GetInt32(),
            Text(e, "name"), Text(e, "overview"), Text(e, "still_path"), Text(e, "air_date"),
            e.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : null)).ToArray();
        return new(season, Text(root, "name"), Text(root, "overview"), Text(root, "poster_path"), Text(root, "air_date"), episodes);
    }
    public async Task<List<MetadataMatch>> SearchAsync(string title, bool series, CancellationToken ct = default, int? year = null)
    {
        var type = series ? "tv" : "movie";
        using var json = await Get($"search/{type}?query={Uri.EscapeDataString(title)}&include_adult=false&region={Uri.EscapeDataString(options.Country)}" + (year is null ? "" : $"&{(series ? "first_air_date_year" : "year")}={year}"), ct);
        return json.RootElement.GetProperty("results").EnumerateArray().Select(x =>
        {
            var date = x.TryGetProperty(series ? "first_air_date" : "release_date", out var d) ? d.GetString() : null;
            return new MetadataMatch(x.GetProperty("id").GetInt32(), type, x.GetProperty(series ? "name" : "title").GetString()!,
                date?.Length >= 4 ? date[..4] : null, x.TryGetProperty("poster_path", out var p) ? p.GetString() : null,
                x.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : "", MetadataClassification.Tmdb(x, type),
                x.TryGetProperty(series ? "original_name" : "original_title", out var original) ? original.GetString() : null, Animated: MetadataClassification.Animation(x));
        }).ToList();
    }
    public async Task<MetadataMatch> DetailsAsync(MetadataMatch match, CancellationToken ct = default)
    {
        using var json = await Get($"{match.Type}/{match.Id}?append_to_response=keywords,external_ids,credits", ct);
        var root = json.RootElement;
        string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var date = Text(match.Type == "tv" ? "first_air_date" : "release_date");
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["tmdb"] = match.Id.ToString() };
        if (Text("imdb_id") is { } imdb) ids["imdb"] = imdb;
        if (root.TryGetProperty("external_ids", out var external)) foreach (var provider in new[] { "imdb", "tvdb" })
            if (external.TryGetProperty(provider + "_id", out var id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number) ids[provider] = id.ToString();
        var overview = Text("overview");
        if (string.IsNullOrWhiteSpace(overview) && System.Text.RegularExpressions.Regex.IsMatch(Text(match.Type == "tv" ? "original_name" : "original_title") ?? "", @"\p{IsCyrillic}") && options.Language != "ru-RU")
        {
            using var native = await Get($"{match.Type}/{match.Id}", ct, "ru-RU");
            if (native.RootElement.TryGetProperty("overview", out var nativeOverview)) overview = nativeOverview.GetString();
        }
        return match with { Kind = MetadataClassification.Tmdb(root, match.Type), Animated = MetadataClassification.Animation(root), Title = Text(match.Type == "tv" ? "name" : "title") ?? match.Title,
            OriginalTitle = Text(match.Type == "tv" ? "original_name" : "original_title"), ProviderIds = ids,
            Genres = root.TryGetProperty("genres", out var genres) ? genres.EnumerateArray().Select(g => g.TryGetProperty("name", out var name) ? name.GetString() : null).OfType<string>().ToArray() : null,
            Cast = root.TryGetProperty("credits", out var credits) && credits.TryGetProperty("cast", out var cast) ? cast.EnumerateArray().Take(20).Select(g => g.GetProperty("name").GetString()).OfType<string>().ToArray() : null,
            Rating = root.TryGetProperty("vote_average", out var rating) && rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out var value) ? value : null,
            Poster = Text("poster_path") ?? match.Poster, Overview = overview ?? match.Overview, Year = date?.Length >= 4 ? date[..4] : match.Year };
    }
    public async Task<MetadataMatch?> MatchAsync(MediaItem item, CancellationToken ct = default, IReadOnlyList<string>? queries = null)
    {
        if (item.Year is null && !LibraryIdentity.Locked(item, "Year") && MetadataTitles.FolderYear(item) is { } folderYear) item = item with { Year = folderYear };
        var known = System.Text.RegularExpressions.Regex.Match(item.PosterSource ?? "", @"^https://www\.themoviedb\.org/(tv|movie)/(\d+)");
        var knownType = item.MetadataType ?? (known.Success ? known.Groups[1].Value : null);
        var knownId = int.TryParse(LibraryIdentity.ProviderId(item, "tmdb"), out var stored) ? stored : known.Success ? int.Parse(known.Groups[2].Value) : (int?)null;
        knownType ??= item.Series is not null ? "tv" : "movie";
        if (knownId is null && LibraryIdentity.ProviderId(item, "imdb") is null && LibraryIdentity.ProviderId(item, "tvdb") is null && LibraryIdentity.ProviderId(item, "tvmaze") is { } mazeId)
        {
            // This independent client must never forward TMDB credentials to TVmaze.
            using var bridge = imageHandler is null ? new HttpClient() : new HttpClient(imageHandler, false);
            bridge.Timeout = TimeSpan.FromSeconds(20);
            try
            {
                using var maze = JsonDocument.Parse(await MetadataHttp.GetString(bridge, $"https://api.tvmaze.com/shows/{Uri.EscapeDataString(mazeId)}", ct));
                if (maze.RootElement.TryGetProperty("externals", out var references))
                {
                    var ids = new Dictionary<string,string>(item.ProviderIds ?? []);
                    foreach (var (field, provider) in new[] { ("imdb", "imdb"), ("thetvdb", "tvdb") })
                        if (references.TryGetProperty(field, out var reference) && reference.ValueKind is JsonValueKind.String or JsonValueKind.Number) ids[provider] = reference.ToString();
                    item = item with { ProviderIds = ids };
                }
            }
            catch (HttpRequestException) { }
        }
        if (knownId is > 0 && knownType is "tv" or "movie")
        {
            try
            {
                var details = await DetailsAsync(new(knownId.Value, knownType, item.DisplayTitle, item.Year?.ToString(), null, item.Overview ?? ""), ct);
                if (MetadataMatching.Compatible(item, details)) return details;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        foreach (var provider in new[] { "imdb", "tvdb" })
        {
            if (LibraryIdentity.ProviderId(item, provider) is not { } externalId) continue;
            using var found = await Get($"find/{Uri.EscapeDataString(externalId)}?external_source={provider}_id", ct);
            var list = found.RootElement.GetProperty(knownType == "tv" ? "tv_results" : "movie_results").EnumerateArray().ToArray();
            if (list.Length == 1)
            {
                var details = await DetailsAsync(new(list[0].GetProperty("id").GetInt32(), knownType, item.DisplayTitle, item.Year?.ToString(), null, ""), ct);
                if (MetadataMatching.Compatible(item, details)) return details;
            }
        }
        var movieEvidence = !item.Matched && !item.MetadataLocked && MetadataMatching.HasMovieEvidence(item);
        var typoSearches = new List<(MediaItem Item, MetadataMatch[]? Candidates)>();
        Task<MetadataMatch?> ChooseSearch(IEnumerable<MetadataMatch> candidates, MediaItem source) =>
            MetadataMatching.ChooseWithAliasesAsync(candidates, source, AlternativeTitlesAsync, ct, rows => typoSearches.Add((source, rows)));
        var tv = movieEvidence ? [] : await SearchAsync(item.DisplayTitle, true, ct, item.Year);
        var movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(item.DisplayTitle, false, ct, item.Year);
        var best = await ChooseSearch(tv.Concat(movies), item);
        // A region's release year can differ; broaden the query without accepting tied matches.
        if (best is null && item.Year is not null)
        {
            tv = movieEvidence ? [] : await SearchAsync(item.DisplayTitle, true, ct);
            movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(item.DisplayTitle, false, ct);
            best = await ChooseSearch(tv.Concat(movies), item);
        }
        if (best is null) foreach (var query in (queries ?? MetadataTitles.Queries(item)).Skip(1))
        {
            tv = movieEvidence ? [] : await SearchAsync(query, true, ct);
            movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(query, false, ct);
            best = await ChooseSearch(tv.Concat(movies), item with { Title = query, Series = item.Series is null ? null : query });
            if (best is not null) break;
        }
        // Inconsistent romanization can prevent a full query from reaching the correct
        // result. Search a distinctive Cyrillic word, then require the full source title.
        if (best is null && MetadataTitles.RussianSearchAlias(item.DisplayTitle) is { } russian)
        {
            foreach (var word in russian.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 5).OrderByDescending(x => x.Length).Take(2))
            {
                tv = movieEvidence ? [] : await SearchAsync(word, true, ct, item.Year);
                movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(word, false, ct, item.Year);
                best = MetadataMatching.Choose(tv.Concat(movies), item);
                if (best is not null) break;
            }
        }
        var typoFallback = best is null;
        best ??= MetadataMatching.ChooseTypoFallback(typoSearches);
        if (best is null) return null;
        var selected = await DetailsAsync(best, ct);
        return MetadataMatching.Compatible(item, selected) && (!typoFallback || selected.Year == best.Year) ? selected : null;
    }
    private async Task<string[]> AlternativeTitlesAsync(MetadataMatch match, CancellationToken ct)
    {
        using var json = await Get($"{match.Type}/{match.Id}/alternative_titles", ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty(match.Type == "tv" ? "results" : "titles", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid alternative-title response.");
        return list.EnumerateArray().Where(x => x.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("title").GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public Task<string?> CachePosterAsync(MetadataMatch match, string directory, CancellationToken ct = default) =>
        CacheImageAsync(match.Poster, $"{match.Type}-{match.Id}", directory, ct);
    public async Task<string?> CacheImageAsync(string? image, string key, string directory, CancellationToken ct = default, bool replace = false, string imageSize = "w500")
    {
        if (imageSize is not ("w500" or "w780")) throw new ArgumentException("Invalid image size.");
        if (image is null || !image.StartsWith('/') || image.Contains("..")) return null;
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains("..")) throw new ArgumentException("Invalid image cache key.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + "-" + imageSize + "-" + LibraryIdentity.Hash(image)[..12] + (replace ? "-" + Guid.NewGuid().ToString("N") : "") + ".jpg");
        if (replace || !ArtworkCache.IsUsable(path))
        {
            // Separate client: never send the API authorization token to the image CDN.
            using var images = imageHandler is null ? new HttpClient() : new HttpClient(imageHandler, false);
            images.Timeout = TimeSpan.FromSeconds(20);
            var bytes = await MetadataHttp.GetImage(images, "https://image.tmdb.org/t/p/" + imageSize + image, ct);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return path;
    }
    public void Dispose() => http.Dispose();

    public async Task<MetadataSeason[]> SeasonsAsync(int showId, CancellationToken ct)
    {
        using var json = await Get($"tv/{showId}", ct);
        if (!json.RootElement.TryGetProperty("seasons", out var list)) return [];
        var result = new List<MetadataSeason>();
        foreach (var season in list.EnumerateArray()) if (await SeasonAsync(showId, season.GetProperty("season_number").GetInt32(), ct) is { } details) result.Add(details);
        return result.ToArray();
    }
    public async Task<MetadataSeason[]> DvdSeasonsAsync(int showId, CancellationToken ct)
    {
        using var groups = await Get($"tv/{showId}/episode_groups", ct);
        var dvd = groups.RootElement.GetProperty("results").EnumerateArray().Where(g => g.TryGetProperty("type", out var type) && type.GetInt32() == 3).ToArray();
        if (dvd.Length != 1) return [];
        using var group = await Get("tv/episode_group/" + dvd[0].GetProperty("id").GetString(), ct);
        return group.RootElement.GetProperty("groups").EnumerateArray().Select(s => new MetadataSeason(s.GetProperty("order").GetInt32(),
            s.GetProperty("name").GetString(), null, null, null, s.GetProperty("episodes").EnumerateArray().Select(e => new MetadataEpisode(
                e.GetProperty("order").GetInt32() + 1, e.GetProperty("name").GetString(), e.TryGetProperty("overview", out var plot) ? plot.GetString() : null,
                e.TryGetProperty("still_path", out var image) ? image.GetString() : null, e.TryGetProperty("air_date", out var date) ? date.GetString() : null,
                e.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : null)).ToArray())).ToArray();
    }
}
