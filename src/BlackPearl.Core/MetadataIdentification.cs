using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

public record MetadataSearchResult(List<MetadataMatch> Matches, string[] Warnings);

// The explicit picker uses the same enabled providers as automatic matching.
// Credentials belong only to the TMDB client, never to fallback or image URLs.
public static class MetadataIdentification
{
    public static async Task<MetadataSearchResult> SearchAsync(LibraryStore library, MediaItem item, string query, string? token,
        CancellationToken ct = default, HttpClient? client = null, MetadataClient? metadata = null)
    {
        var options = MetadataOptions.Load(library);
        using var scope = MetadataHttp.BeginScope(library, false, options);
        using var context = MetadataDiagnostics.BeginItem("identify", item);
        using var owned = client is null ? MetadataHttp.CreateClient() : null; var http = client ?? owned!;
        using var ownedTmdb = metadata is null && TmdbCredentials.Resolve(token) is not null ? new MetadataClient(token, options: options) : null;
        var tmdb = metadata ?? ownedTmdb;
        var matches = new List<MetadataMatch>(); var warnings = new List<string>();
        foreach (var provider in options.ProviderOrder)
        {
            try
            {
                if (provider == "tmdb")
                {
                    if (tmdb is null) { warnings.Add("TMDB needs a saved key."); continue; }
                    matches.AddRange(await tmdb.SearchAsync(query, true, ct));
                    matches.AddRange(await tmdb.SearchAsync(query, false, ct));
                }
                else if (provider == "tvmaze")
                {
                    using var response = JsonDocument.Parse(await MetadataHttp.GetString(http, "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(query), ct));
                    foreach (var row in response.RootElement.EnumerateArray()) matches.Add(Tvmaze(row.GetProperty("show")));
                }
                else if (provider == "wikipedia")
                {
                    var searchItem = item with { Title = query, Series = null, Year = null, Matched = false };
                    foreach (var alias in MetadataTitles.Queries(searchItem).Take(3)) matches.AddRange(await WikipediaMetadata.SearchAsync(http, searchItem, alias, ct));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            { MetadataDiagnostics.Failure("identify", query, provider, ex); warnings.Add(provider + ": " + AutomaticArtwork.FailureReason(ex)); }
        }
        var local = await Task.Run(library.All, ct);
        var identified = local.Where(x => x.Id != item.Id && x.MetadataId is > 0 &&
            MetadataTitles.Normalize(x.DisplayTitle) == MetadataTitles.Normalize(query)).ToArray();
        return new(matches.DistinctBy(x => (x.Provider, x.Type, x.Id)).Select(match => match with
            { Evidence = identified.Any(x => LibraryIdentity.ProviderId(x, match.Provider) == match.Id.ToString()) ? "Already identified in another local release" : null })
            .OrderByDescending(x => x.Evidence is not null).ThenByDescending(x => MetadataMatching.Score(query, item.Year, x.Title, x.OriginalTitle, int.TryParse(x.Year, out var year) ? year : null))
            .Take(30).ToList(), warnings.ToArray());
    }
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static MetadataMatch Tvmaze(JsonElement show)
    {
        var id = show.GetProperty("id").GetInt32(); var date = Text(show, "premiered");
        var image = show.TryGetProperty("image", out var images) && images.ValueKind == JsonValueKind.Object ? Text(images, "medium") ?? Text(images, "original") : null;
        var ids = new Dictionary<string, string> { ["tvmaze"] = id.ToString() };
        if (show.TryGetProperty("externals", out var external) && external.ValueKind == JsonValueKind.Object)
            foreach (var (field, provider) in new[] { ("imdb", "imdb"), ("thetvdb", "tvdb") })
                if (external.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) ids[provider] = value.ToString();
        return new(id, "tv", Text(show, "name")!, date?.Length >= 4 ? date[..4] : null, image,
            WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(Text(show, "summary") ?? "", @"(?i)</p>|<br\s*/?>", "\n"), "<[^>]*>", "")).Trim(),
            MetadataClassification.Tvmaze(show), ProviderIds: ids, Animated: Text(show, "type") == "Animation", Provider: "tvmaze", Source: Text(show, "url"));
    }
    public static async Task<MetadataMatch> DetailsAsync(LibraryStore library, MetadataMatch match, string? token, CancellationToken ct = default)
    {
        var options = MetadataOptions.Load(library);
        using var scope = MetadataHttp.BeginScope(library, false, options);
        if (match.Provider == "tmdb")
        {
            using var metadata = new MetadataClient(token ?? "", options: options);
            return (await metadata.DetailsAsync(match, ct)) with { Source = $"https://www.themoviedb.org/{match.Type}/{match.Id}" };
        }
        if (match.Provider is "wikipedia" or "wikipedia:ru")
        {
            using var http = MetadataHttp.CreateClient();
            return (await WikipediaMetadata.Resolve(http, match, options, ct)).Details ?? match;
        }
        return match;
    }
    public static async Task<string?> PosterAsync(MetadataMatch match, string directory, string? token, CancellationToken ct = default)
    {
        if (match.Poster is null) return null;
        if (match.Provider == "tmdb")
        {
            using var metadata = new MetadataClient(token ?? "");
            return await metadata.CachePosterAsync(match, Path.Combine(directory, "posters"), ct);
        }
        using var http = MetadataHttp.CreateClient();
        return await AutomaticArtwork.Download(http, match.Poster, directory, ct);
    }
    public static MediaItem Apply(MediaItem item, MetadataMatch match, string? poster)
    {
        if (item.MetadataLocked) return item;
        var classified = MetadataClassification.Apply(item, match.Kind ?? (match.Type == "tv" ? "Show" : "Movie"), match.Type, match.Type == "tv" ? match.Title : null);
        var changedIdentity = item.MetadataProvider != match.Provider || item.MetadataId != match.Id || item.MetadataType != match.Type;
        var keepArtwork = !changedIdentity || LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork";
        if (changedIdentity)
            classified = classified with
            {
                SeasonTitle = LibraryIdentity.Locked(item, "SeasonTitle") ? item.SeasonTitle : null,
                SeasonOverview = LibraryIdentity.Locked(item, "SeasonOverview") ? item.SeasonOverview : null,
                EpisodeTitle = LibraryIdentity.Locked(item, "EpisodeTitle") ? item.EpisodeTitle : null,
                EpisodeOverview = LibraryIdentity.Locked(item, "EpisodeOverview") ? item.EpisodeOverview : null,
                EpisodePoster = LibraryIdentity.Locked(item, "EpisodePoster") ? item.EpisodePoster : null,
                SeasonPoster = LibraryIdentity.Locked(item, "SeasonPoster") || item.SeasonSource is null ? item.SeasonPoster : null,
                AirDate = LibraryIdentity.Locked(item, "AirDate") ? item.AirDate : null,
                RuntimeMinutes = LibraryIdentity.Locked(item, "RuntimeMinutes") ? item.RuntimeMinutes : null,
                SeasonSource = null, EpisodeSource = null
            };
        classified = AutomaticArtwork.MergeDetails(LibraryIdentity.WithProvider(classified with { ProviderIds = null, MetadataProvider = null, MetadataId = null }, match.Provider, match.Id), match);
        var result = classified with
        {
            Title = LibraryIdentity.Locked(item, "Title") ? item.Title : match.Title,
            Series = LibraryIdentity.Locked(item, "Title") && match.Type == "tv" ? item.Series ?? item.Title : classified.Series,
            Kind = LibraryIdentity.Locked(item, "Kind") ? item.Kind : classified.Kind,
            Poster = LibraryIdentity.Locked(item, "Poster") ? item.Poster : poster ?? (keepArtwork ? item.Poster : null),
            SeriesPoster = LibraryIdentity.Locked(item, "Poster") ? item.SeriesPoster : match.Type == "tv" ? poster ?? (keepArtwork ? item.SeriesPoster : null) : null,
            PosterSource = LibraryIdentity.Locked(item, "Poster") || poster is null && keepArtwork ? item.PosterSource : match.Source,
            Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : string.IsNullOrWhiteSpace(match.Overview) ? item.Overview : match.Overview,
            Season = LibraryIdentity.Locked(item, "Season") ? item.Season : classified.Season,
            Episode = LibraryIdentity.Locked(item, "Episode") ? item.Episode : classified.Episode,
            EpisodeEnd = LibraryIdentity.Locked(item, "Episode") ? item.EpisodeEnd : classified.EpisodeEnd,
            Matched = true, ArtworkFetchedAt = poster is not null || keepArtwork && ArtworkCache.IsUsable(item.Poster) ? Wire.Now : 0,
            MetadataFetchedAt = 0
        };
        return TmdbRetention.Record(item, result, match.Provider, TmdbRetention.TitleFields.Concat(TmdbRetention.PosterFields).ToArray());
    }
}
