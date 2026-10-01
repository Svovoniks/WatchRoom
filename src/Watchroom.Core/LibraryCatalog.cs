namespace Watchroom.Core;

public record LibraryCard(string DisplayTitle, string Caption, string? Poster, MediaItem Media, string Level, int? Season = null);

public static class LibraryCatalog
{
    public static IEnumerable<MediaItem> CardItems(IEnumerable<MediaItem> source, LibraryCard card) =>
        (card.Level is "series" or "season"
            ? source.Where(x => x.Series == card.Media.Series && x.Kind == card.Media.Kind && (card.Level != "season" || (x.Season ?? 1) == card.Season))
            : source.Where(x => x.Id == card.Media.Id))
        .OrderBy(x => x.Season ?? 1).ThenBy(x => x.Episode ?? int.MaxValue).ThenBy(x => x.Path);
    public static List<LibraryCard> Search(IEnumerable<MediaItem> source, string query, string? series = null, string? kind = null, int? season = null) =>
        Browse(source, series, kind, season).Where(card => string.IsNullOrWhiteSpace(query) ||
            new[] { card.DisplayTitle, card.Caption, card.Media.Title, card.Media.Path }.Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
    private static string? Existing(IEnumerable<string?> paths) => paths.FirstOrDefault(p => p is not null && File.Exists(p));
    public static string? EpisodeArtwork(MediaItem item) => Existing(item.PosterSource is null or "Local artwork"
        ? [item.Poster, item.EpisodePoster, item.SeasonPoster, item.SeriesPoster]
        : [item.EpisodePoster, item.SeasonPoster, item.SeriesPoster, item.Poster]);
    public static List<LibraryCard> Browse(IEnumerable<MediaItem> source, string? series = null, string? kind = null, int? season = null)
    {
        var items = source.ToArray();
        if (series is null)
            return items.GroupBy(x => x.Series is null ? x.Id : x.Kind + ":" + x.Series).Select(g =>
            {
                var item = g.First();
                var seasons = g.Select(x => x.Season ?? 1).Distinct().Count();
                return new LibraryCard(item.DisplayTitle, item.Series is null ? item.Caption + (item.Available ? "" : " · Unavailable") : $"{seasons} {(seasons == 1 ? "season" : "seasons")} · {g.Count()} episodes" + (g.Any(x => x.Available) ? "" : " · Unavailable"),
                    Existing(g.Select(x => x.SeriesPoster).Concat(g.Select(x => x.Poster))), item, item.Series is null ? "movie" : "series");
            }).OrderBy(x => x.DisplayTitle).ToList();
        var episodes = items.Where(x => x.Series == series && x.Kind == kind);
        if (season is null)
            return episodes.GroupBy(x => x.Season ?? 1).OrderBy(g => g.Key).Select(g => new LibraryCard(g.Key == 0 ? "Specials" : $"Season {g.Key}", $"{g.Count()} episodes",
                Existing(g.Select(x => x.SeasonPoster).Concat(g.Select(x => x.SeriesPoster)).Concat(g.Select(x => x.Poster))), g.First(), "season", g.Key)).ToList();
        return episodes.Where(x => (x.Season ?? 1) == season).OrderBy(x => x.Episode ?? int.MaxValue).ThenBy(x => x.Path).Select(x => new LibraryCard(
            x.Episode is not null ? x.EpisodeDisplayTitle : Path.GetFileNameWithoutExtension(x.Path), x.Caption + (x.Available ? "" : " · Unavailable"), EpisodeArtwork(x), x, "episode", season)).ToList();
    }
}
