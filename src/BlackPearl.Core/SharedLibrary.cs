namespace BlackPearl.Core;

public record LibraryBrowseRequest(string Query = "", int Category = 0, string? ParentId = null, int? Season = null, int Page = 0, bool Home = false, int Seed = 0);
public record SharedLibraryEntry(string Id, string Title, string Caption, string Level, int? Season, string Overview,
    string? Thumbnail = null, string Kind = "Movie", int? Episode = null, int? RuntimeMinutes = null,
    string? HomeSection = null, long PositionMs = 0);
public record LibraryBrowsePage(SharedLibraryEntry[] Entries, int Total, int Page, string Location);
public record LibraryBrowseView(LibraryBrowseRequest Browse, int LoadedCount, double ScrollFraction = 0, string? HoveredId = null, string? SelectedId = null);
// Where the browsing guest's mouse is: X and Y within the library grid, and CardX and CardY within the card it is over, all from 0 to 1.
public record LibraryPointer(double X, double Y, string? CardId = null, double CardX = 0, double CardY = 0)
{
    public bool Valid() => CardId?.Length is null or (> 0 and <= 128) && new[] { X, Y, CardX, CardY }.All(value => double.IsFinite(value) && value is >= 0 and <= 1);
}
// FullHd asks for the large copy; older hosts ignore it and send the 600 px copy.
public record LibraryArtworkRequest(string Id, string Level, int? Season = null, bool FullHd = false);
// Paced chunks belong to a full HD transfer: the guest acknowledges each one before the host sends more.
public record LibraryArtworkChunk(string Key, int Index, int Count, string Data, bool Paced = false);

public static class ArtworkTransfer
{
    // The original 600 px copy is pushed in one burst; it stays small enough not to delay room commands.
    public const int ChunkSize = 8000, MaxChunks = 25;
    // Full HD copies are larger, so at most Window chunks wait on the connection ahead of playback commands.
    public const int PacedChunkSize = 24000, PacedMaxChunks = 80, Window = 2;
    public const int MaxFullHdBytes = 1_400_000;
    public static bool ValidChunk(LibraryArtworkChunk chunk) => chunk.Paced
        ? chunk.Count is >= 1 and <= PacedMaxChunks && chunk.Index >= 0 && chunk.Index < chunk.Count && chunk.Data.Length <= PacedChunkSize
        : chunk.Count is >= 1 and <= MaxChunks && chunk.Index >= 0 && chunk.Index < chunk.Count && chunk.Data.Length <= ChunkSize;
}

// Estimates artwork throughput from completed transfers so large copies are only fetched when the link has room.
public sealed class ArtworkBandwidth
{
    private double? charactersPerSecond;
    public double? CharactersPerSecond => charactersPerSecond;
    public void Record(long characters, long elapsedMs)
    {
        if (characters < 20000 || elapsedMs <= 0) return;
        var rate = characters * 1000d / elapsedMs;
        charactersPerSecond = charactersPerSecond is { } previous ? previous * .6 + rate * .4 : rate;
    }
    // A full HD copy is about 1 MB on the wire; fetch it only if that takes a few seconds at most.
    public bool AllowsFullHd(double seconds = 4, long expectedCharacters = 1_000_000) => charactersPerSecond is { } rate && rate * seconds >= expectedCharacters;
    public void Slowed() => charactersPerSecond = charactersPerSecond is { } rate ? rate / 2 : null;
}
public record LibraryBrowseActivity(string Peer, string Name, string Location, string Query, long UpdatedAt, LibraryBrowseView? View = null);

public static class SharedLibrary
{
    public static string? ArtworkPath(IEnumerable<MediaItem> source, LibraryArtworkRequest request)
    {
        var items = source.Where(x => x.Available && !x.IsVirtual && !x.IsExtra).ToArray();
        var item = items.FirstOrDefault(x => x.Id == request.Id);
        if (item is null) return null;
        return LibraryCatalog.Browse(items, request.Level is "series" or "movie" ? null : LibraryIdentity.ShowKey(item),
            item.Kind, request.Level == "episode" ? item.Season ?? -1 : null)
            .FirstOrDefault(card => card.Media.Id == request.Id && card.Level == request.Level && (request.Level != "season" || card.Season == request.Season))?.Poster;
    }
    public const int PageSize = 8;
    private static string Limit(string? value, int length) => value is null ? "" : value[..Math.Min(value.Length, length)];
    public static LibraryBrowsePage Browse(IEnumerable<MediaItem> source, LibraryBrowseRequest request, Func<string?, string?>? thumbnail = null,
        IReadOnlyDictionary<string, string>? playback = null)
    {
        if (request.Query is null || request.Query.Length > 100 || request.Category is < 0 or > 3 and not LibraryCatalog.Music || request.Page is < 0 or > 100000 || request.ParentId?.Length > 128)
            throw new InvalidDataException("Invalid library search");
        // Keep artwork paths internal; only bounded thumbnail bytes leave the host.
        var all = source.ToArray();
        var items = all.Where(x => x.Available && !x.IsVirtual && !x.IsExtra).ToArray();
        var parent = request.ParentId is null ? null : items.FirstOrDefault(x => x.Id == request.ParentId);
        if (request.ParentId is not null && parent is null) throw new InvalidDataException("This title is no longer available. Return to all titles.");
        var location = request.Home ? "Home" : parent is null ? "All titles" : parent.DisplayTitle + (request.Season is null ? "" : $" · Season {request.Season}");
        var publicItems = items.Select(x => new MediaItem(x.Id, "", x.Title, x.Kind, x.Year, x.Series, x.Season, x.Episode,
            Overview: x.Overview, EpisodeTitle: x.EpisodeTitle, EpisodeOverview: x.EpisodeOverview,
            ShowId: LibraryIdentity.ShowKey(x), EpisodeEnd: x.EpisodeEnd, Poster: x.Poster,
            SeriesPoster: x.SeriesPoster, SeasonPoster: x.SeasonPoster, EpisodePoster: x.EpisodePoster,
            GeneratedEpisodePoster: x.GeneratedEpisodePoster, PosterSource: x.PosterSource, RuntimeMinutes: x.RuntimeMinutes,
            Artist: x.Artist, DurationSeconds: x.DurationSeconds)).ToArray();
        // All lists videos; music has its own category. An album opened from it keeps browsing within it.
        var visible = publicItems.Where(x => request.Category switch { 1 => x.Kind == "Movie", 2 => x.Kind == "Show", 3 => x.Kind == "Anime",
            LibraryCatalog.Music => x.IsMusic, _ => parent is not null || !x.IsMusic });
        var home = request.Home ? LibraryHome.Browse(all, playback, request.Seed) : null;
        var cards = home?.Select(entry => entry.Card).ToList() ?? LibraryCatalog.Search(visible, request.Query, parent is null ? null : LibraryIdentity.ShowKey(publicItems.First(x => x.Id == parent.Id)), parent?.Kind, request.Season);
        var page = Math.Min(request.Page, Math.Max(0, (cards.Count - 1) / PageSize));
        var result = new LibraryBrowsePage(cards.Skip(page * PageSize).Take(PageSize).Select((c, index) => new SharedLibraryEntry(c.Media.Id,
            Limit(string.IsNullOrWhiteSpace(c.DisplayTitle) ? c.Media.Title : c.DisplayTitle, 180), Limit(c.Caption, 240), c.Level, c.Season, Limit(c.Media.DetailOverview, 400),
            thumbnail?.Invoke(c.Poster) is { Length: <= 5400 } image ? image : null, c.Media.Kind, c.Media.Episode, c.Media.RuntimeMinutes,
            home?[page * PageSize + index].Section, home?[page * PageSize + index].PositionMs ?? 0)).ToArray(), cards.Count, page, Limit(location, 240));
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
