using System.Net.Http.Headers;
using System.Text.Json;

namespace Watchroom.Core;

public record MetadataMatch(int Id, string Type, string Title, string? Year, string? Poster, string Overview)
{
    public override string ToString() => $"{Title} ({Year ?? "Unknown year"}) · {Type}";
}
public sealed class MetadataClient : IDisposable
{
    private readonly HttpClient http = new() { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(20) };
    public MetadataClient(string token) => http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
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
                x.TryGetProperty("overview", out var o) ? o.GetString() ?? "" : "");
        }).ToList();
    }
    public async Task<string?> CachePosterAsync(MetadataMatch match, string directory, CancellationToken ct = default)
    {
        if (match.Poster is null || !match.Poster.StartsWith('/') || match.Poster.Contains("..")) return null;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{match.Type}-{match.Id}.jpg");
        if (!File.Exists(path))
        {
            // Separate client: never send the API authorization token to the image CDN.
            using var images = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await images.GetByteArrayAsync("https://image.tmdb.org/t/p/w342" + match.Poster, ct);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        return path;
    }
    public void Dispose() => http.Dispose();
}
