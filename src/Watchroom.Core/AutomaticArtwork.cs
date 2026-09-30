using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Watchroom.Core;

public static class AutomaticArtwork
{
    private static string Normalize(string value) => Regex.Replace(value, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress, CancellationToken ct, HttpClient? client = null)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
        var http = client ?? owned!; http.DefaultRequestHeaders.UserAgent.ParseAdd("Watchroom/0.2 (desktop media library)");
        using var tmdb = string.IsNullOrWhiteSpace(token) ? null : new MetadataClient(token);
        var groups = library.All().Where(x => x.Available).GroupBy(x => x.Series is null ? x.Id : x.Kind + ":" + x.Series).ToArray();
        int fetched = 0, missing = 0, failed = 0; string? error = null;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (group.Any(x => File.Exists(x.SeriesPoster) || File.Exists(x.Poster))) continue;
            var item = group.First(); progress?.Report("Finding poster · " + item.DisplayTitle);
            try
            {
                string? poster = null, source = null;
                if (tmdb is not null)
                {
                    var matches = await tmdb.SearchAsync(item.DisplayTitle, item.Series is not null || item.Kind is "Show" or "Anime", ct);
                    var exact = matches.Where(x => Normalize(x.Title) == Normalize(item.DisplayTitle) && (item.Year is null || x.Year == item.Year.ToString())).ToArray();
                    if (exact.Length == 1) { poster = await tmdb.CachePosterAsync(exact[0], Path.Combine(directory, "posters"), ct); source = $"https://www.themoviedb.org/{exact[0].Type}/{exact[0].Id}"; }
                }
                else
                {
                    var match = await Lookup(http, item, ct);
                    if (match is null && item.Kind == "Anime")
                    {
                        foreach (var alias in await AnimeTitles.AliasesAsync(http, directory, item.DisplayTitle, ct))
                        {
                            await Task.Delay(550, ct);
                            match = await Lookup(http, item with { Title = alias, Series = alias }, ct);
                            if (match is not null) break;
                        }
                    }
                    if (match is not null) { poster = await Download(http, match.Value.Image, directory, ct); source = match.Value.Source; }
                }
                if (poster is null) { missing++; await Task.Delay(550, ct); continue; }
                foreach (var entry in group) library.Save(entry with { Poster = poster, SeriesPoster = entry.Series is null ? null : poster, PosterSource = source });
                fetched++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            { failed++; error = ex is HttpRequestException h && h.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "Provider rejected access; check your TMDB token." : "A poster provider could not be reached or returned an invalid image."; if (failed >= 3) break; }
            await Task.Delay(550, ct);
        }
        return $"Posters: {fetched} added · {missing} need a manual match · {failed} failed." + (error is null ? "" : " " + error);
    }
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await http.GetStringAsync(url, ct));
    private static async Task<(string Image, string Source)?> Lookup(HttpClient http, MediaItem item, CancellationToken ct)
    {
        if (item.Series is not null || item.Kind is "Show" or "Anime")
        {
            using var results = await Json(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle), ct);
            var matches = results.RootElement.EnumerateArray().Select(x => x.GetProperty("show")).Where(x => Normalize(x.GetProperty("name").GetString()!) == Normalize(item.DisplayTitle)
                && (item.Year is null || x.GetProperty("premiered").GetString()?.StartsWith(item.Year.ToString()!) == true)).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object) return null;
            return (image.GetProperty("medium").GetString()!, matches[0].GetProperty("url").GetString()!);
        }
        var titles = item.Year is { } year ? new[] { $"{item.Title} ({year} film)", $"{item.Title} (film)", item.Title } : new[] { $"{item.Title} (film)", item.Title };
        using var pages = await Json(http, "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=pageimages%7Ccategories&piprop=thumbnail&pithumbsize=500&pilicense=any&cllimit=50&titles=" + Uri.EscapeDataString(string.Join("|", titles)), ct);
        var candidates = pages.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray().Where(p => p.TryGetProperty("thumbnail", out _) && p.TryGetProperty("categories", out var categories)
            && categories.EnumerateArray().Any(c => c.GetProperty("title").GetString()!.Contains("films", StringComparison.OrdinalIgnoreCase))).ToArray();
        // Never silently choose between remakes with the same name.
        if (candidates.Length != 1) return null;
        var page = candidates[0];
        return (page.GetProperty("thumbnail").GetProperty("source").GetString()!, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(page.GetProperty("title").GetString()!));
    }
    private static async Task<string> Download(HttpClient http, string url, string directory, CancellationToken ct)
    {
        var uri = new Uri(url); if (uri.Scheme != "https" || !(uri.Host.EndsWith(".tvmaze.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "upload.wikimedia.org")) throw new IOException("Unsupported artwork host.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new IOException("Poster is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[32768];
        int count; while ((count = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > 10 * 1024 * 1024) throw new IOException("Poster is too large."); buffer.Write(chunk, 0, count); }
        var bytes = buffer.ToArray(); var jpeg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216; var png = bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!jpeg && !png) throw new IOException("Poster is not a supported image.");
        var folder = Path.Combine(directory, "posters"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + (jpeg ? ".jpg" : ".png"));
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
