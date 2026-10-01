using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public static class SeriesMetadata
{
    private static string Normalize(string value) => Regex.Replace(value, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
    private static string? Plain(string? html) => html is null ? null : WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim();
    private static string? Image(JsonElement value) => value.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "medium") ?? Text(image, "original") : null;
    private static string? Nonempty(string? fetched, string? previous) => string.IsNullOrWhiteSpace(fetched) ? previous : fetched;
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await http.GetStringAsync(url, ct));

    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress,
        CancellationToken ct, HttpClient http, bool force = false, MetadataClient? metadata = null)
    {
        using var owned = metadata is null && !string.IsNullOrWhiteSpace(token) ? new MetadataClient(token) : null;
        var tmdb = metadata ?? owned;
        var provider = tmdb is null ? "tvmaze" : "tmdb";
        int seasons = 0, episodes = 0, failed = 0, unmatched = 0;
        var groups = library.All().Where(x => x.Available && x.Series is not null).GroupBy(x => (x.Kind, x.Series)).ToArray();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            // Newly scanned episodes have no timestamp and trigger a refresh for their show.
            if (!force && group.All(x => x.MetadataProvider == provider && x.MetadataFetchedAt > Wire.Now - (long)TimeSpan.FromDays(7).TotalMilliseconds)) continue;
            var entries = group.ToArray(); var item = entries[0];
            progress?.Report("Fetching seasons and episodes · " + item.DisplayTitle);
            try
            {
                var id = entries.FirstOrDefault(x => x.MetadataProvider == provider && x.MetadataId is > 0)?.MetadataId;
                // Reuse earlier manual matches, including libraries created before provider IDs were saved.
                if (id is null)
                {
                    var pattern = provider == "tmdb" ? @"^https://www\.themoviedb\.org/tv/(\d+)" : @"^https://www\.tvmaze\.com/shows/(\d+)";
                    var source = entries.Select(x => Regex.Match(x.PosterSource ?? "", pattern)).FirstOrDefault(x => x.Success);
                    if (source is not null && int.TryParse(source.Groups[1].Value, out var knownId)) id = knownId;
                }
                if (id is null && tmdb is not null)
                {
                    var matches = (await tmdb.SearchAsync(item.DisplayTitle, true, ct)).Where(x => Normalize(x.Title) == Normalize(item.DisplayTitle)
                        && (item.Year is null || x.Year == item.Year.ToString())).ToArray();
                    if (matches.Length == 1) id = matches[0].Id;
                }
                if (id is null && tmdb is null)
                {
                    id = await ResolveTvmaze(http, item, ct);
                    if (id is null && item.Kind == "Anime")
                        foreach (var alias in await AnimeTitles.AliasesAsync(http, directory, item.DisplayTitle, ct))
                        {
                            await Task.Delay(550, ct);
                            id = await ResolveTvmaze(http, item with { Series = alias, Title = alias }, ct);
                            if (id is not null) break;
                        }
                }
                if (id is null) { unmatched++; continue; }
                foreach (var entry in entries) library.Save(entry with { MetadataProvider = provider, MetadataId = id });

                if (tmdb is not null)
                {
                    foreach (var seasonGroup in entries.GroupBy(x => x.Season ?? 1))
                    {
                        ct.ThrowIfCancellationRequested();
                        var details = await tmdb.SeasonAsync(id.Value, seasonGroup.Key, ct);
                        if (details is null) continue;
                        seasons++;
                        foreach (var entry in seasonGroup)
                        {
                            var episode = details.Episodes.SingleOrDefault(x => x.Number == entry.Episode);
                            var current = entry with { MetadataProvider = provider, MetadataId = id,
                                SeasonTitle = Nonempty(details.Title, entry.SeasonTitle), SeasonOverview = Nonempty(details.Overview, entry.SeasonOverview),
                                SeasonSource = $"https://www.themoviedb.org/tv/{id}/season/{seasonGroup.Key}",
                                EpisodeTitle = Nonempty(episode?.Title, entry.EpisodeTitle), EpisodeOverview = Nonempty(episode?.Overview, entry.EpisodeOverview),
                                AirDate = Nonempty(episode?.AirDate, entry.AirDate), RuntimeMinutes = episode?.Runtime ?? entry.RuntimeMinutes,
                                EpisodeSource = episode is null ? entry.EpisodeSource : $"https://www.themoviedb.org/tv/{id}/season/{seasonGroup.Key}/episode/{episode.Number}" };
                            library.Save(current); // Keep text even if the image provider fails.
                            if (!File.Exists(current.SeasonPoster)) current = current with { SeasonPoster = await tmdb.CacheImageAsync(details.Image, $"tv-{id}-s{seasonGroup.Key}", Path.Combine(directory, "posters"), ct) ?? current.SeasonPoster };
                            if (episode is not null && !File.Exists(current.EpisodePoster)) current = current with { EpisodePoster = await tmdb.CacheImageAsync(episode.Image, $"tv-{id}-s{seasonGroup.Key}-e{episode.Number}", Path.Combine(directory, "posters"), ct) ?? current.EpisodePoster };
                            library.Save(current with { MetadataFetchedAt = Wire.Now });
                            if (episode is not null) episodes++;
                        }
                        await Task.Delay(150, ct);
                    }
                }
                else
                {
                    using var seasonJson = await Json(http, $"https://api.tvmaze.com/shows/{id}/seasons", ct);
                    using var episodeJson = await Json(http, $"https://api.tvmaze.com/shows/{id}/episodes?specials=1", ct);
                    var seasonList = seasonJson.RootElement.EnumerateArray().ToArray();
                    var episodeList = episodeJson.RootElement.EnumerateArray().ToArray();
                    foreach (var seasonGroup in entries.GroupBy(x => x.Season ?? 1))
                    {
                        var season = seasonList.SingleOrDefault(x => Number(x, "number") == seasonGroup.Key);
                        if (season.ValueKind != JsonValueKind.Undefined) seasons++;
                        foreach (var entry in seasonGroup)
                        {
                            // Skip ambiguous or unnumbered specials instead of matching the wrong file.
                            var matches = episodeList.Where(x => Number(x, "season") == (entry.Season ?? 1) && entry.Episode is not null && Number(x, "number") == entry.Episode).ToArray();
                            var episode = matches.Length == 1 ? matches[0] : default;
                            var hasSeason = season.ValueKind != JsonValueKind.Undefined;
                            var hasEpisode = episode.ValueKind != JsonValueKind.Undefined;
                            var current = entry with { MetadataProvider = provider, MetadataId = id,
                                SeasonTitle = hasSeason ? Nonempty(Text(season, "name"), entry.SeasonTitle) : entry.SeasonTitle,
                                SeasonOverview = hasSeason ? Nonempty(Plain(Text(season, "summary")), entry.SeasonOverview) : entry.SeasonOverview,
                                SeasonSource = hasSeason ? Text(season, "url") : entry.SeasonSource,
                                EpisodeTitle = hasEpisode ? Nonempty(Text(episode, "name"), entry.EpisodeTitle) : entry.EpisodeTitle,
                                EpisodeOverview = hasEpisode ? Nonempty(Plain(Text(episode, "summary")), entry.EpisodeOverview) : entry.EpisodeOverview,
                                EpisodeSource = hasEpisode ? Text(episode, "url") : entry.EpisodeSource,
                                AirDate = hasEpisode ? Nonempty(Text(episode, "airdate"), entry.AirDate) : entry.AirDate,
                                RuntimeMinutes = hasEpisode ? Number(episode, "runtime") ?? entry.RuntimeMinutes : entry.RuntimeMinutes };
                            library.Save(current);
                            if (hasSeason && !File.Exists(current.SeasonPoster) && Image(season) is { } seasonImage)
                                current = current with { SeasonPoster = await AutomaticArtwork.Download(http, seasonImage, directory, ct) };
                            if (hasEpisode && !File.Exists(current.EpisodePoster) && Image(episode) is { } episodeImage)
                                current = current with { EpisodePoster = await AutomaticArtwork.Download(http, episodeImage, directory, ct) };
                            library.Save(current with { MetadataFetchedAt = Wire.Now });
                            if (hasEpisode) episodes++;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            { failed++; progress?.Report("Season/episode metadata unavailable · " + item.DisplayTitle); }
            await Task.Delay(550, ct);
        }
        return $"Seasons: {seasons} · Episodes: {episodes} · {unmatched} shows need matching · {failed} metadata failures.";
    }
    private static async Task<int?> ResolveTvmaze(HttpClient http, MediaItem item, CancellationToken ct)
    {
        using var json = await Json(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle), ct);
        var matches = json.RootElement.EnumerateArray().Select(x => x.GetProperty("show")).Where(x => Normalize(Text(x, "name") ?? "") == Normalize(item.DisplayTitle)
            && (item.Year is null || Text(x, "premiered")?.StartsWith(item.Year.ToString()!) == true)).ToArray();
        return matches.Length == 1 ? Number(matches[0], "id") : null;
    }
}
