using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public static class SeriesMetadata
{
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
    private static string? Plain(string? html) => html is null ? null : WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim();
    private static string? Image(JsonElement value) => value.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "original") ?? Text(image, "medium") : null;
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await MetadataHttp.GetString(http, url, ct));

    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress,
        CancellationToken ct, HttpClient http, bool force = false, MetadataClient? metadata = null, MetadataRefresh? refresh = null)
    {
        var options = MetadataOptions.Load(library); var mode = refresh ?? (force ? MetadataRefresh.RefreshText : MetadataRefresh.FillMissing);
        using var requests = MetadataHttp.BeginScope(library, force || refresh is not null, options);
        using var owned = metadata is null && !string.IsNullOrWhiteSpace(token) ? new MetadataClient(token, options: options) : null;
        var tmdb = metadata ?? owned;
        foreach (var compilation in library.All().Where(x => x.Series is not null && MetadataTitles.IsUnverifiedCompilation(x)))
            library.Save(compilation with { EpisodeTitle = LibraryIdentity.Locked(compilation, "EpisodeTitle") ? compilation.EpisodeTitle : null,
                EpisodeOverview = LibraryIdentity.Locked(compilation, "EpisodeOverview") ? compilation.EpisodeOverview : null,
                EpisodePoster = LibraryIdentity.Locked(compilation, "EpisodePoster") ? compilation.EpisodePoster : null,
                AirDate = LibraryIdentity.Locked(compilation, "AirDate") ? compilation.AirDate : null, EpisodeSource = null,
                NumberingConflict = "Compilation file numbers are not verified episode numbers; identify the contained episode range before fetching episode details." });
        library.RemoveVirtual(library.All().Where(x => x.IsVirtual &&
            (DateOnly.TryParse(x.AirDate, out var date) && date > DateOnly.FromDateTime(DateTime.UtcNow) ? !options.ImportUpcoming : !options.ImportMissing)).Select(x => x.Id));
        int seasons = 0, episodes = 0, failed = 0, unmatched = 0;
        var groups = library.All().Where(x => x.Available && !x.IsVirtual && !x.IsExtra && x.Series is not null && !x.MetadataLocked && !MetadataTitles.IsUnverifiedCompilation(x) &&
            (x.Matched || !MetadataMatching.HasMovieEvidence(x))).GroupBy(LibraryIdentity.ShowKey).ToArray();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (mode == MetadataRefresh.FillMissing && group.All(x => x.MetadataFetchedAt > 0 &&
                (options.RefreshDays <= 0 || x.MetadataFetchedAt > Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds) &&
                (!force && refresh is null || x.Episode is null || !string.IsNullOrWhiteSpace(x.EpisodeTitle)) &&
                (x.SeasonPoster is null || ArtworkCache.IsUsable(x.SeasonPoster)) &&
                (x.EpisodePoster is null || ArtworkCache.IsUsable(x.EpisodePoster)))) continue;
            var entries = group.ToArray(); var item = entries[0];
            if (!force && refresh is null && MetadataFetchPolicy.Deferred(library, "episodes", entries, options, token)) continue;
            var replaceText = mode != MetadataRefresh.FillMissing || refresh is null && options.RefreshDays > 0 && entries.Any(x =>
                x.MetadataFetchedAt > 0 && x.MetadataFetchedAt < Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds);
            progress?.Report("Fetching seasons and episodes · " + item.DisplayTitle);
            var matched = false; var attemptFailed = false; var superseded = false;
            foreach (var provider in options.ProviderOrder.Where(p => p is "tmdb" or "tvmaze"))
            {
                if (provider == "tmdb" && tmdb is null || provider == "tvmaze" && item.NumberingOrder == "dvd") continue;
                try
                {
                    int? id = int.TryParse(LibraryIdentity.ProviderId(item, provider), out var stored) && stored > 0 ? stored : null;
                    if (id is null)
                    {
                        var source = Regex.Match(item.PosterSource ?? "", provider == "tmdb" ? @"themoviedb\.org/tv/(\d+)" : @"tvmaze\.com/shows/(\d+)");
                        if (source.Success) id = int.Parse(source.Groups[1].Value);
                    }
                    if (id is null || !item.Matched && MetadataTitles.IsAnimeSource(item))
                    {
                        var resolved = await AutomaticArtwork.ResolveAsync(http, tmdb, item, options with { Providers = [provider] }, directory, ct, entries);
                        id = resolved?.Id;
                    }
                    if (id is null) continue;
                    MetadataSeason[] details;
                    if (provider == "tmdb")
                    {
                        if (item.NumberingOrder == "dvd") details = await tmdb!.DvdSeasonsAsync(id.Value, ct);
                        else if (entries.Any(x => x.NumberingOrder == "absolute" || x.AbsoluteEpisode is not null) || options.ImportMissing || options.ImportUpcoming)
                            details = await tmdb!.SeasonsAsync(id.Value, ct);
                        else
                        {
                            var result = new List<MetadataSeason>();
                            foreach (var number in entries.Select(x => x.Season).OfType<int>().Distinct())
                                if (await tmdb!.SeasonAsync(id.Value, number, ct) is { } season) result.Add(season);
                            details = result.ToArray();
                        }
                    }
                    else details = await TvmazeSeasons(http, id.Value, ct);
                    if (details.Length == 0) continue;
                    var entryIds = entries.Select(x => x.Id).ToHashSet();
                    var latest = library.ByIds(entryIds).ToDictionary(x => x.Id);
                    if (entries.Any(x => !latest.TryGetValue(x.Id, out var updated) || !AutomaticArtwork.SameFetchIdentity(x, updated)))
                    { progress?.Report("Kept newer numbering/identity · " + item.DisplayTitle); superseded = true; matched = true; break; }
                    entries = entries.Select(x => latest[x.Id]).ToArray();
                    var absolute = details.Where(s => s.Number > 0).OrderBy(s => s.Number)
                        .SelectMany(s => s.Episodes.OrderBy(e => e.Number).Select(e => (Season: s, Episode: e))).ToArray();
                    var partStarts = new List<int> { 0 };
                    for (var index = 1; index < absolute.Length; index++)
                        if (absolute[index].Season.Number != absolute[index - 1].Season.Number ||
                            DateOnly.TryParse(absolute[index].Episode.AirDate, out var nextDate) &&
                            DateOnly.TryParse(absolute[index - 1].Episode.AirDate, out var priorDate) && nextDate.DayNumber - priorDate.DayNumber >= 42)
                            partStarts.Add(index);
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var current = LibraryIdentity.WithProvider(entry, provider, id, !entry.Matched || entry.MetadataProvider == provider);
                        var absoluteNumber = entry.AbsoluteEpisode ?? (entry.NumberingOrder == "absolute" ? entry.Episode : null);
                        int? mappedPartEnd = null;
                        if (entry.SourcePart is > 1 && entry.SourcePartEpisode is { } localNumber)
                        {
                            var part = entry.SourcePart.Value;
                            var expectedParts = entries.Select(x => x.SourcePart ?? 1).Max();
                            var start = part <= partStarts.Count ? partStarts[part - 1] : -1;
                            var end = part < partStarts.Count ? partStarts[part] : absolute.Length;
                            if (partStarts.Count != expectedParts || start < 0 || localNumber <= 0 || start + (entry.SourcePartEpisodeEnd ?? localNumber) > end)
                            {
                                library.Save(current with { NumberingConflict = "Part-relative numbering needs an unambiguous provider season or broadcast break; correct numbering manually if this part cannot be resolved." });
                                continue;
                            }
                            absoluteNumber = start + localNumber;
                            mappedPartEnd = entry.SourcePartEpisodeEnd is { } localEnd ? start + localEnd : null;
                        }
                        if (absoluteNumber is > 0 && absoluteNumber <= absolute.Length && !LibraryIdentity.Locked(current, "Season") && !LibraryIdentity.Locked(current, "Episode"))
                        {
                            var mapping = absolute[absoluteNumber.Value - 1];
                            var absoluteEnd = entry.SourcePart is > 1 ? mappedPartEnd : entry.AbsoluteEpisodeEnd ?? entry.EpisodeEnd;
                            if (absoluteEnd > absoluteNumber && (absoluteEnd > absolute.Length || absolute[absoluteEnd.Value - 1].Season.Number != mapping.Season.Number))
                            { library.Save(current with { NumberingConflict = "This absolute episode range spans provider seasons; split the file or correct its numbering before matching." }); continue; }
                            current = current with { Season = mapping.Season.Number, Episode = mapping.Episode.Number, AbsoluteEpisode = absoluteNumber,
                                AbsoluteEpisodeEnd = absoluteEnd, EpisodeEnd = absoluteEnd > absoluteNumber ? absolute[absoluteEnd.Value - 1].Episode.Number : null };
                            current = current with { NumberingConflict = null };
                        }
                        else if (absoluteNumber is not null) continue;
                        var season = details.SingleOrDefault(s => s.Number == current.Season);
                        var episodeList = season?.Episodes.Where(e => LibraryIdentity.EpisodeNumbers(current).Contains(e.Number)).OrderBy(e => e.Number).ToArray() ?? [];
                        var first = episodeList.FirstOrDefault();
                        string? Merge(string field, string? value, string? old) => LibraryIdentity.Locked(current, field) || string.IsNullOrWhiteSpace(value) ? old :
                            !replaceText && !string.IsNullOrWhiteSpace(old) ? old : value;
                        if (season is null) { library.Save(current); continue; }
                        current = current with
                        {
                            SeasonTitle = Merge("SeasonTitle", season.Title, current.SeasonTitle), SeasonOverview = Merge("SeasonOverview", season.Overview, current.SeasonOverview),
                            EpisodeTitle = Merge("EpisodeTitle", string.Join(" / ", episodeList.Select(e => e.Title).Where(t => !string.IsNullOrWhiteSpace(t))), current.EpisodeTitle),
                            EpisodeOverview = Merge("EpisodeOverview", string.Join("\n\n", episodeList.Select(e => e.Overview).Where(t => !string.IsNullOrWhiteSpace(t))), current.EpisodeOverview),
                            AirDate = Merge("AirDate", first?.AirDate, current.AirDate),
                            RuntimeMinutes = LibraryIdentity.Locked(current, "RuntimeMinutes") ? current.RuntimeMinutes :
                                episodeList.Any(e => e.Runtime is not null) ? episodeList.Sum(e => e.Runtime ?? 0) : current.RuntimeMinutes,
                            SeasonSource = provider == "tmdb" ? $"https://www.themoviedb.org/tv/{id}/season/{season.Number}" : $"https://www.tvmaze.com/shows/{id}",
                            EpisodeSource = first is null ? current.EpisodeSource : provider == "tmdb" ? $"https://www.themoviedb.org/tv/{id}/season/{season.Number}/episode/{first.Number}" : $"https://www.tvmaze.com/shows/{id}"
                        };
                        library.Save(current);
                        var replace = mode == MetadataRefresh.ReplaceArtwork;
                        if (!LibraryIdentity.Locked(current, "SeasonPoster") && (replace || !ArtworkCache.IsUsable(current.SeasonPoster)) && season.Image is { } seasonImage)
                            current = current with { SeasonPoster = provider == "tmdb" ? await tmdb!.CacheImageAsync(seasonImage, $"tv-{id}-s{season.Number}", Path.Combine(directory, "posters"), ct, replace)
                                : await AutomaticArtwork.Download(http, seasonImage, directory, ct, replace) };
                        library.Save(current);
                        if (!LibraryIdentity.Locked(current, "EpisodePoster") && (replace || !ArtworkCache.IsUsable(current.EpisodePoster)) && first?.Image is { } episodeImage)
                            current = current with { EpisodePoster = provider == "tmdb" ? await tmdb!.CacheImageAsync(episodeImage, $"tv-{id}-s{season.Number}-e{first.Number}", Path.Combine(directory, "posters"), ct, replace, "w780")
                                : await AutomaticArtwork.Download(http, episodeImage, directory, ct, replace) };
                        library.Save(current with { MetadataFetchedAt = Wire.Now });
                        if (options.SaveNfo) LocalMetadata.Export(current);
                        episodes += episodeList.Length;
                    }
                    matched = true;
                    seasons += details.Count(s => entries.Any(e => e.Season == s.Number));
                    ReconcileMissing(library, directory, item, details, provider, id.Value, options);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException && !ct.IsCancellationRequested)
                { attemptFailed = true; failed++; progress?.Report(provider + " season/episode metadata unavailable · " + item.DisplayTitle); }
            }
            if (!matched) unmatched++;
            if (!superseded)
            {
                var ids = entries.Select(x => x.Id).ToHashSet();
                MetadataFetchPolicy.Record(library, "episodes", library.ByIds(ids), options, token, attemptFailed && !matched, !matched);
            }
        }
        return $"Seasons: {seasons} · Episodes: {episodes} · {unmatched} shows need matching · {failed} metadata failures.";
    }
    private static async Task<MetadataSeason[]> TvmazeSeasons(HttpClient http, int id, CancellationToken ct)
    {
        using var seasonJson = await Json(http, $"https://api.tvmaze.com/shows/{id}/seasons", ct);
        using var episodeJson = await Json(http, $"https://api.tvmaze.com/shows/{id}/episodes?specials=1", ct);
        var episodeList = episodeJson.RootElement.EnumerateArray().ToArray();
        return seasonJson.RootElement.EnumerateArray().Where(s => Number(s, "number") is not null).Select(s => new MetadataSeason(Number(s, "number")!.Value,
            Text(s, "name"), Plain(Text(s, "summary")), Image(s), Text(s, "premiereDate"),
            episodeList.Where(e => Number(e, "season") == Number(s, "number") && Number(e, "number") is not null)
                .GroupBy(e => Number(e, "number")).Where(g => g.Count() == 1).Select(g => g.Single()).Select(e =>
                    new MetadataEpisode(Number(e, "number")!.Value, Text(e, "name"), Plain(Text(e, "summary")), Image(e), Text(e, "airdate"), Number(e, "runtime"))).ToArray())).ToArray();
    }
    private static void ReconcileMissing(LibraryStore library, string directory, MediaItem show, MetadataSeason[] seasons, string provider, int id, MetadataOptions options)
    {
        var existing = library.InShow(show).ToArray();
        var physical = existing.Where(x => !x.IsVirtual).SelectMany(x => LibraryIdentity.EpisodeNumbers(x).Select(n => (x.Season, Number: n))).ToHashSet();
        var wanted = new HashSet<string>();
        foreach (var season in seasons) foreach (var episode in season.Episodes)
        {
            if (physical.Contains(((int?)season.Number, episode.Number)) || !DateOnly.TryParse(episode.AirDate, out var date)) continue;
            var upcoming = date > DateOnly.FromDateTime(DateTime.UtcNow);
            if (upcoming ? !options.ImportUpcoming : !options.ImportMissing) continue;
            var key = "missing-" + LibraryIdentity.Hash($"{LibraryIdentity.ShowKey(show)}:{season.Number}:{episode.Number}"); wanted.Add(key);
            var placeholder = LibraryIdentity.WithProvider(show with { Id = key, Path = Path.Combine(directory, ".missing", key), Available = false, IsVirtual = true,
                Season = season.Number, Episode = episode.Number, EpisodeEnd = null, AbsoluteEpisode = null, EpisodeTitle = episode.Title,
                EpisodeOverview = episode.Overview, SeasonTitle = season.Title, SeasonOverview = season.Overview, EpisodePoster = null,
                AirDate = episode.AirDate, RuntimeMinutes = episode.Runtime, MetadataFetchedAt = Wire.Now }, provider, id, false);
            library.Save(placeholder);
        }
        library.RemoveVirtual(existing.Where(x => x.IsVirtual && !wanted.Contains(x.Id)).Select(x => x.Id));
    }
}
