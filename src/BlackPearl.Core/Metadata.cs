using System.Net.Http.Headers;
using System.Text.Json;

namespace BlackPearl.Core;

public record MetadataMatch(int Id, string Type, string Title, string? Year, string? Poster, string Overview, string? Kind = null,
    string? OriginalTitle = null, Dictionary<string, string>? ProviderIds = null, string[]? Genres = null, string[]? Cast = null, double? Rating = null, bool? Animated = null,
    string[]? Aliases = null, string Provider = "tmdb", string? Source = null, string? Evidence = null,
    int? RuntimeMinutes = null, int? EpisodeCount = null, int? SeasonCount = null, bool NumberedSeries = false, string? ReleaseDate = null, string? Ended = null)
{
    public string Description => $"{Year ?? "Unknown year"} · {(Animated == true ? "Animation" : Type == "tv" && Animated == false ? "Live action" : Type == "tv" ? "Series" : "Film")} · {Provider}";
    public override string ToString() => $"{Title} · {Description}";
}
// SourceSeason and SourceNumber: where an episode listed in another order sits in the provider's own aired numbering.
public record MetadataEpisode(int Number, string? Title, string? Overview, string? Image, string? AirDate, int? Runtime, int? SourceSeason = null, int? SourceNumber = null);
public record MetadataSeason(int Number, string? Title, string? Overview, string? Image, string? AirDate, MetadataEpisode[] Episodes);
public record MetadataEpisodeGroup(string Id, int Type, string? Name);
public sealed partial class MetadataClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpMessageHandler? imageHandler;
    private readonly MetadataOptions options;
    private readonly string? apiKey;
    public MetadataClient(string? token, HttpMessageHandler? handler = null, MetadataOptions? options = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BlackPearl/0.3 (+https://github.com/Svovoniks/BlackPearl; metadata)");
        http.BaseAddress = new Uri("https://api.themoviedb.org/3/"); http.Timeout = TimeSpan.FromSeconds(20);
        token = TmdbCredentials.Resolve(token) ?? throw new InvalidOperationException("TMDB is unavailable in this build without a user key.");
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
            // With a region, a film's date is its release there, often years after its premiere. The year filter
            // accepts any release in that year, so a film it returned does have one.
            return new MetadataMatch(x.GetProperty("id").GetInt32(), type, x.GetProperty(series ? "name" : "title").GetString()!,
                !series && year is not null && !string.IsNullOrEmpty(date) ? year.ToString() : date?.Length >= 4 ? date[..4] : null, x.TryGetProperty("poster_path", out var p) ? p.GetString() : null,
                x.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : "", MetadataClassification.Tmdb(x, type),
                x.TryGetProperty(series ? "original_name" : "original_title", out var original) ? original.GetString() : null, Animated: MetadataClassification.Animation(x), ReleaseDate: string.IsNullOrEmpty(date) ? null : date);
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
            Poster = Text("poster_path") ?? match.Poster, Overview = overview ?? match.Overview, Year = date?.Length >= 4 ? date[..4] : match.Year, ReleaseDate = string.IsNullOrEmpty(date) ? match.ReleaseDate : date,
            RuntimeMinutes = root.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) && minutes > 0 ? minutes : null,
            EpisodeCount = root.TryGetProperty("number_of_episodes", out var episodes) && episodes.ValueKind == JsonValueKind.Number && episodes.TryGetInt32(out var count) ? count : null,
            SeasonCount = root.TryGetProperty("number_of_seasons", out var seasons) && seasons.ValueKind == JsonValueKind.Number && seasons.TryGetInt32(out var seasonCount) ? seasonCount : null };
    }
    public async Task<MetadataMatch?> MatchAsync(MediaItem item, CancellationToken ct = default, IReadOnlyList<string>? queries = null, IEnumerable<MediaItem>? entries = null,
        IReadOnlySet<string>? rejected = null, IReadOnlySet<string>? knownShows = null, (string Type, int Id)? recheck = null)
    {
        bool Rejected(string type, int id) => rejected?.Contains(MetadataRejections.Key("tmdb", type, id)) == true;
        // Rechecking an automatic match: the item is the files' own reading, and the stored match stays unless this lookup ranks another above it.
        MetadataMatch? kept = null;
        if (recheck is { } previous && !Rejected(previous.Type, previous.Id))
            try
            {
                var details = await DetailsAsync(new(previous.Id, previous.Type, item.DisplayTitle, item.Year?.ToString(), null, item.Overview ?? ""), ct);
                if (MetadataMatching.Compatible(item, details)) kept = details;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        if (item.Year is null && !LibraryIdentity.Locked(item, "Year") && MetadataTitles.FolderYear(item) is { } folderYear) item = item with { Year = folderYear };
        var known = System.Text.RegularExpressions.Regex.Match(item.PosterSource ?? "", @"^https://www\.themoviedb\.org/(tv|movie)/(\d+)");
        var knownType = item.MetadataType ?? (known.Success ? known.Groups[1].Value : null);
        var knownId = int.TryParse(LibraryIdentity.ProviderId(item, "tmdb"), out var stored) ? stored : known.Success ? int.Parse(known.Groups[2].Value) : (int?)null;
        knownType ??= item.Series is not null ? "tv" : "movie";
        if (recheck is null && knownId is null && LibraryIdentity.ProviderId(item, "imdb") is null && LibraryIdentity.ProviderId(item, "tvdb") is null && LibraryIdentity.ProviderId(item, "tvmaze") is { } mazeId)
        {
            // This independent client must never forward TMDB credentials to TVmaze.
            using var bridge = imageHandler is null ? MetadataHttp.CreateClient() : new HttpClient(imageHandler, false);
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
        if (recheck is null && knownId is > 0 && knownType is "tv" or "movie" && !Rejected(knownType, knownId.Value))
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
            if (recheck is not null || LibraryIdentity.ProviderId(item, provider) is not { } externalId) continue;
            using var found = await Get($"find/{Uri.EscapeDataString(externalId)}?external_source={provider}_id", ct);
            var list = found.RootElement.GetProperty(knownType == "tv" ? "tv_results" : "movie_results").EnumerateArray().ToArray();
            if (list.Length == 1 && !Rejected(knownType, list[0].GetProperty("id").GetInt32()))
            {
                var details = await DetailsAsync(new(list[0].GetProperty("id").GetInt32(), knownType, item.DisplayTitle, item.Year?.ToString(), null, ""), ct);
                if (MetadataMatching.Compatible(item, details)) return details;
            }
        }
        var movieEvidence = !item.Matched && !item.MetadataLocked && MetadataMatching.HasMovieEvidence(item);
        var searchQueries = queries ?? MetadataTitles.Queries(item);
        var titleQuery = searchQueries.FirstOrDefault() ?? item.DisplayTitle;
        var queryItem = item with { Title = titleQuery, Series = item.Series is null ? null : titleQuery };
        var typoSearches = new List<(MediaItem Item, MetadataMatch[]? Candidates)>();
        var evidenceSearches = new List<(MediaItem Item, MetadataMatch[] Candidates)>();
        Exception? aliasFailure = null;
        async Task<MetadataMatch?> ChooseSearch(IEnumerable<MetadataMatch> candidates, MediaItem source)
        {
            var rows = await WithEndedAsync(candidates.Where(x => !Rejected(x.Type, x.Id)).ToArray(), source, ct); evidenceSearches.Add((source, rows));
            try
            {
                var chosen = await MetadataMatching.ChooseWithAliasesAsync(rows, source, AlternativeTitlesAsync, ct, rows => typoSearches.Add((source, rows)));
                // A dated local movie cannot be identified by an undated placeholder
                // solely because all dated adaptations have a different year.
                if (source.Year is not null && chosen is { Type: "movie", Year: null })
                {
                    MetadataDiagnostics.Outcome("missing-release-year");
                    MetadataDiagnostics.Record("metadata-candidate-rejected", new { chosen.Id, chosen.Title, Reason = "missing-release-year" });
                    return null;
                }
                return chosen;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            { aliasFailure = ex; typoSearches.Add((source, null)); return null; }
        }
        // A later season's year may be when that season aired; filtering shows by it would leave out the show that began earlier.
        var showYear = MetadataMatching.LaterSeason(item) ? null : item.Year;
        var tv = movieEvidence ? [] : await SearchAsync(titleQuery, true, ct, showYear);
        var movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(titleQuery, false, ct, item.Year);
        var best = await ChooseSearch(tv.Concat(movies), queryItem);
        // A region's release year can differ; broaden the query without accepting tied matches.
        if (best is null && item.Year is not null)
        {
            tv = movieEvidence ? [] : await SearchAsync(titleQuery, true, ct);
            movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(titleQuery, false, ct);
            best = await ChooseSearch(tv.Concat(movies), queryItem);
        }
        if (best is null) foreach (var query in searchQueries.Skip(1))
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
                tv = movieEvidence ? [] : await SearchAsync(word, true, ct, showYear);
                movies = item.Episode is not null || item.MetadataType == "tv" ? [] : await SearchAsync(word, false, ct, item.Year);
                var source = item with { Title = russian, Series = item.Series is null ? null : russian };
                best = await ChooseSearch(tv.Concat(movies), source);
                if (best is not null) break;
            }
        }
        // Shows of the same name are told apart by the entry the library matched other copies to, then by the seasons the files hold.
        if (best is null && knownShows is { Count: > 0 }) best = MatchKnownShow(item, evidenceSearches, knownShows);
        if (best is null) best = await MatchEpisodeEvidenceAsync(item, entries, evidenceSearches, ct);
        var typoFallback = best is null;
        best ??= MetadataMatching.ChooseTypoFallback(typoSearches);
        if (best is null) best = await MatchMovieEvidenceAsync(item, evidenceSearches, ct);
        if (best is null) best = await MatchNumberedSeriesAsync(item, searchQueries, entries, ct);
        if (best is not null && Rejected(best.Type, best.Id)) best = null;
        if (kept is not null)
        {
            // Only a lookup that saw both and ranks the new one higher replaces the stored match; a tie-break or a fallback doesn't.
            var switching = best is not null && (best.Type != kept.Type || best.Id != kept.Id) && evidenceSearches.Any(search =>
                search.Candidates.FirstOrDefault(x => x.Type == kept.Type && x.Id == kept.Id) is { } stored &&
                search.Candidates.FirstOrDefault(x => x.Type == best.Type && x.Id == best.Id) is { } found && MetadataMatching.Outranks(found, stored, search.Item));
            MetadataDiagnostics.Record("metadata-recheck", new { Kept = kept.Id, kept.Type, Found = best?.Id, FoundType = best?.Type, Switched = switching });
            if (!switching) return kept;
        }
        if (best is null)
        {
            if (aliasFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(aliasFailure).Throw();
            return null;
        }
        var selected = await DetailsAsync(best, ct);
        return MetadataMatching.Compatible(item, selected) && (!typoFallback || selected.Year == best.Year) ? selected : kept;
    }
    // For a later season with a year, a show that began earlier stays in the running only if it was on air then, which search results don't say.
    private async Task<MetadataMatch[]> WithEndedAsync(MetadataMatch[] rows, MediaItem source, CancellationToken ct)
    {
        if (!MetadataMatching.LaterSeason(source) || source.Year is not { } year) return rows;
        var result = rows.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            var row = result[index];
            if (row.Type != "tv" || !int.TryParse(row.Year, out var began) || began >= year - 1 ||
                MetadataMatching.Score(source.DisplayTitle, null, row.Title, row.OriginalTitle, null) < 6) continue;
            using var json = await Get($"tv/{row.Id}", ct);
            var root = json.RootElement;
            // The last episode's date, even for a show still marked in production: "Rage" (1987) is listed as returning with nothing since 2007.
            // A revival whose new season the provider hasn't listed yet stays unmatched until it does, rather than going to another show.
            if (root.TryGetProperty("last_air_date", out var last) && last.ValueKind == JsonValueKind.String) result[index] = row with { Ended = last.GetString() };
        }
        return result;
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
        if (replace || !ArtworkCache.IsUsable(path) || File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow - TmdbRetention.MaximumAge)
        {
            // Separate client: never send the API authorization token to the image CDN.
            using var images = imageHandler is null ? MetadataHttp.CreateClient() : new HttpClient(imageHandler, false);
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
        // The provider gate bounds concurrency and spacing; requesting seasons together lets it use both slots.
        var numbers = list.EnumerateArray().Select(season => season.GetProperty("season_number").GetInt32()).ToArray();
        var seasons = await Task.WhenAll(numbers.Select(number => SeasonAsync(showId, number, ct)));
        return seasons.OfType<MetadataSeason>().ToArray();
    }
    public async Task<MetadataSeason[]> DvdSeasonsAsync(int showId, CancellationToken ct)
    {
        var dvd = (await EpisodeGroupsAsync(showId, ct)).Where(g => g.Type == 3).ToArray();
        return dvd.Length == 1 ? await GroupSeasonsAsync(dvd[0].Id, ct) : [];
    }
    // A show's alternative numbering orders: original air date 1, absolute 2, DVD 3, digital 4, story arc 5, production 6, TV 7.
    public async Task<MetadataEpisodeGroup[]> EpisodeGroupsAsync(int showId, CancellationToken ct)
    {
        using var groups = await Get($"tv/{showId}/episode_groups", ct);
        return groups.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Where(g => g.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && g.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number)
                .Select(g => new MetadataEpisodeGroup(g.GetProperty("id").GetString()!, g.GetProperty("type").GetInt32(), g.TryGetProperty("name", out var name) ? name.GetString() : null)).ToArray()
            : [];
    }
    // A group's seasons numbered by their order in it, and its episodes from 1 in theirs.
    public async Task<MetadataSeason[]> GroupSeasonsAsync(string groupId, CancellationToken ct)
    {
        JsonDocument document;
        try { document = await Get("tv/episode_group/" + Uri.EscapeDataString(groupId), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return []; }
        using var group = document;
        return group.RootElement.GetProperty("groups").EnumerateArray().Select(s => new MetadataSeason(s.GetProperty("order").GetInt32(),
            s.GetProperty("name").GetString(), null, null, null, s.GetProperty("episodes").EnumerateArray().Select(e => new MetadataEpisode(
                e.GetProperty("order").GetInt32() + 1, e.GetProperty("name").GetString(), e.TryGetProperty("overview", out var plot) ? plot.GetString() : null,
                e.TryGetProperty("still_path", out var image) ? image.GetString() : null, e.TryGetProperty("air_date", out var date) ? date.GetString() : null,
                e.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : null,
                e.TryGetProperty("season_number", out var aired) && aired.ValueKind == JsonValueKind.Number ? aired.GetInt32() : null,
                e.TryGetProperty("episode_number", out var number) && number.ValueKind == JsonValueKind.Number ? number.GetInt32() : null)).ToArray())).ToArray();
    }
}
