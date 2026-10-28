using System.Text.Json;

namespace Watchroom.Core;

// A show or film added from a metadata search before any of its files are in the library. Its episodes, or the film
// itself, are placeholders in the catalog until a scan finds the files. Seasons are the seasons whose episodes count as
// missing; KnownSeasons are every season the provider listed at the last refresh, tracked or not.
public record WantedTitle(string Id, string Provider, int ProviderId, string Type, string Kind, string Title, int? Year,
    int[] Seasons, bool AddNewSeasons, long AddedAt, long FetchedAt = 0, string? OriginalTitle = null, string? Overview = null,
    string? Poster = null, string? ReleaseDate = null, WantedSeason[]? KnownSeasons = null, Dictionary<string, string>? ProviderIds = null,
    WantedCandidate? Candidate = null, string[]? Rejected = null)
{
    public bool IsMovie => Type == "movie";
}
public record WantedSeason(int Number, int Episodes, int Aired);
// Files that look like a wanted title without settling it, such as two folders of one name or files with no year.
// Key is the show or film they belong to now; nothing links until the user confirms.
public record WantedCandidate(string Key, string Path, string Reason);
public record WantedProgress(int OnDisk, int Missing, int Upcoming, MediaItem? NextUp, string? NextUpStatus);

public sealed partial class LibraryStore
{
    private const string WantedPrefix = "wanted:";
    public List<WantedTitle> Wanted() => SettingsWithPrefix(WantedPrefix).Values.Select(WantedTitles.Read).OfType<WantedTitle>().ToList();
    public WantedTitle? Wanted(string id) => Setting(WantedPrefix + id) is { } json ? WantedTitles.Read(json) : null;
    public void SaveWanted(WantedTitle title) => Setting(WantedPrefix + title.Id, Wire.Serialize(title));
    internal void RemoveSettings(IEnumerable<string> keys)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM settings WHERE key=$k";
        var parameter = command.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var key in keys) { parameter.Value = key; command.ExecuteNonQuery(); }
        transaction.Commit();
    }
    internal void ForgetWanted(string id) => RemoveSettings([WantedPrefix + id]);
    // Files linked to wanted titles by the most recent scan, as "Title · what arrived" lines.
    public string[] LastWantedArrivals { get; internal set; } = [];
}

public static class WantedTitles
{
    private static readonly string[] Providers = ["tmdb", "tvdb", "imdb", "tvmaze"];
    // A wanted show takes the key any show identified by the same provider would have, so files that gain that identity group with it.
    public static string Key(string provider, string type, int id) => type == "movie" ? "wanted-" + LibraryIdentity.Hash($"{provider}:movie:{id}") : "show-" + LibraryIdentity.Hash(provider + ":" + id);
    internal static WantedTitle? Read(string json)
    {
        try { return Expire(Wire.Read<WantedTitle>(json)); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return null; }
    }
    // Text read from TMDB follows the same retention limit as the library; a refresh fills it in again.
    private static WantedTitle Expire(WantedTitle title) => title.Provider == "tmdb" && title.FetchedAt > 0 &&
        Wire.Now - title.FetchedAt > (long)TmdbRetention.MaximumAge.TotalMilliseconds ? title with { Overview = null, OriginalTitle = null } : title;
    private static bool Upcoming(string? date) => !DateOnly.TryParse(date, out var day) || day > DateOnly.FromDateTime(DateTime.UtcNow);
    private static Dictionary<string, string> Ids(WantedTitle title)
    {
        var ids = new Dictionary<string, string>(title.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        ids[title.Provider] = title.ProviderId.ToString();
        return ids;
    }
    private static string? Source(WantedTitle title) => title.Provider switch
    {
        "tmdb" => $"https://www.themoviedb.org/{title.Type}/{title.ProviderId}", "tvmaze" => $"https://www.tvmaze.com/shows/{title.ProviderId}", _ => null
    };
    public static WantedTitle FromMatch(MetadataMatch match, string kind, int[] seasons, bool addNewSeasons, string? poster, MetadataSeason[]? details = null) =>
        new(Key(match.Provider, match.Type, match.Id), match.Provider, match.Id, match.Type, kind, match.Title, int.TryParse(match.Year, out var year) ? year : null,
            seasons, addNewSeasons, Wire.Now, Wire.Now, match.OriginalTitle, string.IsNullOrWhiteSpace(match.Overview) ? null : match.Overview, poster, match.ReleaseDate,
            details?.Select(Summary).ToArray(), match.ProviderIds is null ? null : new(match.ProviderIds, StringComparer.OrdinalIgnoreCase));
    public static WantedSeason Summary(MetadataSeason season) => new(season.Number, season.Episodes.Count(e => DateOnly.TryParse(e.AirDate, out _)), season.Episodes.Count(e => !Upcoming(e.AirDate)));

    // The film, or the show with no particular episode, as the catalog stores it.
    public static MediaItem Placeholder(WantedTitle title, string directory) => new(title.Id, Path.Combine(directory, ".missing", title.Id), title.Title, title.Kind, title.Year,
        Series: title.IsMovie ? null : title.Title, Poster: title.Poster, Overview: title.Overview, Available: false, Matched: true, SeriesPoster: title.IsMovie ? null : title.Poster,
        PosterSource: title.Poster is null ? null : Source(title), AirDate: title.IsMovie ? title.ReleaseDate : null, MetadataProvider: title.Provider, MetadataId: title.ProviderId,
        MetadataFetchedAt: Wire.Now, MetadataKind: title.Kind, MetadataType: title.Type, ShowId: title.IsMovie ? null : title.Id, ProviderIds: Ids(title),
        OriginalTitle: title.OriginalTitle, IsVirtual: true, ArtworkFetchedAt: Wire.Now, SourceLibraryKind: title.Kind);
    private static string EpisodeKey(WantedTitle title, int season, int episode) => "missing-" + LibraryIdentity.Hash($"{title.Id}:{season}:{episode}");
    // Provider text and artwork follow the retention limit; the title, kind, identity and episode numbers are what the user
    // chose to track, so expiring them would turn a show's placeholder into something else.
    private static readonly string[] RetainedFields = ["overview", "originalTitle", "seasonTitle", "seasonOverview", "episodeTitle", "episodeOverview", "airDate", "runtimeMinutes", .. TmdbRetention.PosterFields];
    private static void Store(LibraryStore library, WantedTitle title, MediaItem item) =>
        library.Save(TmdbRetention.Record(item with { Overview = null, OriginalTitle = null, SeasonTitle = null, SeasonOverview = null, EpisodeTitle = null, EpisodeOverview = null,
            AirDate = null, RuntimeMinutes = null, Poster = null, SeriesPoster = null, PosterSource = null, TmdbFields = null }, item, title.Provider, RetainedFields));

    // Adds the title with a placeholder for every tracked episode. Seasons up to watchedThrough, or the film when seen, start out watched.
    public static WantedTitle Add(LibraryStore library, string directory, WantedTitle title, MetadataSeason[] seasons, int watchedThrough = 0, bool seen = false)
    {
        title = title with { KnownSeasons = title.IsMovie ? null : seasons.Select(Summary).ToArray(), FetchedAt = Wire.Now };
        library.SaveWanted(title);
        Reconcile(library, directory, title, seasons);
        var watched = title.IsMovie ? seen ? [title.Id] : [] : library.InShow(Placeholder(title, directory))
            .Where(x => x.IsVirtual && x.Season is > 0 && x.Season <= watchedThrough).Select(x => x.Id).ToArray();
        if (watched.Length > 0) library.SetSettings(watched.Select(id => new KeyValuePair<string, string>("watched:" + id, "true")));
        return title;
    }

    // Brings the placeholders in line with the tracked seasons. With provider seasons it also adds new episodes and, when
    // the title follows new seasons, starts tracking seasons it hadn't seen before; without them it only removes. Episodes
    // that now have a file lose their placeholder, which passes its watched mark to the file.
    public static WantedTitle Reconcile(LibraryStore library, string directory, WantedTitle title, MetadataSeason[]? seasons = null)
    {
        if (title.IsMovie)
        {
            if (library.Get(title.Id) is not { IsVirtual: false }) Store(library, title, Placeholder(title, directory));
            return title;
        }
        if (seasons is not null)
        {
            var known = title.KnownSeasons?.Select(s => s.Number).ToHashSet();
            var tracked = title.Seasons.Concat(title.AddNewSeasons && known is not null ? seasons.Select(s => s.Number).Where(n => n > 0 && !known.Contains(n)) : []).Distinct().Order().ToArray();
            title = title with { Seasons = tracked, KnownSeasons = seasons.Select(Summary).ToArray(), FetchedAt = Wire.Now };
            library.SaveWanted(title);
        }
        var show = Placeholder(title, directory);
        var existing = library.InShow(show).ToArray();
        var physical = existing.Where(x => !x.IsVirtual && !x.IsExtra && x.NumberingConflict is null)
            .SelectMany(x => LibraryIdentity.EpisodeNumbers(x).Select(n => (Season: x.Season, Number: n, x.Id))).ToLookup(x => (x.Season, x.Number), x => x.Id);
        var keep = new HashSet<string>();
        if (seasons is not null)
            foreach (var season in seasons.Where(s => title.Seasons.Contains(s.Number))) foreach (var episode in season.Episodes)
            {
                if (physical.Contains(((int?)season.Number, episode.Number)) || !DateOnly.TryParse(episode.AirDate, out _)) continue;
                var key = EpisodeKey(title, season.Number, episode.Number); keep.Add(key);
                Store(library, title, show with { Id = key, Path = Path.Combine(directory, ".missing", key), Season = season.Number, Episode = episode.Number,
                    EpisodeTitle = episode.Title, EpisodeOverview = episode.Overview, SeasonTitle = season.Title, SeasonOverview = season.Overview,
                    AirDate = episode.AirDate, RuntimeMinutes = episode.Runtime });
            }
        var stale = existing.Where(x => x.IsVirtual && (seasons is not null ? !keep.Contains(x.Id) :
            x.Season is not { } number || !title.Seasons.Contains(number) || physical.Contains((x.Season, x.Episode ?? -1)))).ToArray();
        // The file of an episode marked watched before it arrived starts out watched.
        var settings = library.SettingsWithPrefix("watched:");
        var transfers = stale.Where(x => settings.GetValueOrDefault("watched:" + x.Id) == "true")
            .SelectMany(x => physical[(x.Season, x.Episode ?? -1)]).Where(id => settings.GetValueOrDefault("watched:" + id) != "true").Distinct()
            .Select(id => new KeyValuePair<string, string>("watched:" + id, "true")).ToArray();
        if (transfers.Length > 0) library.SetSettings(transfers);
        if (stale.Length > 0)
        {
            library.RemoveVirtual(stale.Select(x => x.Id));
            library.RemoveSettings(stale.SelectMany(x => new[] { "watched:" + x.Id, "position:" + x.Id, "played:" + x.Id }));
        }
        return title;
    }

    // Stops tracking: the placeholders go, and files that already arrived stay in the library as they are.
    public static void Remove(LibraryStore library, string directory, WantedTitle title)
    {
        var placeholders = (title.IsMovie ? library.ByIds([title.Id]) : library.InShow(Placeholder(title, directory))).Where(x => x.IsVirtual).Select(x => x.Id).ToArray();
        library.RemoveVirtual(placeholders);
        library.RemoveSettings(placeholders.SelectMany(id => new[] { "watched:" + id, "position:" + id, "played:" + id }));
        library.ForgetWanted(title.Id);
    }

    // How a show or film already in the library relates to a wanted title: the same provider identity, the same title and
    // year, the same title without a year to confirm it, or nothing. Any differing identity rules a match out.
    public enum Likeness { None, Title, Identity, Unconfirmed }
    public static Likeness Compare(WantedTitle title, IReadOnlyCollection<MediaItem> files)
    {
        var first = files.First();
        if (title.IsMovie != (first.Series is null) || title.IsMovie && first.MetadataType == "tv" || !title.IsMovie && first.MetadataType == "movie") return Likeness.None;
        var ids = Ids(title); var same = false;
        foreach (var provider in Providers)
        {
            if (!ids.TryGetValue(provider, out var wanted)) continue;
            var found = files.Select(x => LibraryIdentity.ProviderId(x, provider)).OfType<string>().Distinct().ToArray();
            if (found.Any(id => id != wanted)) return Likeness.None;
            same |= found.Length > 0;
        }
        if (same) return Likeness.Identity;
        var name = MetadataTitles.Normalize(first.Series ?? first.Title);
        if (name.Length == 0 || new[] { title.Title, title.OriginalTitle }.OfType<string>().All(x => MetadataTitles.Normalize(x) != name)) return Likeness.None;
        var year = files.Select(x => x.Year).FirstOrDefault(x => x is not null);
        return year is null || title.Year is null ? Likeness.Unconfirmed : year == title.Year ? Likeness.Title : Math.Abs(year.Value - title.Year.Value) == 1 ? Likeness.Unconfirmed : Likeness.None;
    }
    // Where a group of files lives, for showing to the user: the show's folder, or the film's file.
    public static string Location(IReadOnlyCollection<MediaItem> files) => files.Select(x => x.SeriesPath).FirstOrDefault(x => x is not null) ??
        (files.Count == 1 ? files.First().Path : Path.GetDirectoryName(files.First().Path) ?? files.First().Path);
    private static IEnumerable<IGrouping<string, MediaItem>> Groups(WantedTitle title, IEnumerable<MediaItem> all) => all
        .Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus && x.ExtraOf is null && (title.IsMovie ? x.Series is null : x.Series is not null))
        .GroupBy(LibraryIdentity.ShowKey).Where(g => g.Key != title.Id);

    // Run after a scan: files that certainly belong to a wanted title join it; files that only look like it are offered
    // to the user. Returns the number of records changed.
    public static int Attach(LibraryStore library, string directory)
    {
        var wanted = library.Wanted(); var arrivals = new List<string>();
        if (wanted.Count == 0) { library.LastWantedArrivals = []; return 0; }
        var all = library.All(); var changes = 0;
        foreach (var title in wanted)
        {
            var candidates = Groups(title, all).Where(g => !(title.Rejected ?? []).Contains(g.Key))
                .Select(g => (Files: g.ToArray(), Likeness: Compare(title, g.ToArray()))).Where(x => x.Likeness != Likeness.None).ToArray();
            var identified = candidates.Where(x => x.Likeness == Likeness.Identity).ToArray();
            var certain = identified.Length == 1 ? identified[0].Files : identified.Length == 0 && candidates.Length == 1 && candidates[0].Likeness == Likeness.Title ? candidates[0].Files : null;
            if (certain is not null)
            {
                var before = Progress(title, library.InShow(Placeholder(title, directory)), _ => false);
                changes += Link(library, directory, title, certain, confirmed: false);
                var after = library.Wanted(title.Id) ?? title;
                arrivals.Add($"{title.Title} · " + (title.IsMovie ? "the film arrived" : Arrived(Progress(after, library.InShow(Placeholder(after, directory)), _ => false).OnDisk - before.OnDisk)) + " in " + Location(certain));
                continue;
            }
            var best = identified.Length > 0 ? identified : candidates;
            WantedCandidate? candidate = best.Length == 0 ? null : new(LibraryIdentity.ShowKey(best[0].Files[0]), Location(best[0].Files),
                best.Length > 1 ? $"{best.Length} {(title.IsMovie ? "files" : "folders")} look like {title.Title}" :
                best[0].Files.All(x => x.Year is null) ? "The files have no year to confirm it" : $"The files say {best[0].Files.First(x => x.Year is not null).Year}, not {title.Year}");
            if (candidate != title.Candidate) { library.SaveWanted(title with { Candidate = candidate }); changes++; }
        }
        library.LastWantedArrivals = arrivals.ToArray();
        return changes;
    }
    private static string Arrived(int episodes) => episodes == 1 ? "1 episode arrived" : $"{Math.Max(0, episodes)} episodes arrived";

    // Joins files to a wanted title. A film's file takes its placeholder's watched mark and the title is done; a show's
    // files join the show, and their episodes' placeholders give way. Confirmed links are the user's choice and are kept.
    public static int Link(LibraryStore library, string directory, WantedTitle title, IEnumerable<MediaItem> files, bool confirmed = true)
    {
        var entries = files.Where(x => !x.IsVirtual && !x.IsExtra).ToArray(); var changes = 0;
        if (entries.Length == 0) return 0;
        MediaItem Identify(MediaItem entry)
        {
            var ids = new Dictionary<string, string>(entry.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var id in Ids(title)) ids.TryAdd(id.Key, id.Value);
            var identified = entry with { ProviderIds = ids, Kind = LibraryIdentity.Locked(entry, "Kind") ? entry.Kind : title.Kind, MetadataFetchedAt = 0,
                Matched = entry.Matched || confirmed, Year = LibraryIdentity.Locked(entry, "Year") ? entry.Year : title.Year ?? entry.Year };
            return entry.MetadataId is > 0 && entry.MetadataProvider is not null ? identified : identified with { MetadataProvider = title.Provider, MetadataId = title.ProviderId, MetadataType = title.Type };
        }
        if (title.IsMovie)
        {
            var file = entries.OrderByDescending(x => x.FileSize ?? 0).First();
            library.Save(Identify(file) with { Title = LibraryIdentity.Locked(file, "Title") ? file.Title : title.Title });
            var settings = library.SettingsWithPrefix("watched:");
            if (settings.GetValueOrDefault("watched:" + title.Id) == "true") library.Setting("watched:" + file.Id, "true");
            library.RemoveVirtual([title.Id]);
            library.RemoveSettings(["watched:" + title.Id, "position:" + title.Id, "played:" + title.Id]);
            library.ForgetWanted(title.Id);
            return 1;
        }
        foreach (var entry in entries.Where(x => !LibraryIdentity.Locked(x, "ShowId")))
        {
            library.Save(LibraryIdentity.Ensure(Identify(entry) with { ShowId = title.Id, Series = LibraryIdentity.Locked(entry, "Title") ? entry.Series : title.Title }));
            changes++;
        }
        library.SaveWanted(title with { Candidate = null });
        Reconcile(library, directory, library.Wanted(title.Id) ?? title);
        return changes;
    }
    // Links the files the user picked from a folder; null when the folder holds no library videos of the title's kind.
    public static (MediaItem[] Files, Likeness Likeness)? Folder(WantedTitle title, IEnumerable<MediaItem> all, string folder)
    {
        var files = all.Where(x => !x.IsVirtual && !x.IsExtra && x.ExtraOf is null && (title.IsMovie ? x.Series is null : x.Series is not null) &&
            LibraryStore.IsWithin(x.Path, folder)).ToArray();
        if (files.Length == 0) return null;
        var groups = files.GroupBy(LibraryIdentity.ShowKey).Select(g => Compare(title, g.ToArray())).ToArray();
        return (files, groups.Contains(Likeness.Identity) ? Likeness.Identity : groups.Contains(Likeness.Title) ? Likeness.Title : groups.Contains(Likeness.Unconfirmed) ? Likeness.Unconfirmed : Likeness.None);
    }
    public static WantedTitle Reject(LibraryStore library, WantedTitle title)
    {
        if (title.Candidate is not { } candidate) return title;
        var updated = title with { Candidate = null, Rejected = (title.Rejected ?? []).Append(candidate.Key).Distinct().ToArray() };
        library.SaveWanted(updated); return updated;
    }

    // What a wanted title has: episodes on disk and still missing or upcoming in its tracked seasons, and the first
    // episode in order that hasn't been watched.
    public static WantedProgress Progress(WantedTitle title, IEnumerable<MediaItem> members, Func<string, bool> watched)
    {
        var items = members.Where(x => !x.IsExtra && !x.IsBonus).ToArray();
        if (title.IsMovie)
        {
            var film = items.FirstOrDefault();
            return film is null || !film.IsVirtual ? new(1, 0, 0, null, null) : Upcoming(film.AirDate) ? new(0, 0, 1, film, "Upcoming") : new(0, 1, 0, film, "Missing");
        }
        var tracked = items.Where(x => x.Season is { } season && title.Seasons.Contains(season)).ToArray();
        var onDisk = tracked.Where(x => !x.IsVirtual && x.NumberingConflict is null).SelectMany(x => LibraryIdentity.EpisodeNumbers(x).Select(n => (x.Season, n))).Distinct().Count();
        var missing = tracked.Count(x => x.IsVirtual && !Upcoming(x.AirDate)); var upcoming = tracked.Count(x => x.IsVirtual && Upcoming(x.AirDate));
        var next = LibraryCatalog.PreferredFiles(tracked.Where(x => x.Season > 0 && x.Episode is not null)).OrderBy(x => x.Season).ThenBy(x => x.Episode).FirstOrDefault(x => !watched(x.Id));
        return new(onDisk, missing, upcoming, next, next is null || !next.IsVirtual ? null : Upcoming(next.AirDate) ? "Upcoming" : "Missing");
    }

    // The provider's seasons for a show, or none for a film.
    public static async Task<(MetadataMatch Details, MetadataSeason[] Seasons)> FetchAsync(MetadataMatch match, MetadataClient? tmdb, HttpClient http, CancellationToken ct)
    {
        if (match.Provider == "tmdb")
        {
            if (tmdb is null) throw new InvalidOperationException("TMDB needs a saved key.");
            var details = (await tmdb.DetailsAsync(match, ct)) with { Source = $"https://www.themoviedb.org/{match.Type}/{match.Id}" };
            return (details, match.Type == "tv" ? await tmdb.SeasonsAsync(match.Id, ct) : []);
        }
        if (match.Provider == "tvmaze") return (match, await SeriesMetadata.TvmazeSeasons(http, match.Id, ct));
        return (match, []);
    }
    // Details and seasons of a search result, for choosing what to track before adding it.
    public static async Task<(MetadataMatch Details, MetadataSeason[] Seasons)> DetailsAsync(LibraryStore library, MetadataMatch match, string? token, CancellationToken ct)
    {
        var options = MetadataOptions.Load(library);
        using var scope = MetadataHttp.BeginScope(library, false, options);
        using var tmdb = match.Provider == "tmdb" && TmdbCredentials.Resolve(token) is not null ? new MetadataClient(token, options: options) : null;
        using var http = MetadataHttp.CreateClient();
        return await FetchAsync(match, tmdb, http, ct);
    }
    // Looks for new seasons and episodes of every wanted title whose details are older than the refresh interval,
    // or of one title when asked. Failures leave the placeholders as they are.
    public static async Task<string> RefreshAsync(LibraryStore library, string directory, string? token, IProgress<string>? progress, CancellationToken ct,
        bool force = false, string? only = null, MetadataClient? metadata = null, HttpClient? client = null)
    {
        var options = MetadataOptions.Load(library);
        var due = library.Wanted().Where(x => only is null ? force || x.FetchedAt < Wire.Now - (long)TimeSpan.FromDays(Math.Max(1, options.RefreshDays)).TotalMilliseconds : x.Id == only).ToArray();
        if (due.Length == 0) return "";
        using var scope = MetadataHttp.BeginScope(library, force || only is not null, options);
        using var ownedTmdb = metadata is null && TmdbCredentials.Resolve(token) is not null ? new MetadataClient(token, options: options) : null;
        using var ownedHttp = client is null ? MetadataHttp.CreateClient() : null;
        var tmdb = metadata ?? ownedTmdb; var http = client ?? ownedHttp!;
        int refreshed = 0, failed = 0;
        foreach (var title in due)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("Checking for new episodes · " + title.Title);
            try
            {
                var (details, seasons) = await FetchAsync(new(title.ProviderId, title.Type, title.Title, title.Year?.ToString(), null, title.Overview ?? "", title.Kind,
                    title.OriginalTitle, title.ProviderIds, Provider: title.Provider, ReleaseDate: title.ReleaseDate), tmdb, http, ct);
                var current = library.Wanted(title.Id) ?? title;
                current = current with { Title = details.Title, OriginalTitle = details.OriginalTitle ?? current.OriginalTitle, ReleaseDate = details.ReleaseDate ?? current.ReleaseDate,
                    Overview = string.IsNullOrWhiteSpace(details.Overview) ? current.Overview : details.Overview, Year = int.TryParse(details.Year, out var year) ? year : current.Year,
                    ProviderIds = details.ProviderIds ?? current.ProviderIds, FetchedAt = Wire.Now };
                library.SaveWanted(current);
                Reconcile(library, directory, current, current.IsMovie ? null : seasons.Length > 0 ? seasons : null);
                refreshed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or TaskCanceledException)
            { failed++; MetadataDiagnostics.Failure("wanted", title.Title, title.Provider, ex); }
        }
        return $"Wanted titles: {refreshed} checked · {failed} failures.";
    }
}
