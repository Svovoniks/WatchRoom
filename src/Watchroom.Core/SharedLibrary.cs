namespace Watchroom.Core;

public record LibraryBrowseRequest(string Query = "", int Category = 0, string? ParentId = null, int? Season = null, int Page = 0);
public record SharedLibraryEntry(string Id, string Title, string Caption, string Level, int? Season, string Overview,
    string? Thumbnail = null, string Kind = "Movie", int? Episode = null, int? RuntimeMinutes = null);
public record LibraryBrowsePage(SharedLibraryEntry[] Entries, int Total, int Page, string Location);
public record LibraryBrowseActivity(string Peer, string Name, string Location, string Query, long UpdatedAt);

public static class SharedLibrary
{
    public const int PageSize = 8;
    private static string Limit(string? value, int length) => value is null ? "" : value[..Math.Min(value.Length, length)];
    public static LibraryBrowsePage Browse(IEnumerable<MediaItem> source, LibraryBrowseRequest request, Func<string?, string?>? thumbnail = null)
    {
        if (request.Query is null || request.Query.Length > 100 || request.Category is < 0 or > 3 || request.Page is < 0 or > 100000 || request.ParentId?.Length > 128)
            throw new InvalidDataException("Invalid library search");
        // Keep artwork paths internal; only bounded thumbnail bytes leave the host.
        var items = source.Where(x => x.Available && !x.IsVirtual && !x.IsExtra).ToArray();
        var parent = request.ParentId is null ? null : items.FirstOrDefault(x => x.Id == request.ParentId);
        if (request.ParentId is not null && parent is null) throw new InvalidDataException("This title is no longer available. Return to all titles.");
        var location = parent is null ? "All titles" : parent.DisplayTitle + (request.Season is null ? "" : $" · Season {request.Season}");
        var publicItems = items.Select(x => new MediaItem(x.Id, "", x.Title, x.Kind, x.Year, x.Series, x.Season, x.Episode,
            Overview: x.Overview, EpisodeTitle: x.EpisodeTitle, EpisodeOverview: x.EpisodeOverview,
            ShowId: LibraryIdentity.ShowKey(x), EpisodeEnd: x.EpisodeEnd, Poster: x.Poster,
            SeriesPoster: x.SeriesPoster, SeasonPoster: x.SeasonPoster, EpisodePoster: x.EpisodePoster,
            GeneratedEpisodePoster: x.GeneratedEpisodePoster, PosterSource: x.PosterSource, RuntimeMinutes: x.RuntimeMinutes)).ToArray();
        var visible = publicItems.Where(x => request.Category switch { 1 => x.Kind == "Movie", 2 => x.Kind == "Show", 3 => x.Kind == "Anime", _ => true });
        var cards = LibraryCatalog.Search(visible, request.Query, parent is null ? null : LibraryIdentity.ShowKey(publicItems.First(x => x.Id == parent.Id)), parent?.Kind, request.Season);
        var page = Math.Min(request.Page, Math.Max(0, (cards.Count - 1) / PageSize));
        var result = new LibraryBrowsePage(cards.Skip(page * PageSize).Take(PageSize).Select(c => new SharedLibraryEntry(c.Media.Id,
            Limit(string.IsNullOrWhiteSpace(c.DisplayTitle) ? c.Media.Title : c.DisplayTitle, 180), Limit(c.Caption, 240), c.Level, c.Season, Limit(c.Media.DetailOverview, 400),
            thumbnail?.Invoke(c.Poster) is { Length: <= 5400 } image ? image : null, c.Media.Kind, c.Media.Episode, c.Media.RuntimeMinutes)).ToArray(), cards.Count, page, Limit(location, 240));
        // The data JSON is nested in the wire envelope: escaped Unicode counts twice.
        // Preserve posters and titles, shortening optional summaries to fit the channel.
        for (var limit = 200; limit >= 0; limit = limit == 0 ? -1 : limit / 2)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(result)))) < 62000) break;
            result = result with { Entries = result.Entries.Select(entry => entry with
                { Overview = Limit(entry.Overview, limit), Caption = Limit(entry.Caption, limit) }).ToArray() };
        }
        // Unusual base64 can expand when JSON escapes it. Always enforce the final
        // byte budget even after optional text is exhausted.
        for (var index = result.Entries.Length - 1; index >= 0 &&
            System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(result)))) >= 62000; index--)
            result.Entries[index] = result.Entries[index] with { Thumbnail = null };
        return result;
    }
}
