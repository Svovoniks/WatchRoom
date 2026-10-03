namespace Watchroom.Core;

public record LibraryCard(string DisplayTitle, string Caption, string? Poster, MediaItem Media, string Level, int? Season = null);

public static class LibraryCatalog
{
    public static Task<List<LibraryCard>> SearchAsync(IEnumerable<MediaItem> source, string query, int filter = 0,
        string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false) => Task.Run(() =>
    {
        var visible = source.Where(item =>
        {
            cancellation.ThrowIfCancellationRequested();
            return filter switch
            {
                1 => item.Kind == "Movie", 2 => item.Kind == "Show", 3 => item.Kind == "Anime",
                4 => !File.Exists(item.Poster) && !File.Exists(item.SeriesPoster) && !File.Exists(item.SeasonPoster),
                5 => !item.Available, _ => true
            };
        });
        return Search(visible, query, series, kind, season, cancellation, displaySpecials);
    }, cancellation);
    public static IEnumerable<MediaItem> CardItems(IEnumerable<MediaItem> source, LibraryCard card, bool displaySpecials = false) =>
        PreferredFiles(card.Level is "series" or "season"
            ? source.Where(x => LibraryIdentity.SameShow(x, card.Media) && (card.Level != "season" || (x.Season ?? -1) == card.Season || displaySpecials && card.Season > 0 && x.Season == 0 && (x.AirsBeforeSeason == card.Season || x.AirsAfterSeason == card.Season)))
            : source.Where(x => x.Id == card.Media.Id))
        .Where(x => !x.IsVirtual).OrderBy(x => displaySpecials && x.Season == 0 ? x.AirsBeforeSeason ?? x.AirsAfterSeason ?? 0 : x.Season ?? int.MaxValue)
        .ThenBy(x => displaySpecials && x.Season == 0 && (x.AirsBeforeSeason is not null || x.AirsAfterSeason is not null)
            ? x.AirsBeforeSeason is not null ? (x.AirsBeforeEpisode ?? 1) - 0.5 : double.MaxValue : x.Episode ?? int.MaxValue).ThenBy(x => x.Path);
    public static List<LibraryCard> Search(IEnumerable<MediaItem> source, string query, string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false) =>
        Browse(source, series, kind, season, cancellation, displaySpecials).Where(card =>
        {
            cancellation.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(query) || new[] { card.DisplayTitle, card.Caption, card.Media.Title, card.Media.Path }
                .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        }).ToList();
    private static string? Existing(IEnumerable<string?> paths, CancellationToken cancellation = default) => paths.FirstOrDefault(p =>
    {
        cancellation.ThrowIfCancellationRequested();
        return ArtworkCache.IsUsable(p);
    });
    public static string? EpisodeArtwork(MediaItem item, CancellationToken cancellation = default) => Existing(item.PosterSource is null or "Local artwork"
        ? [item.Poster, item.EpisodePoster, item.GeneratedEpisodePoster, item.SeasonPoster, item.SeriesPoster]
        : [item.EpisodePoster, item.GeneratedEpisodePoster, item.SeasonPoster, item.SeriesPoster, item.Poster], cancellation);
    public static IEnumerable<MediaItem> PreferredFiles(IEnumerable<MediaItem> source)
    {
        var covered = new HashSet<string>();
        foreach (var item in source.OrderBy(x => x.IsVirtual).ThenByDescending(x => x.Available)
            .ThenByDescending(x => (x.EpisodeEnd ?? x.Episode) - x.Episode).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var keys = item.Season is null || item.NumberingConflict is not null ? [] :
                LibraryIdentity.EpisodeNumbers(item).Select(n => $"{LibraryIdentity.ShowKey(item)}:{item.Season}:{n}").ToArray();
            if (keys.Length > 0 && keys.All(covered.Contains)) continue;
            foreach (var key in keys) covered.Add(key);
            yield return item;
        }
    }
    public static List<LibraryCard> Browse(IEnumerable<MediaItem> source, string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false)
    {
        cancellation.ThrowIfCancellationRequested();
        var items = source.Where(item => !item.IsExtra).Select(item => { cancellation.ThrowIfCancellationRequested(); return item; }).ToArray();
        if (series is null)
            return items.GroupBy(LibraryIdentity.ShowKey, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                cancellation.ThrowIfCancellationRequested();
                var item = g.OrderBy(x => x.IsVirtual).ThenByDescending(x => x.Available).First();
                var seasons = g.Select(x => x.Season).Distinct().Count();
                return new LibraryCard(item.DisplayTitle, item.Series is null ? item.Caption + (item.Available ? "" : " · Unavailable") :
                    (item.Year is null ? "" : item.Year + " · ") + $"{seasons} {(seasons == 1 ? "season" : "seasons")} · {LibraryIdentity.EpisodeCount(g)} episodes" +
                    (g.Any(x => x.IsVirtual) ? $" · {g.Count(x => x.IsVirtual)} missing/upcoming" : "") + (g.Any(x => x.Available) ? "" : " · Unavailable"),
                    Existing(g.Select(x => x.SeriesPoster).Concat(g.Select(x => x.Poster)).Concat(g.Select(x => x.SeasonPoster)), cancellation), item, item.Series is null ? "movie" : "series");
            }).OrderBy(x => x.DisplayTitle).ToList();
        var episodes = items.Where(x => LibraryIdentity.InShow(x, series, kind));
        if (season is null)
            return episodes.GroupBy(x => x.Season ?? -1).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key).Select(g => new LibraryCard(g.Key < 0 ? "Season unknown" : g.Key == 0 ? "Specials" : $"Season {g.Key}", $"{LibraryIdentity.EpisodeCount(g)} episodes",
                Existing(g.Select(x => x.SeasonPoster).Concat(g.Select(x => x.SeriesPoster)).Concat(g.Select(x => x.Poster)), cancellation), g.First(), "season", g.Key)).ToList();
        var seasonItems = episodes.Where(x => (x.Season ?? -1) == season || displaySpecials && season > 0 && x.Season == 0 && (x.AirsBeforeSeason == season || x.AirsAfterSeason == season)).ToArray();
        return PreferredFiles(seasonItems).OrderBy(x => x.Season == 0 && season > 0 ? x.AirsBeforeSeason == season ? (x.AirsBeforeEpisode ?? 1) - 0.5 : double.MaxValue : x.Episode ?? int.MaxValue)
            .ThenBy(x => x.Path).Select(x => new LibraryCard(x.Episode is not null ? x.EpisodeDisplayTitle : Path.GetFileNameWithoutExtension(x.Path),
                x.Caption + (x.IsVirtual ? DateOnly.TryParse(x.AirDate, out var date) && date > DateOnly.FromDateTime(DateTime.UtcNow) ? " · Upcoming" : " · Missing" : x.Available ? "" : " · Unavailable") +
                (x.Season is not null && x.Episode is not null && x.NumberingConflict is null && seasonItems.Count(v => v.Season == x.Season && v.Episode == x.Episode && v.EpisodeEnd == x.EpisodeEnd && v.NumberingConflict is null && !v.IsVirtual) is > 1 and var versions ? $" · {versions} versions" : ""),
                EpisodeArtwork(x, cancellation), x, "episode", season)).ToList();
    }
}
