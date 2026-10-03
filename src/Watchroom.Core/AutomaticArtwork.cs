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
    public static async Task<string> FetchAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress, CancellationToken ct, HttpClient? client = null, bool force = false,
        MetadataRefresh? refresh = null, MetadataClient? metadata = null)
    {
        using var owned = client is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(20) } : null;
        var http = client ?? owned!; http.DefaultRequestHeaders.UserAgent.ParseAdd("Watchroom/0.2 (desktop media library)");
        var options = MetadataOptions.Load(library); var mode = refresh ?? (force ? MetadataRefresh.RefreshText : MetadataRefresh.FillMissing);
        using var ownedTmdb = metadata is null && !string.IsNullOrWhiteSpace(token) ? new MetadataClient(token, options: options) : null;
        var tmdb = metadata ?? ownedTmdb;
        foreach (var entry in library.All().Where(x => x.Available && !x.IsVirtual && !x.IsExtra))
        {
            var local = LocalMetadata.Apply(entry);
            try
            {
                if (!File.Exists(local.Poster) && local.LocalPosterUrl is { } image) local = local with { Poster = await Download(http, image, directory, ct), PosterSource = "Local artwork" };
                if (!File.Exists(local.SeasonPoster) && local.LocalSeasonPosterUrl is { } seasonImage) local = local with { SeasonPoster = await Download(http, seasonImage, directory, ct) };
                if (!File.Exists(local.EpisodePoster) && local.LocalEpisodePosterUrl is { } episodeImage) local = local with { EpisodePoster = await Download(http, episodeImage, directory, ct) };
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { progress?.Report("NFO artwork unavailable · " + entry.DisplayTitle); }
            if (Wire.Serialize(local) != Wire.Serialize(entry)) library.Save(local);
        }
        var groups = library.All().Where(x => x.Available && !x.IsVirtual && !x.IsExtra && !x.MetadataLocked).GroupBy(LibraryIdentity.ShowKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.First().Series is not null).ToArray();
        int fetched = 0, overviews = 0, missing = 0, failed = 0; string? error = null;
        var issues = new List<object>();
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            // Complete cached groups need neither provider lookups nor another
            // full-library read for each title during startup.
            if (mode == MetadataRefresh.FillMissing && group.All(x => x.MetadataKind is not null && x.ArtworkFetchedAt > 0 &&
                !string.IsNullOrWhiteSpace(x.Overview) && (ArtworkCache.IsUsable(x.SeriesPoster) || ArtworkCache.IsUsable(x.Poster)) &&
                (options.RefreshDays <= 0 || x.ArtworkFetchedAt >= Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds))) continue;
            var ids = group.Select(x => x.Id).ToHashSet();
            var entries = library.All().Where(x => ids.Contains(x.Id)).ToArray();
            if (entries.Length == 0 || entries.Select(LibraryIdentity.ShowKey).Distinct().Count() != 1) continue;
            var existingOverview = entries.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Overview))?.Overview;
            if (existingOverview is not null)
                entries = entries.Select(entry =>
                {
                    if (!string.IsNullOrWhiteSpace(entry.Overview) || LibraryIdentity.Locked(entry, "Overview")) return entry;
                    var updated = entry with { Overview = existingOverview }; library.Save(updated); return updated;
                }).ToArray();
            var expired = options.RefreshDays > 0 && entries.Any(x => x.ArtworkFetchedAt > 0 && x.ArtworkFetchedAt < Wire.Now - (long)TimeSpan.FromDays(options.RefreshDays).TotalMilliseconds);
            var needsOverview = existingOverview is null || mode != MetadataRefresh.FillMissing || refresh is null && expired;
            var needsClassification = mode != MetadataRefresh.FillMissing || expired || entries.Any(x => x.MetadataKind is null || x.ArtworkFetchedAt == 0);
            var sharedPoster = entries.SelectMany(x => new[] { x.SeriesPoster, x.Poster }).FirstOrDefault(ArtworkCache.IsUsable);
            // A season image or a prior match does not prove the show poster exists.
            var needsPoster = sharedPoster is null || mode == MetadataRefresh.ReplaceArtwork;
            if (sharedPoster is not null)
                entries = entries.Select(entry =>
                {
                    var updated = entry with { Poster = ArtworkCache.IsUsable(entry.Poster) ? entry.Poster : sharedPoster,
                        SeriesPoster = entry.Series is null ? entry.SeriesPoster : ArtworkCache.IsUsable(entry.SeriesPoster) ? entry.SeriesPoster : sharedPoster };
                    if (updated != entry) library.Save(updated);
                    return updated;
                }).ToArray();
            if (!needsOverview && !needsPoster && !needsClassification) continue;
            if (!force && refresh is null && MetadataFetchPolicy.Deferred(library, "title", entries, options, token)) continue;
            var item = entries[0]; progress?.Report("Finding metadata · " + item.DisplayTitle);
            var attemptFailed = false; var attemptUnmatched = false;
            try
            {
                string? poster = null, source = null, overview = null;
                var resolved = await ResolveAsync(http, tmdb, MetadataTitles.WithSplitIdentity(item, library.All()), options, directory, ct);
                attemptUnmatched = resolved is null;
                var currentEntries = library.All().Where(x => ids.Contains(x.Id)).ToDictionary(x => x.Id);
                if (entries.Any(x => !currentEntries.TryGetValue(x.Id, out var current) || !SameFetchIdentity(x, current)))
                {
                    issues.Add(new { Title = item.DisplayTitle, Code = "changed-during-fetch", Detail = "Identity or locks changed while the provider request was running; stale result discarded." });
                    progress?.Report("Kept newer metadata · " + item.DisplayTitle); continue;
                }
                // Preserve text/artwork edits made while awaiting a provider response.
                entries = entries.Select(x => currentEntries[x.Id]).ToArray();
                if (resolved is { } found)
                {
                    var changedIdentity = !item.Matched && (item.MetadataKind is not null && found.Kind != item.MetadataKind ||
                        item.MetadataProvider == found.Provider && item.MetadataId is not null && found.Id != item.MetadataId);
                    if (changedIdentity)
                    {
                        needsOverview = true;
                        needsPoster = true;
                        entries = entries.Select(entry => ResetIdentity(entry)).ToArray();
                    }
                    Classify(found.Kind, found.Type, found.Provider, found.Id, found.Title, found.Year);
                    overview = found.Overview; source = found.ImageSource ?? found.Source;
                    if (found.Tmdb is { } details)
                        entries = entries.Select(entry => MergeDetails(entry, details)).ToArray();
                    SaveOverview();
                    foreach (var entry in entries) library.Save(entry);
                    if (needsPoster && entries.Any(x => !LibraryIdentity.Locked(x, "Poster")) && found.Image is not null)
                        poster = (found.ImageProvider ?? found.Provider) == "tmdb" && tmdb is not null
                            ? await tmdb.CacheImageAsync(found.Image, $"{found.Type}-{found.Id}", Path.Combine(directory, "posters"), ct, mode == MetadataRefresh.ReplaceArtwork)
                            : await Download(http, found.Image, directory, ct, mode == MetadataRefresh.ReplaceArtwork);
                }
                void Classify(string kind, string type, string provider, int? id, string? title, int? year)
                {
                    entries = entries.Select(entry =>
                    {
                        var updated = entry.Matched && provider != entry.MetadataProvider ? entry : MetadataClassification.Apply(entry, kind, type, type == "tv" ? title : null);
                        if (year is not null && !LibraryIdentity.Locked(entry, "Year")) updated = updated with { Year = year };
                        if (LibraryIdentity.Locked(entry, "Title")) updated = updated with { Title = entry.Title, Series = type == "tv" ? entry.Series ?? entry.Title : null };
                        return LibraryIdentity.WithProvider(updated, provider, id, !entry.Matched || entry.MetadataProvider == provider);
                    }).ToArray();
                    foreach (var entry in entries) library.Save(entry);
                }
                void SaveOverview()
                {
                    if (!needsOverview || string.IsNullOrWhiteSpace(overview)) return;
                    entries = entries.Select(entry => LibraryIdentity.Locked(entry, "Overview") || mode == MetadataRefresh.FillMissing && !string.IsNullOrWhiteSpace(entry.Overview)
                        ? entry : entry with { Overview = overview }).ToArray();
                    foreach (var entry in entries) library.Save(entry);
                    overviews++;
                }
                if (poster is not null)
                {
                    var expected = entries.ToDictionary(x => x.Id);
                    var installed = false;
                    foreach (var entry in library.All().Where(x => ids.Contains(x.Id) && !LibraryIdentity.Locked(x, "Poster")))
                        if (expected.TryGetValue(entry.Id, out var previous) && SameFetchIdentity(previous, entry))
                        { library.Save(entry with { Poster = poster, SeriesPoster = entry.Series is null ? null : poster, PosterSource = source }); installed = true; }
                    if (installed) fetched++;
                }
                if (needsPoster && poster is null || needsOverview && string.IsNullOrWhiteSpace(overview))
                {
                    missing++;
                    issues.Add(new { Title = item.DisplayTitle, Code = resolved is null ? "no-confident-match" : "provider-missing-fields",
                        PosterMissing = needsPoster && poster is null, OverviewMissing = needsOverview && string.IsNullOrWhiteSpace(overview) });
                }
                if (resolved is not null) foreach (var entry in library.All().Where(x => entries.Any(e => e.Id == x.Id)))
                {
                    library.Save(entry with { ArtworkFetchedAt = Wire.Now });
                    if (options.SaveNfo) LocalMetadata.Export(entry);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or InvalidOperationException or KeyNotFoundException or TaskCanceledException && !ct.IsCancellationRequested)
            { attemptFailed = true; failed++; error = FailureReason(ex); issues.Add(new { Title = item.DisplayTitle, Code = "provider-failure", Detail = error }); progress?.Report("Metadata unavailable · " + item.DisplayTitle + " · " + error); }
            MetadataFetchPolicy.Record(library, "title", library.All().Where(x => ids.Contains(x.Id)), options, token, attemptFailed, attemptUnmatched);
            await Task.Delay(550, ct);
        }
        library.GroupShowsByProvider();
        var seriesReport = await SeriesMetadata.FetchAsync(library, directory, token, progress, ct, http, force, tmdb, refresh);
        var summary = $"Posters: {fetched} added · Overviews: {overviews} added · {missing} incomplete/unmatched · {failed} failed." + (error is null ? "" : " " + error) + " " + seriesReport;
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata-fetch-report.json"), Wire.Serialize(new { FetchedAt = Wire.Now,
            TmdbAvailable = tmdb is not null, Providers = options.ProviderOrder, Summary = summary, Issues = issues }), ct);
        return summary + (tmdb is null && options.ProviderOrder.Contains("tmdb") ? " TMDB is unavailable without a token; movie coverage is limited to Wikipedia." : "");
    }
    private static MediaItem ResetIdentity(MediaItem item) => item with
    {
        ProviderIds = [], MetadataProvider = null, MetadataId = null,
        Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : null,
        Poster = LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork" ? item.Poster : null,
        SeriesPoster = LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork" ? item.SeriesPoster : null,
        PosterSource = LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork" ? item.PosterSource : null,
        SeasonTitle = LibraryIdentity.Locked(item, "SeasonTitle") ? item.SeasonTitle : null,
        SeasonOverview = LibraryIdentity.Locked(item, "SeasonOverview") ? item.SeasonOverview : null,
        SeasonPoster = LibraryIdentity.Locked(item, "SeasonPoster") ? item.SeasonPoster : null,
        EpisodeTitle = LibraryIdentity.Locked(item, "EpisodeTitle") ? item.EpisodeTitle : null,
        EpisodeOverview = LibraryIdentity.Locked(item, "EpisodeOverview") ? item.EpisodeOverview : null,
        EpisodePoster = LibraryIdentity.Locked(item, "EpisodePoster") ? item.EpisodePoster : null,
        EpisodeSource = null, SeasonSource = null, MetadataFetchedAt = 0
    };
    internal static bool SameFetchIdentity(MediaItem left, MediaItem right) =>
        left.Title == right.Title && left.Series == right.Series && left.Year == right.Year && left.ShowId == right.ShowId &&
        left.Season == right.Season && left.Episode == right.Episode && left.EpisodeEnd == right.EpisodeEnd &&
        left.NumberingOrder == right.NumberingOrder && left.AbsoluteEpisode == right.AbsoluteEpisode && left.SourcePart == right.SourcePart &&
        left.MetadataType == right.MetadataType && left.MetadataProvider == right.MetadataProvider && left.MetadataId == right.MetadataId &&
        left.Matched == right.Matched && left.MetadataLocked == right.MetadataLocked &&
        (left.LockedFields ?? []).Order().SequenceEqual((right.LockedFields ?? []).Order()) &&
        (left.LocalMetadataFields ?? []).Order().SequenceEqual((right.LocalMetadataFields ?? []).Order()) &&
        (left.ProviderIds ?? []).OrderBy(x => x.Key).SequenceEqual((right.ProviderIds ?? []).OrderBy(x => x.Key));
    internal static string FailureReason(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Provider rejected access (HTTP " + (int)h.StatusCode.Value + "); check provider credentials.",
        HttpRequestException h when h.StatusCode == HttpStatusCode.TooManyRequests => "Provider rate limited requests (HTTP 429); retry after its cooldown.",
        HttpRequestException h when h.StatusCode is not null => "Provider returned HTTP " + (int)h.StatusCode.Value + ".",
        TaskCanceledException => "Provider request timed out.",
        HttpRequestException => "Provider connection failed.",
        IOException => "Artwork or local cache could not be read or written.",
        _ => "Provider returned invalid or incomplete metadata."
    };
    internal record Resolved(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title, MetadataMatch? Tmdb = null,
        string? ImageProvider = null, string? ImageSource = null, int? Year = null, bool? Animated = null);
    public static MediaItem MergeDetails(MediaItem item, MetadataMatch match)
    {
        var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var id in match.ProviderIds ?? []) ids[id.Key] = id.Value;
        return item with { ProviderIds = ids, OriginalTitle = LibraryIdentity.Locked(item, "OriginalTitle") ? item.OriginalTitle : match.OriginalTitle ?? item.OriginalTitle,
            Genres = LibraryIdentity.Locked(item, "Genres") ? item.Genres : match.Genres ?? item.Genres,
            Cast = LibraryIdentity.Locked(item, "Cast") ? item.Cast : match.Cast ?? item.Cast,
            Rating = LibraryIdentity.Locked(item, "Rating") ? item.Rating : match.Rating ?? item.Rating,
            Year = LibraryIdentity.Locked(item, "Year") ? item.Year : int.TryParse(match.Year, out var year) ? year : item.Year };
    }
    internal static async Task<Resolved?> ResolveAsync(HttpClient http, MetadataClient? tmdb, MediaItem item, MetadataOptions options, string directory, CancellationToken ct)
    {
        Exception? failure = null; Resolved? best = null;
        bool Accept(Resolved candidate)
        {
            if (best is null) best = candidate;
            else if (candidate.Type == best.Type && candidate.Title is not null && best.Title is not null &&
                MetadataTitles.Normalize(candidate.Title) == MetadataTitles.Normalize(best.Title) && candidate.Kind == best.Kind &&
                (candidate.Animated is null || best.Animated is null || candidate.Animated == best.Animated) &&
                (candidate.Year is null || best.Year is null || Math.Abs(candidate.Year.Value - best.Year.Value) <= 1))
            {
                var useImage = best.Image is null && candidate.Image is not null;
                best = best with { Image = useImage ? candidate.Image : best.Image, ImageProvider = useImage ? candidate.Provider : best.ImageProvider,
                    ImageSource = useImage ? candidate.Source : best.ImageSource,
                    Overview = string.IsNullOrWhiteSpace(best.Overview) ? candidate.Overview : best.Overview };
            }
            return best.Image is not null && !string.IsNullOrWhiteSpace(best.Overview);
        }
        foreach (var provider in options.ProviderOrder)
        {
            try
            {
                if (provider == "tmdb" && tmdb is not null)
                {
                    var match = await tmdb.MatchAsync(item, ct);
                    if (match is null && MetadataTitles.IsAnimeSource(item))
                        foreach (var alias in await AnimeTitles.AliasesAsync(http, directory, item.DisplayTitle, ct))
                        { match = await tmdb.MatchAsync(item with { Title = alias, Series = item.Series is null ? null : alias }, ct); if (match is not null) break; }
                    if (match is not null && Accept(new(match.Poster, $"https://www.themoviedb.org/{match.Type}/{match.Id}", match.Overview,
                        match.Kind ?? (match.Type == "tv" ? "Show" : "Movie"), match.Type, provider, match.Id, match.Title, match,
                        Year: int.TryParse(match.Year, out var year) ? year : null, Animated: match.Animated))) return best;
                }
                if (provider == "tvmaze" && item.MetadataType != "movie" &&
                    (item.Matched || !MetadataMatching.HasMovieEvidence(item)))
                {
                    var show = await LookupShow(http, item, ct);
                    if (show is null && MetadataTitles.IsAnimeSource(item)) foreach (var alias in await AnimeTitles.AliasesAsync(http, directory, item.DisplayTitle, ct))
                    { show = await LookupShow(http, item with { Title = alias, Series = alias }, ct); if (show is not null) break; }
                    if (show is { } value)
                    {
                        // A standalone title can name both a film and a show.
                        if (item.Series is null && item.Episode is null && options.ProviderOrder.Contains("wikipedia") && await LookupMovieWithFallback(http, item, directory, ct) is not null) return null;
                        if (Accept(new(value.Image, value.Source, value.Overview, value.Kind, value.Type, provider, value.Id, value.Title,
                            Year: value.Year, Animated: value.Animated))) return best;
                    }
                }
                if (provider == "wikipedia" && item.MetadataType != "tv" && await LookupMovieWithFallback(http, item, directory, ct) is { } movie)
                    if (Accept(movie)) return best;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or InvalidOperationException or KeyNotFoundException or TaskCanceledException && !ct.IsCancellationRequested)
            { failure = ex; }
        }
        if (best is not null) return best;
        if (failure is not null) throw failure;
        return null;
    }
    private static async Task<JsonDocument> Json(HttpClient http, string url, CancellationToken ct) => JsonDocument.Parse(await MetadataHttp.GetString(http, url, ct));
    private static async Task<(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title, int? Year, bool? Animated)?> LookupShow(HttpClient http, MediaItem item, CancellationToken ct)
    {
            var known = Regex.Match(item.PosterSource ?? "", @"^https://www\.tvmaze\.com/shows/(\d+)");
            var knownId = int.TryParse(LibraryIdentity.ProviderId(item, "tvmaze"), out var stored) ? stored : known.Success ? int.Parse(known.Groups[1].Value) : (int?)null;
            var external = LibraryIdentity.ProviderId(item, "imdb") is { } imdb ? "imdb=" + Uri.EscapeDataString(imdb)
                : LibraryIdentity.ProviderId(item, "tvdb") is { } tvdb ? "thetvdb=" + Uri.EscapeDataString(tvdb) : null;
            var url = knownId is > 0 ? $"https://api.tvmaze.com/shows/{knownId}" : external is not null ? "https://api.tvmaze.com/lookup/shows?" + external : "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle);
            JsonDocument document;
            try { document = await Json(http, url, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            { knownId = null; external = null; document = await Json(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle), ct); }
            using var results = document;
            var candidates = knownId is > 0 || external is not null ? new[] { results.RootElement } : results.RootElement.EnumerateArray().Select(x => x.GetProperty("show"));
            var matches = candidates.ToArray();
            MetadataMatch Candidate(JsonElement value) => new(value.GetProperty("id").GetInt32(), "tv", value.GetProperty("name").GetString()!,
                value.TryGetProperty("premiered", out var premiered) && premiered.ValueKind == JsonValueKind.String && premiered.GetString()?.Length >= 4 ? premiered.GetString()![..4] : null,
                null, "", MetadataClassification.Tvmaze(value), Animated: value.TryGetProperty("type", out var type) ? type.GetString() == "Animation" : null);
            if ((knownId is > 0 || external is not null) && matches.Length == 1 && !MetadataMatching.Compatible(item, Candidate(matches[0])))
            {
                knownId = null; external = null;
                using var fresh = await Json(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(item.DisplayTitle), ct);
                matches = fresh.RootElement.EnumerateArray().Select(x => x.GetProperty("show").Clone()).ToArray();
            }
            if (knownId is not > 0 && external is null)
            {
                int? Best(JsonElement[] candidates, string title)
                {
                    return MetadataMatching.Choose(candidates.Select(Candidate), item with { Title = title, Series = title })?.Id;
                }
                var best = Best(matches, item.DisplayTitle);
                if (best is null) foreach (var query in MetadataTitles.Queries(item).Skip(1))
                {
                    using var aliases = await Json(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query), ct);
                    var aliasMatches = aliases.RootElement.EnumerateArray().Select(x => x.GetProperty("show").Clone()).ToArray();
                    best = Best(aliasMatches, query);
                    if (best is not null) { matches = aliasMatches; break; }
                }
                if (best is null) return null;
                matches = matches.Where(x => x.GetProperty("id").GetInt32() == best).ToArray();
            }
            if (matches.Length != 1) return null; var show = matches[0];
            var imageUrl = show.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
                ? image.TryGetProperty("medium", out var medium) && medium.ValueKind == JsonValueKind.String ? medium.GetString()
                    : image.TryGetProperty("original", out var original) && original.ValueKind == JsonValueKind.String ? original.GetString() : null
                : null;
            var summary = show.TryGetProperty("summary", out var text) ? text.GetString() : null;
            // TVmaze summaries contain HTML; render readable plain text in the native UI.
            summary = summary is null ? null : WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(summary, @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim();
            return (imageUrl, show.GetProperty("url").GetString()!, summary, MetadataClassification.Tvmaze(show), "tv", "tvmaze",
                show.TryGetProperty("id", out var id) ? id.GetInt32() : null, show.GetProperty("name").GetString(),
                int.TryParse(Candidate(show).Year, out var showYear) ? showYear : null, Candidate(show).Animated);
    }
    private static async Task<(string? Image, string Source, string? Overview, string Kind, string Type, string Provider, int? Id, string? Title)?> LookupMovie(HttpClient http, MediaItem item, CancellationToken ct)
    {
        if (item.Episode is not null) return null;
        var titles = item.Year is { } year ? new[] { $"{item.Title} ({year} film)", $"{item.Title} (film)", item.Title } : new[] { $"{item.Title} (film)", item.Title };
        var known = int.TryParse(LibraryIdentity.ProviderId(item, "wikipedia"), out var pageId) && pageId > 0;
        using var pages = await Json(http, "https://en.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&redirects=1&prop=pageimages%7Ccategories%7Cextracts&exintro=1&explaintext=1&exlimit=max&piprop=thumbnail&pithumbsize=500&pilicense=any&cllimit=50&" +
            (known ? "pageids=" + pageId : "titles=" + Uri.EscapeDataString(string.Join("|", titles))), ct);
        var candidates = pages.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray().Where(p => p.TryGetProperty("title", out var title)
            && (known || MetadataTitles.Normalize(Regex.Replace(title.GetString() ?? "", @"\s*\([^)]*film\)\s*$", "", RegexOptions.IgnoreCase)) == MetadataTitles.Normalize(item.DisplayTitle))
            && p.TryGetProperty("categories", out var categories)
            && WikipediaMetadata.MatchesYear(p, item)
            && categories.EnumerateArray().Any(c => c.GetProperty("title").GetString()!.Contains("films", StringComparison.OrdinalIgnoreCase))
            && !categories.EnumerateArray().Any(c => Regex.IsMatch(c.GetProperty("title").GetString() ?? "", @"(?i)television (?:series|shows|characters)|sitcoms"))).ToArray();
        // Never silently choose between remakes with the same name.
        if (candidates.Length != 1) return null;
        var page = candidates[0];
        return (page.TryGetProperty("thumbnail", out var thumbnail) ? thumbnail.GetProperty("source").GetString() : null,
            "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(page.GetProperty("title").GetString()!),
            page.TryGetProperty("extract", out var extract) ? extract.GetString() : null,
            MetadataClassification.Wikipedia(page), "movie", "wikipedia", page.TryGetProperty("pageid", out var resolvedId) ? resolvedId.GetInt32() : known ? pageId : null,
            Regex.Replace(page.GetProperty("title").GetString()!, @"\s*\([^)]*film\)\s*$", "", RegexOptions.IgnoreCase));
    }
    private static async Task<Resolved?> LookupMovieWithFallback(HttpClient http, MediaItem item, string directory, CancellationToken ct)
    {
        if (item.Episode is not null || item.AbsoluteEpisode is not null || item.SourcePartEpisode is not null || item.Series is not null) return null;
        var queries = MetadataTitles.Queries(item).ToList();
        if (MetadataTitles.IsAnimeSource(item))
            foreach (var query in queries.ToArray()) queries.AddRange(await AnimeTitles.AliasesAsync(http, directory, query, ct));
        Exception? failure = null;
        if (WikipediaMetadata.ActionAvailable(http))
        {
            foreach (var query in queries.Distinct().Take(8))
            {
                try
                {
                    if (await LookupMovie(http, item with { Title = query }, ct) is { } movie)
                        return new(movie.Image, movie.Source, movie.Overview, movie.Kind, movie.Type, movie.Provider, movie.Id, movie.Title);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
                { WikipediaMetadata.LimitAction(http); failure = ex; break; }
            }
        }
        try
        {
            // The independent summary endpoint remains useful when Action API requests are unavailable.
            return await WikipediaMetadata.SummaryAsync(http, item, queries.Distinct().Take(8), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        { if (failure is not null) throw failure; return null; }
    }
    internal static async Task<string> Download(HttpClient http, string url, string directory, CancellationToken ct, bool replace = false)
    {
        var uri = new Uri(url); if (uri.Scheme != "https" || !(uri.Host.EndsWith(".tvmaze.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "upload.wikimedia.org" || uri.Host == "image.tmdb.org")) throw new IOException("Unsupported artwork host.");
        var folder = Path.Combine(directory, "posters");
        var stem = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + (replace ? "-" + Guid.NewGuid().ToString("N") : ""));
        foreach (var extension in new[] { ".jpg", ".png" }) if (!replace && ArtworkCache.IsUsable(stem + extension)) return stem + extension;
        var bytes = await MetadataHttp.GetImage(http, url, ct); var jpeg = bytes[0] == 255 && bytes[1] == 216;
        Directory.CreateDirectory(folder);
        var path = stem + (jpeg ? ".jpg" : ".png");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, bytes, ct); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }
}
