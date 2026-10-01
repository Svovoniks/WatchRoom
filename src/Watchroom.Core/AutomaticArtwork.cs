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
    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress, CancellationToken ct, HttpClient? client = null, bool force = false)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
        var http = client ?? owned!; http.DefaultRequestHeaders.UserAgent.ParseAdd("Watchroom/0.2 (desktop media library)");
        using var tmdb = string.IsNullOrWhiteSpace(token) ? null : new MetadataClient(token);
        var groups = library.All().Where(x => x.Available).GroupBy(x => x.Series is null ? x.Id : x.Kind + ":" + x.Series).ToArray();
        int fetched = 0, overviews = 0, missing = 0, failed = 0; string? error = null;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            var entries = group.ToArray();
            var existingOverview = entries.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Overview))?.Overview;
            if (existingOverview is not null)
                foreach (var entry in entries.Where(x => string.IsNullOrWhiteSpace(x.Overview))) library.Save(entry with { Overview = existingOverview });
            var needsOverview = existingOverview is null;
            var needsClassification = force || entries.Any(x => x.MetadataKind is null);
            var needsPoster = !entries.Any(x => x.Matched || File.Exists(x.SeriesPoster) || File.Exists(x.SeasonPoster) || File.Exists(x.Poster));
            if (!needsOverview && !needsPoster && !needsClassification) continue;
            var item = group.First(); progress?.Report("Finding metadata · " + item.DisplayTitle);
            try
            {
                string? poster = null, source = null, overview = null;
                if (tmdb is not null)
                {
                    var match = await tmdb.MatchAsync(item, ct);
                    if (match is not null)
                    {
                        Classify(match.Kind ?? (match.Type == "tv" ? "Show" : "Movie"), match.Type, "tmdb", match.Id, match.Title);
                        overview = match.Overview;
                        source = $"https://www.themoviedb.org/{match.Type}/{match.Id}";
                        SaveOverview();
                        if (needsPoster) poster = await tmdb.CachePosterAsync(match, Path.Combine(directory, "posters"), ct);
                    }
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
                    if (match is not null)
                    {
                        Classify(match.Value.Kind, match.Value.Type, match.Value.Provider, match.Value.Id, match.Value.Title);
                        overview = match.Value.Overview; source = match.Value.Source;
                        SaveOverview();
                        if (needsPoster && match.Value.Image is not null) poster = await Download(http, match.Value.Image, directory, ct);
                    }
                }
                void Classify(string kind, string type, string provider, int? id, string? title)
                {
                    entries = entries.Select(entry => MetadataClassification.Apply(entry, kind, type, type == "tv" ? title : null) with { MetadataProvider = provider, MetadataId = id }).ToArray();
                    foreach (var entry in entries) library.Save(entry);
                }
                void SaveOverview()
                {
                    if (!needsOverview || string.IsNullOrWhiteSpace(overview)) return;
                    foreach (var entry in entries) library.Save(entry with { Overview = overview });
                    overviews++;
                }
                if (poster is not null)
                {
                    foreach (var entry in entries) library.Save(entry with { Poster = poster, SeriesPoster = entry.Series is null ? null : poster, PosterSource = source,
                        Overview = string.IsNullOrWhiteSpace(entry.Overview) ? needsOverview ? overview : existingOverview : entry.Overview });
                    fetched++;
                }
                if (needsPoster && poster is null || needsOverview && string.IsNullOrWhiteSpace(overview)) missing++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            { failed++; error = ex is HttpRequestException h && h.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "Provider rejected access; check your TMDB token." : "A metadata provider could not be reached or returned invalid data."; if (failed >= 3) break; }
            await Task.Delay(550, ct);
        }
        var seriesReport = await SeriesMetadata.FetchAsync(library, directory, token, progress, ct, http, force);
        return $"Posters: {fetched} added · Overviews: {overviews} added · {missing} need a manual match · {failed} failed." + (error is null ? "" : " " + error) + " " + seriesReport;
    }
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await http.GetStringAsync(url, ct));
    private static async Task<(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title)?> Lookup(HttpClient http, MediaItem item, CancellationToken ct)
    {
        var show = await LookupShow(http, item, ct);
        if (show is not null && (item.Episode is not null || item.MetadataType == "tv" || item.MetadataProvider == "tvmaze")) return show;
        var movie = await LookupMovie(http, item, ct);
        return show is not null && movie is not null ? null : show ?? movie;
    }
    private static async Task<(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title)?> LookupShow(HttpClient http, MediaItem item, CancellationToken ct)
    {
            var known = Regex.Match(item.PosterSource ?? "", @"^https://www\.tvmaze\.com/shows/(\d+)");
            var knownId = item.MetadataProvider == "tvmaze" ? item.MetadataId : known.Success ? int.Parse(known.Groups[1].Value) : (int?)null;
            using var results = await Json(http, knownId is > 0 ? $"https://api.tvmaze.com/shows/{knownId}" : "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle), ct);
            var candidates = knownId is > 0 ? new[] { results.RootElement } : results.RootElement.EnumerateArray().Select(x => x.GetProperty("show"));
            var matches = candidates.Where(x => knownId is > 0 || Normalize(x.GetProperty("name").GetString()!) == Normalize(item.DisplayTitle)
                && (item.Year is null || x.GetProperty("premiered").GetString()?.StartsWith(item.Year.ToString()!) == true)).ToArray();
            if (matches.Length != 1) return null;
            var show = matches[0];
            var imageUrl = show.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? image.GetProperty("medium").GetString() : null;
            var summary = show.TryGetProperty("summary", out var text) ? text.GetString() : null;
            // TVmaze summaries contain HTML; render readable plain text in the native UI.
            summary = summary is null ? null : WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(summary, @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim();
            return (imageUrl, show.GetProperty("url").GetString()!, summary, MetadataClassification.Tvmaze(show), "tv", "tvmaze",
                show.TryGetProperty("id", out var id) ? id.GetInt32() : null, show.GetProperty("name").GetString());
    }
    private static async Task<(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title)?> LookupMovie(HttpClient http, MediaItem item, CancellationToken ct)
    {
        var titles = item.Year is { } year ? new[] { $"{item.Title} ({year} film)", $"{item.Title} (film)", item.Title } : new[] { $"{item.Title} (film)", item.Title };
        using var pages = await Json(http, "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=pageimages%7Ccategories%7Cextracts&exintro=1&explaintext=1&exlimit=max&piprop=thumbnail&pithumbsize=500&pilicense=any&cllimit=50&titles=" + Uri.EscapeDataString(string.Join("|", titles)), ct);
        var candidates = pages.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray().Where(p => p.TryGetProperty("title", out var title)
            && Normalize(Regex.Replace(title.GetString() ?? "", @"\s*\([^)]*film\)\s*$", "", RegexOptions.IgnoreCase)) == Normalize(item.DisplayTitle)
            && p.TryGetProperty("categories", out var categories)
            && categories.EnumerateArray().Any(c => c.GetProperty("title").GetString()!.Contains("films", StringComparison.OrdinalIgnoreCase))).ToArray();
        // Never silently choose between remakes with the same name.
        if (candidates.Length != 1) return null;
        var page = candidates[0];
        return (page.TryGetProperty("thumbnail", out var thumbnail) ? thumbnail.GetProperty("source").GetString() : null,
            "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(page.GetProperty("title").GetString()!),
            page.TryGetProperty("extract", out var extract) ? extract.GetString() : null,
            MetadataClassification.Wikipedia(page), "movie", "wikipedia", page.TryGetProperty("pageid", out var pageId) ? pageId.GetInt32() : null, null);
    }
    internal static async Task<string> Download(HttpClient http, string url, string directory, CancellationToken ct)
    {
        var uri = new Uri(url); if (uri.Scheme != "https" || !(uri.Host.EndsWith(".tvmaze.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "upload.wikimedia.org")) throw new IOException("Unsupported artwork host.");
        var folder = Path.Combine(directory, "posters");
        var stem = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))));
        foreach (var extension in new[] { ".jpg", ".png" }) if (File.Exists(stem + extension) && new FileInfo(stem + extension).Length > 0) return stem + extension;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new IOException("Poster is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[32768];
        int count; while ((count = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + count > 10 * 1024 * 1024) throw new IOException("Poster is too large."); buffer.Write(chunk, 0, count); }
        var bytes = buffer.ToArray(); var jpeg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216; var png = bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!jpeg && !png) throw new IOException("Poster is not a supported image.");
        Directory.CreateDirectory(folder);
        var path = stem + (jpeg ? ".jpg" : ".png");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
