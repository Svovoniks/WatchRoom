using System.Text.Json;

namespace Watchroom.Core;

public static class MetadataClassification
{
    public static bool? Animation(JsonElement details) => details.TryGetProperty("genres", out var genres)
        ? genres.EnumerateArray().Any(g => g.TryGetProperty("id", out var id) && id.GetInt32() == 16)
        : details.TryGetProperty("genre_ids", out var ids) ? ids.EnumerateArray().Any(g => g.GetInt32() == 16) : null;
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    public static string Tvmaze(JsonElement show)
    {
        var animation = Text(show, "type") == "Animation";
        var japanese = Text(show, "language") == "Japanese";
        foreach (var field in new[] { "network", "webChannel" })
            if (show.TryGetProperty(field, out var channel) && channel.ValueKind == JsonValueKind.Object && channel.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.Object)
                japanese |= Text(country, "code") == "JP";
        return animation && japanese ? "Anime" : "Show";
    }
    public static string Tmdb(JsonElement details, string type)
    {
        var animation = details.TryGetProperty("genres", out var genres) && genres.EnumerateArray().Any(g => g.TryGetProperty("id", out var id) && id.GetInt32() == 16)
            || details.TryGetProperty("genre_ids", out var ids) && ids.EnumerateArray().Any(g => g.GetInt32() == 16);
        var japanese = Text(details, "original_language") == "ja"
            || details.TryGetProperty("origin_country", out var origins) && origins.EnumerateArray().Any(c => c.GetString() == "JP")
            || details.TryGetProperty("production_countries", out var countries) && countries.EnumerateArray().Any(c => Text(c, "iso_3166_1") == "JP");
        var animeKeyword = false;
        if (details.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "results", "keywords" })
                if (keywords.TryGetProperty(key, out var list)) animeKeyword |= list.EnumerateArray().Any(k => string.Equals(Text(k, "name"), "anime", StringComparison.OrdinalIgnoreCase));
        return animation && (japanese || animeKeyword) ? "Anime" : type == "tv" ? "Show" : "Movie";
    }
    public static string Wikipedia(JsonElement page)
    {
        var names = page.GetProperty("categories").EnumerateArray().Select(c => Text(c, "title") ?? "");
        return names.Any(c => c.Contains("anime films", StringComparison.OrdinalIgnoreCase) || c.Contains("Japanese animated", StringComparison.OrdinalIgnoreCase)) ? "Anime" : "Movie";
    }
    public static MediaItem Apply(MediaItem item, string kind, string type, string? seriesTitle = null) => item with
    {
        Kind = kind, MetadataKind = kind, MetadataType = type,
        Series = type == "tv" ? seriesTitle ?? item.Series ?? item.Title : null,
        Season = type == "tv" ? item.Season : null, Episode = type == "tv" ? item.Episode : null,
        EpisodeEnd = type == "tv" ? item.EpisodeEnd : null, AbsoluteEpisode = type == "tv" ? item.AbsoluteEpisode : null,
        AbsoluteEpisodeEnd = type == "tv" ? item.AbsoluteEpisodeEnd : null,
        ShowId = type == "tv" ? item.ShowId : null, SeasonId = type == "tv" ? item.SeasonId : null,
        SeriesPath = type == "tv" ? item.SeriesPath : null, NumberingSource = type == "tv" ? item.NumberingSource : null,
        SourcePart = type == "tv" ? item.SourcePart : null, SourcePartEpisode = type == "tv" ? item.SourcePartEpisode : null,
        SourcePartEpisodeEnd = type == "tv" ? item.SourcePartEpisodeEnd : null,
        SeasonTitle = type == "tv" ? item.SeasonTitle : null, SeasonOverview = type == "tv" ? item.SeasonOverview : null,
        EpisodeTitle = type == "tv" ? item.EpisodeTitle : null, EpisodeOverview = type == "tv" ? item.EpisodeOverview : null,
        SeasonPoster = type == "tv" ? item.SeasonPoster : null, SeriesPoster = type == "tv" ? item.SeriesPoster : null,
        EpisodePoster = type == "tv" ? item.EpisodePoster : null, SeasonSource = type == "tv" ? item.SeasonSource : null,
        EpisodeSource = type == "tv" ? item.EpisodeSource : null, AirDate = type == "tv" ? item.AirDate : null, RuntimeMinutes = type == "tv" ? item.RuntimeMinutes : null
    };
}
