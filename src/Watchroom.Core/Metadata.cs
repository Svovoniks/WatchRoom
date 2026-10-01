using System.Net.Http.Headers;
using System.Text.Json;

namespace Watchroom.Core;

public record MetadataMatch(int Id, string Type, string Title, string? Year, string? Poster, string Overview, string? Kind = null)
{
    public override string ToString() => $"{Title} ({Year ?? "Unknown year"}) · {Type}";
}
public record MetadataEpisode(int Number, string? Title, string? Overview, string? Image, string? AirDate, int? Runtime);
public record MetadataSeason(int Number, string? Title, string? Overview, string? Image, string? AirDate, MetadataEpisode[] Episodes);
public sealed class MetadataClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpMessageHandler? imageHandler;
    public MetadataClient(string token, HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler, false);
        http.BaseAddress = new Uri("https://api.themoviedb.org/3/"); http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        imageHandler = handler;
    }
    public async Task<MetadataSeason?> SeasonAsync(int showId, int season, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"tv/{showId}/season/{season}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var episodes = root.GetProperty("episodes").EnumerateArray().Select(e => new MetadataEpisode(e.GetProperty("episode_number").GetInt32(),
            Text(e, "name"), Text(e, "overview"), Text(e, "still_path"), Text(e, "air_date"),
            e.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.TryGetInt32(out var minutes) ? minutes : null)).ToArray();
        return new(season, Text(root, "name"), Text(root, "overview"), Text(root, "poster_path"), Text(root, "air_date"), episodes);
    }
    public async Task<List<MetadataMatch>> SearchAsync(string title, bool series, CancellationToken ct = default)
    {
        var type = series ? "tv" : "movie";
        using var response = await http.GetAsync($"search/{type}?query={Uri.EscapeDataString(title)}&include_adult=false", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("results").EnumerateArray().Take(15).Select(x =>
        {
            var date = x.TryGetProperty(series ? "first_air_date" : "release_date", out var d) ? d.GetString() : null;
            return new MetadataMatch(x.GetProperty("id").GetInt32(), type, x.GetProperty(series ? "name" : "title").GetString()!,
                date?.Length >= 4 ? date[..4] : null, x.TryGetProperty("poster_path", out var p) ? p.GetString() : null,
                x.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : "", MetadataClassification.Tmdb(x, type));
        }).ToList();
    }
    public async Task<MetadataMatch> DetailsAsync(MetadataMatch match, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{match.Type}/{match.Id}?append_to_response=keywords", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var date = Text(match.Type == "tv" ? "first_air_date" : "release_date");
        return match with { Kind = MetadataClassification.Tmdb(root, match.Type), Title = Text(match.Type == "tv" ? "name" : "title") ?? match.Title,
            Poster = Text("poster_path") ?? match.Poster, Overview = Text("overview") ?? match.Overview, Year = date?.Length >= 4 ? date[..4] : match.Year };
    }
    public async Task<MetadataMatch?> MatchAsync(MediaItem item, CancellationToken ct = default)
    {
        var known = System.Text.RegularExpressions.Regex.Match(item.PosterSource ?? "", @"^https://www\.themoviedb\.org/(tv|movie)/(\d+)");
        var knownType = item.MetadataType ?? (known.Success ? known.Groups[1].Value : null);
        var knownId = item.MetadataProvider == "tmdb" ? item.MetadataId : known.Success ? int.Parse(known.Groups[2].Value) : (int?)null;
        if (knownId is > 0 && knownType is "tv" or "movie")
            return await DetailsAsync(new(knownId.Value, knownType, item.DisplayTitle, item.Year?.ToString(), null, item.Overview ?? ""), ct);
        var tv = await SearchAsync(item.DisplayTitle, true, ct);
        var movies = await SearchAsync(item.DisplayTitle, false, ct);
        string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
        var matches = tv.Concat(movies).Where(x => Normalize(x.Title) == Normalize(item.DisplayTitle)
            && (item.Year is null || x.Year == item.Year.ToString())).ToArray();
        if (item.Episode is not null && matches.Any(x => x.Type == "tv")) matches = matches.Where(x => x.Type == "tv").ToArray();
        return matches.Length == 1 ? await DetailsAsync(matches[0], ct) : null;
    }
    public Task<string?> CachePosterAsync(MetadataMatch match, string directory, CancellationToken ct = default) =>
        CacheImageAsync(match.Poster, $"{match.Type}-{match.Id}", directory, ct);
    public async Task<string?> CacheImageAsync(string? image, string key, string directory, CancellationToken ct = default)
    {
        if (image is null || !image.StartsWith('/') || image.Contains("..")) return null;
        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains("..")) throw new ArgumentException("Invalid image cache key.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".jpg");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            // Separate client: never send the API authorization token to the image CDN.
            using var images = imageHandler is null ? new HttpClient() : new HttpClient(imageHandler, false);
            images.Timeout = TimeSpan.FromSeconds(20);
            var bytes = await images.GetByteArrayAsync("https://image.tmdb.org/t/p/w342" + image, ct);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes, ct); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return path;
    }
    public void Dispose() => http.Dispose();
}
