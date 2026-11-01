using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

public static class SeriesMetadata
{
    private const string MissingChecked = "missing-episodes-checked";
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static int? Number(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : null;
    private static string? Plain(string? html) => html is null ? null : WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim();
    private static string? Image(JsonElement value) => value.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object ? Text(image, "medium") ?? Text(image, "original") : null;
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await MetadataHttp.GetString(http, url, ct));

    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress,
        CancellationToken ct, HttpClient http, bool force = false, MetadataClient? metadata = null, MetadataRefresh? refresh = null)
    {
        var options = MetadataOptions.Load(library); var mode = refresh ?? (force ? MetadataRefresh.RefreshText : MetadataRefresh.FillMissing);
        using var issueScope = MetadataIssues.Begin(library); var outstanding = issueScope.State;
        using var requests = MetadataHttp.BeginScope(library, force || refresh is not null, options);
        using var owned = metadata is null && TmdbCredentials.Resolve(token) is not null ? new MetadataClient(token, options: options) : null;
        var tmdb = metadata ?? owned;
        // Albums are not shows: their tracks never look up show metadata.
        var catalog = library.All().Where(x => !x.IsMusic).ToList(); var catalogChanged = false;
        foreach (var compilation in catalog.Where(x => mode != MetadataRefresh.RetryArtwork && x.Series is not null && MetadataTitles.IsUnverifiedCompilation(x)))
        {
            catalogChanged = true;
            library.Save(compilation with { EpisodeTitle = LibraryIdentity.Locked(compilation, "EpisodeTitle") ? compilation.EpisodeTitle : null,
                EpisodeOverview = LibraryIdentity.Locked(compilation, "EpisodeOverview") ? compilation.EpisodeOverview : null,
                EpisodePoster = LibraryIdentity.Locked(compilation, "EpisodePoster") ? compilation.EpisodePoster : null,
                AirDate = LibraryIdentity.Locked(compilation, "AirDate") ? compilation.AirDate : null, EpisodeSource = null,
                NumberingConflict = "Compilation file numbers are not verified episode numbers; identify the contained episode range before fetching episode details." });
        }
        // Every show's placeholders are redone when the missing or upcoming setting changes, however fresh its metadata,
        // and once for placeholders made before missing episodes were checked against the files' numbering.
        var missingState = $"2:{options.ImportMissing}:{options.ImportUpcoming}";
        var recheck = mode != MetadataRefresh.RetryArtwork && library.Setting(MissingChecked) != missingState;
        var rechecking = recheck && (options.ImportMissing || options.ImportUpcoming);
        if (mode != MetadataRefresh.RetryArtwork)
        {
            // Wanted titles keep their placeholders whatever the missing-episode settings say; other shows don't get specials.
            var wanted = library.Wanted().Select(x => x.Id).ToHashSet();
            var stale = catalog.Where(x => x.IsVirtual && !wanted.Contains(LibraryIdentity.ShowKey(x)) && (x.Season == 0 ||
                (DateOnly.TryParse(x.AirDate, out var date) && date > DateOnly.FromDateTime(DateTime.UtcNow) ? !options.ImportUpcoming : !options.ImportMissing))).Select(x => x.Id).ToArray();
            library.RemoveVirtual(stale); catalogChanged |= stale.Length > 0;
        }
        int seasons = 0, episodes = 0, failed = 0, unmatched = 0, incomplete = 0, artworkFailed = 0, deferred = 0;
        var failedImages = new Dictionary<string, Exception>();
        async Task<(string? Path, bool Failed)> Artwork(MediaItem entry, string stage, string image, Func<Task<string?>> fetch)
        {
            Exception? failure = failedImages.GetValueOrDefault(image);
            if (failure is null)
            {
                try { var path = await fetch(); outstanding.Clear(stage, entry); return (path, false); }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
                { failure = ex; failedImages[image] = ex; artworkFailed++; MetadataDiagnostics.Failure(stage, entry.DisplayTitle, null, ex); }
            }
            if (library.ByIds([entry.Id]).FirstOrDefault() is { } latest && AutomaticArtwork.SameFetchIdentity(entry, latest))
                outstanding.Set(stage, entry, "artwork-failure", AutomaticArtwork.FailureReason(failure), failure);
            return (null, true);
        }
        var groups = (catalogChanged ? library.All() : catalog).Where(x => !x.IsMusic).Where(x => x.Available && !x.IsVirtual && !x.IsExtra && !x.IsBonus && x.Series is not null && !x.MetadataLocked && !MetadataTitles.IsUnverifiedCompilation(x) &&
            (x.Matched || !MetadataMatching.HasMovieEvidence(x))).GroupBy(LibraryIdentity.ShowKey).ToArray();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (mode == MetadataRefresh.FillMissing && !rechecking && group.All(x => x.MetadataFetchedAt > 0 &&
                (options.RefreshDays <= 0 || x.MetadataFetchedAt > Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds) &&
                (!force && refresh is null || x.Episode is null || !string.IsNullOrWhiteSpace(x.EpisodeTitle)) &&
                (x.SeasonPoster is null || ArtworkCache.IsUsable(x.SeasonPoster)) &&
                (x.EpisodePoster is null || ArtworkCache.IsUsable(x.EpisodePoster)))) continue;
            var entries = group.ToArray(); var item = entries[0];
            if (!force && refresh is null && !rechecking && MetadataFetchPolicy.Deferred(library, "episodes", entries, options, token))
            { deferred++; MetadataDiagnostics.Record("metadata-fetch-deferred", new { Stage = "episodes", item.Title, RetryAt = MetadataFetchPolicy.RetryAt(library, "episodes", item.Id) }); continue; }
            if (mode == MetadataRefresh.RetryArtwork && entries.All(x => x.MetadataId is null && (x.ProviderIds?.Count ?? 0) == 0)) continue;
            var replaceText = mode != MetadataRefresh.RetryArtwork && (mode != MetadataRefresh.FillMissing || refresh is null && options.RefreshDays > 0 && entries.Any(x =>
                x.MetadataFetchedAt > 0 && x.MetadataFetchedAt < Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds));
            progress?.Report("Fetching seasons and episodes · " + item.DisplayTitle);
            using var context = MetadataDiagnostics.BeginItem("season-episode", item);
            MetadataDiagnostics.Record("metadata-episodes-start", new { item.NumberingOrder, Entries = entries.Length });
            var matched = false; var attemptFailed = false; var superseded = false; var groupArtworkFailed = false; Exception? providerFailure = null;
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
                        if (mode == MetadataRefresh.RetryArtwork) continue;
                        var resolved = await AutomaticArtwork.ResolveAsync(http, tmdb, item, options with { Providers = [provider] }, directory, ct, entries);
                        id = resolved?.Id;
                    }
                    if (id is null) continue;
                    MetadataSeason[] details;
                    if (provider == "tmdb")
                    {
                        if (item.NumberingOrder == "dvd") details = await tmdb!.DvdSeasonsAsync(id.Value, ct);
                        else if (entries.Any(x => x.NumberingOrder == "absolute" || x.AbsoluteEpisode is not null || x.NumberingSource == "date") || options.ImportMissing || options.ImportUpcoming)
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
                    if (mode != MetadataRefresh.RetryArtwork) outstanding.Clear("episodes", item);
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
                        var beforeDetails = latest[entry.Id];
                        ct.ThrowIfCancellationRequested();
                        var current = TmdbRetention.Record(entry, LibraryIdentity.WithProvider(entry, provider, id, !entry.Matched || entry.MetadataProvider == provider), provider, "metadataProvider", "metadataId");
                        if (entry.NumberingConflict is FilenameEpisodes.InvalidRange or FilenameEpisodes.CrossSeasonRange)
                        { library.Save(current); continue; }
                        var dateNumbering = entry.NumberingSource == "date" && entry.AirDate is not null;
                        if (mode != MetadataRefresh.RetryArtwork && dateNumbering && !LibraryIdentity.Locked(current, "Season") && !LibraryIdentity.Locked(current, "Episode"))
                        {
                            current = current with { Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null,
                                SourcePart = null, SourcePartEpisode = null, SourcePartEpisodeEnd = null, NumberingOrder = "aired" };
                            var dated = details.SelectMany(s => s.Episodes.Where(e => e.AirDate == entry.AirDate).Select(e => (Season: s.Number, Episode: e.Number))).ToArray();
                            if (dated.Length != 1)
                            {
                                library.Save(current with { NumberingConflict = "The filename air date does not identify exactly one provider episode; correct its numbering manually." });
                                continue;
                            }
                            current = current with { Season = dated[0].Season, Episode = dated[0].Episode, NumberingConflict = null };
                        }
                        var absoluteNumber = mode == MetadataRefresh.RetryArtwork || dateNumbering ? null : entry.AbsoluteEpisode ?? (entry.NumberingOrder == "absolute" ? entry.Episode : null);
                        int? mappedPartEnd = null;
                        if (mode != MetadataRefresh.RetryArtwork && !dateNumbering && entry.SourcePart is > 1 && entry.SourcePartEpisode is { } localNumber)
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
                        string? Merge(string field, string? value, string? old) => mode == MetadataRefresh.RetryArtwork || LibraryIdentity.Locked(current, field) || string.IsNullOrWhiteSpace(value) ? old :
                            !replaceText && !string.IsNullOrWhiteSpace(old) ? old : value;
                        if (season is null) { library.Save(current); continue; }
                        current = current with
                        {
                            SeasonTitle = Merge("SeasonTitle", season.Title, current.SeasonTitle), SeasonOverview = Merge("SeasonOverview", season.Overview, current.SeasonOverview),
                            EpisodeTitle = Merge("EpisodeTitle", string.Join(" / ", episodeList.Select(e => e.Title).Where(t => !string.IsNullOrWhiteSpace(t))), current.EpisodeTitle),
                            EpisodeOverview = Merge("EpisodeOverview", string.Join("\n\n", episodeList.Select(e => e.Overview).Where(t => !string.IsNullOrWhiteSpace(t))), current.EpisodeOverview),
                            AirDate = Merge("AirDate", first?.AirDate, current.AirDate),
                            RuntimeMinutes = mode == MetadataRefresh.RetryArtwork || LibraryIdentity.Locked(current, "RuntimeMinutes") ? current.RuntimeMinutes :
                                episodeList.Any(e => e.Runtime is not null) ? episodeList.Sum(e => e.Runtime ?? 0) : current.RuntimeMinutes,
                            SeasonSource = provider == "tmdb" ? $"https://www.themoviedb.org/tv/{id}/season/{season.Number}" : $"https://www.tvmaze.com/shows/{id}",
                            EpisodeSource = first is null ? current.EpisodeSource : provider == "tmdb" ? $"https://www.themoviedb.org/tv/{id}/season/{season.Number}/episode/{first.Number}" : $"https://www.tvmaze.com/shows/{id}"
                        };
                        current = TmdbRetention.Record(beforeDetails, current, provider, TmdbRetention.EpisodeFields);
                        library.Save(current);
                        var replace = mode == MetadataRefresh.ReplaceArtwork;
                        var entryArtworkFailed = false;
                        if (!LibraryIdentity.Locked(current, "SeasonPoster") && (replace || !ArtworkCache.IsUsable(current.SeasonPoster)) && season.Image is { } seasonImage)
                        {
                            var result = await Artwork(current, "season-artwork", seasonImage, async () => provider == "tmdb" ? await tmdb!.CacheImageAsync(seasonImage, $"tv-{id}-s{season.Number}", Path.Combine(directory, "posters"), ct, replace)
                                : await AutomaticArtwork.Download(http, seasonImage, directory, ct, replace));
                            var afterImage = library.ByIds([current.Id]).FirstOrDefault();
                            if (afterImage is null || !AutomaticArtwork.SameFetchIdentity(current, afterImage)) { superseded = true; continue; }
                            current = afterImage;
                            current = TmdbRetention.Record(current, current with { SeasonPoster = result.Path ?? current.SeasonPoster }, provider, "seasonPoster"); entryArtworkFailed |= result.Failed;
                        }
                        library.Save(current);
                        if (!LibraryIdentity.Locked(current, "EpisodePoster") && (replace || !ArtworkCache.IsUsable(current.EpisodePoster)) && first?.Image is { } episodeImage)
                        {
                            var result = await Artwork(current, "episode-artwork", episodeImage, async () => provider == "tmdb" ? await tmdb!.CacheImageAsync(episodeImage, $"tv-{id}-s{season.Number}-e{first.Number}", Path.Combine(directory, "posters"), ct, replace, "w780")
                                : await AutomaticArtwork.Download(http, episodeImage, directory, ct, replace));
                            var afterImage = library.ByIds([current.Id]).FirstOrDefault();
                            if (afterImage is null || !AutomaticArtwork.SameFetchIdentity(current, afterImage)) { superseded = true; continue; }
                            current = afterImage;
                            current = TmdbRetention.Record(current, current with { EpisodePoster = result.Path ?? current.EpisodePoster }, provider, "episodePoster"); entryArtworkFailed |= result.Failed;
                        }
                        groupArtworkFailed |= entryArtworkFailed;
                        library.Save(current with { MetadataFetchedAt = entryArtworkFailed ? 0 : Wire.Now });
                        if (options.SaveNfo) LocalMetadata.Export(current);
                        episodes += episodeList.Length;
                    }
                    matched = true;
                    seasons += details.Count(s => entries.Any(e => e.Season == s.Number));
                    if (mode != MetadataRefresh.RetryArtwork) ReconcileMissing(library, directory, item, details, provider, id.Value, options);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException && !ct.IsCancellationRequested)
                { MetadataDiagnostics.Failure("season-episode", item.DisplayTitle, provider, ex); providerFailure = ex; attemptFailed = true; failed++; progress?.Report(provider + " season/episode metadata unavailable · " + item.DisplayTitle); }
            }
            if (!matched)
            {
                var knownIdentity = item.MetadataId is > 0 || (item.ProviderIds?.Count ?? 0) > 0;
                if (knownIdentity) incomplete++; else unmatched++;
                outstanding.Set(mode == MetadataRefresh.RetryArtwork ? "episode-artwork" : "episodes", item, attemptFailed ? "provider-failure" : knownIdentity ? "episode-metadata-unavailable" : MetadataDiagnostics.MatchOutcome ?? "no-confident-match", error: providerFailure);
            }
            if (!superseded)
            {
                var ids = entries.Select(x => x.Id).ToHashSet();
                MetadataFetchPolicy.Record(library, "episodes", library.ByIds(ids), options, token, attemptFailed && !matched || groupArtworkFailed, !matched);
            }
        }
        if (recheck) library.Setting(MissingChecked, missingState);
        return $"Seasons: {seasons} · Episodes: {episodes} · {unmatched} shows need matching · {incomplete} shows missing episode metadata · {failed} metadata failures · {artworkFailed} artwork failures · {deferred} shows deferred.";
    }
    internal static async Task<MetadataSeason[]> TvmazeSeasons(HttpClient http, int id, CancellationToken ct)
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
    internal static void ReconcileMissing(LibraryStore library, string directory, MediaItem show, MetadataSeason[] seasons, string provider, int id, MetadataOptions options)
    {
        // A wanted show follows its tracked seasons instead.
        if (library.Wanted(LibraryIdentity.ShowKey(show)) is { } tracked) { WantedTitles.Reconcile(library, directory, tracked, tracked.Provider == provider ? seasons : null); return; }
        var existing = library.InShow(show).ToArray();
        var files = existing.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus).ToArray();
        var physical = files.SelectMany(x => LibraryIdentity.EpisodeNumbers(x).Select(n => (x.Season, Number: n))).ToHashSet();
        var listed = seasons.SelectMany(s => s.Episodes.Select(e => ((int?)s.Number, e.Number))).ToHashSet();
        // Files numbered another way than this provider's list (another provider's seasons, numbers running on across
        // seasons, a wrong match) can't tell what is missing, so a show only gets placeholders when at most one file, or 5% of them, doesn't fit.
        var regular = files.Where(x => x.Season is not 0).ToArray();
        var misfits = regular.Count(x => x.Episode is null || LibraryIdentity.EpisodeNumbers(x).Any(n => !listed.Contains((x.Season, n))));
        var fits = !files.Any(x => x.MetadataProvider is { } other && other != provider) && misfits <= Math.Max(1, regular.Length / 20);
        // A file whose number the scan couldn't read, such as "One Piece - 492A", still holds the episode its name gives,
        // and a provider listing one episode in two seasons has it once the file of either is here.
        var unread = files.Where(x => x.Episode is null).SelectMany(x => Regex.Matches(Path.GetFileNameWithoutExtension(x.Path), @"(?<![\d.])\d{2,4}(?=[ab]?(?![\d.p]))")
            .Select(m => int.Parse(m.Value))).ToHashSet();
        var aired = files.Where(x => x.AirDate is not null && !string.IsNullOrWhiteSpace(x.EpisodeTitle)).Select(x => (x.AirDate, x.EpisodeTitle)).ToHashSet();
        var wanted = new HashSet<string>();
        // Specials are left out: providers list promos, shorts and featurettes there that few libraries keep.
        foreach (var season in seasons.Where(s => fits && s.Number > 0)) foreach (var episode in season.Episodes)
        {
            if (physical.Contains(((int?)season.Number, episode.Number)) || unread.Contains(episode.Number) || aired.Contains((episode.AirDate, episode.Title)) ||
                !DateOnly.TryParse(episode.AirDate, out var date)) continue;
            var upcoming = date > DateOnly.FromDateTime(DateTime.UtcNow);
            if (upcoming ? !options.ImportUpcoming : !options.ImportMissing) continue;
            var key = "missing-" + LibraryIdentity.Hash($"{LibraryIdentity.ShowKey(show)}:{season.Number}:{episode.Number}"); wanted.Add(key);
            var placeholder = LibraryIdentity.WithProvider(show with { Id = key, Path = Path.Combine(directory, ".missing", key), Available = false, IsVirtual = true,
                Season = season.Number, Episode = episode.Number, EpisodeEnd = null, AbsoluteEpisode = null, EpisodeTitle = episode.Title,
                EpisodeOverview = episode.Overview, SeasonTitle = season.Title, SeasonOverview = season.Overview, EpisodePoster = null,
                AirDate = episode.AirDate, RuntimeMinutes = episode.Runtime, MetadataFetchedAt = Wire.Now }, provider, id, false);
            library.Save(TmdbRetention.Record(show, placeholder, provider, TmdbRetention.EpisodeFields));
        }
        library.RemoveVirtual(existing.Where(x => x.IsVirtual && !wanted.Contains(x.Id)).Select(x => x.Id));
    }
}
