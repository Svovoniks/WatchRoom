using System.Globalization;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// Auto-download of new episodes. A followed show is checked against its metadata provider's episode list; once an
// episode has aired and isn't on disk, the show's interactive acquisition plugin is asked for its releases (a read-only
// search). Found releases wait for the user's OK, or download after a countdown when the user chose that, and only the
// release IDs the user was shown are downloaded. The same checks run in the open app and, while it is closed, in the
// background check Windows starts every hour (see PLUGINS.md and README.md).
// DesktopIcon: while episodes wait for the user, an icon on the desktop that downloads them when opened.
public record FollowSettings(bool Enabled = false, bool Countdown = false, int CountdownMinutes = 5, bool DesktopIcon = false)
{
    public static readonly int[] CountdownChoices = [2, 5, 15];
    public int Minutes => CountdownChoices.Contains(CountdownMinutes) ? CountdownMinutes : 5;
}
public record FollowRelease(string Id, string Title);
public static class FollowStatus
{
    // Aired and not on disk; its releases are searched for until one turns up or a week has passed.
    public const string Waiting = "waiting";
    // Releases were found and wait for the user (or for the countdown).
    public const string Pending = "pending";
    public const string Downloading = "downloading";
    // The plugin accepted the releases; the files arrive when its downloader finishes.
    public const string Sent = "sent";
    public const string Arrived = "arrived";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string NotFound = "not-found";
    // Sent a while ago and still not on disk.
    public const string Stalled = "stalled";
    public static bool Settled(string status) => status is Arrived or Skipped;
}
public record FollowEpisode(int Season, int Episode, string? AirDate, string Status, long SeenAt, long NextCheckAt = 0, int Attempts = 0,
    FollowRelease[]? Releases = null, AcquisitionOptions? Options = null, string? Token = null, long NotifiedAt = 0, long StartAt = 0,
    long SentAt = 0, string? Target = null, string? Message = null, long UpdatedAt = 0)
{
    public string Code => $"S{Season:00}E{Episode:00}";
}
// One aired or announced episode in the provider's list. Only numbers and dates are kept.
public record FollowListing(int Season, int Episode, string? AirDate);
// Since: the day following started (yyyy-MM-dd); episodes that aired before it are left alone, except the episodes of
// BacklogSeason, which the user asked to get too. Options: the plugin options chosen at setup, used for a season that has
// none of its own saved.
public record FollowedShow(string Key, string Title, string Kind, int? Year, string PluginId, AcquisitionOptions? Options, string Since,
    int? BacklogSeason, long AddedAt, FollowEpisode[] Episodes, FollowListing[]? Listing = null, string? ListingProvider = null, long ListedAt = 0,
    string? Problem = null, long CheckedAt = 0);
public record FollowEpisodeRef(string Show, int Season, int Episode);
// A notification: Token identifies its episodes when the user acts on it.
public record FollowNotice(string Kind, string Token, string Heading, string[] Lines, string? Footer, long StartAt, FollowEpisodeRef[] Episodes);
public record FollowCheckResult(FollowNotice? Ready, FollowNotice? Attention, string Summary);
public record FollowSource(string Provider, int Id, MetadataSeason[] Seasons);
public record FollowOutcome(string Show, string Name, bool Success, string Message);

public static class FollowStore
{
    public const string Prefix = "follow:", SettingsKey = "follow-settings", CheckedKey = "follow-checked";
    public static FollowSettings Settings(LibraryStore library)
    {
        try { return library.Setting(SettingsKey) is { } json ? Wire.Read<FollowSettings>(json) : new(); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return new(); }
    }
    public static void SaveSettings(LibraryStore library, FollowSettings settings) => library.Setting(SettingsKey, Wire.Serialize(settings));
    public static List<FollowedShow> Shows(LibraryStore library) => library.SettingsWithPrefix(Prefix).Values.Select(Read).OfType<FollowedShow>().OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    public static FollowedShow? Show(LibraryStore library, string key) => library.Setting(Prefix + key) is { } json ? Read(json) : null;
    public static void Save(LibraryStore library, FollowedShow show) => library.Setting(Prefix + show.Key, Wire.Serialize(show));
    public static void Remove(LibraryStore library, string key) { using var gate = FollowLock.State(library); library.RemoveSettings([Prefix + key]); }
    public static long LastCheck(LibraryStore library) => long.TryParse(library.Setting(CheckedKey), out var at) ? at : 0;
    private static FollowedShow? Read(string json)
    {
        try { return Wire.Read<FollowedShow>(json); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return null; }
    }
    // Changes a followed show while no other process (the app, a background check or a notification's button) changes it.
    public static FollowedShow? Update(LibraryStore library, string key, Func<FollowedShow, FollowedShow?> change)
    {
        using var gate = FollowLock.State(library);
        if (Show(library, key) is not { } show) return null;
        var changed = change(show);
        if (changed is not null) Save(library, changed);
        return changed;
    }
    // Starts following a show. A show followed again keeps what it already knows about its episodes.
    public static FollowedShow Follow(LibraryStore library, MediaItem member, string pluginId, AcquisitionOptions? options, int? backlogSeason, DateOnly today)
    {
        using var gate = FollowLock.State(library);
        var key = LibraryIdentity.ShowKey(member);
        var existing = Show(library, key);
        var show = new FollowedShow(key, member.Series ?? member.Title, member.Kind, member.Year, pluginId, options, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            backlogSeason, Wire.Now, existing?.Episodes ?? [], existing?.Listing, existing?.ListingProvider, existing?.ListedAt ?? 0);
        Save(library, show);
        return show;
    }
}

// Followed shows change in the app, in the hourly background check and in a notification button's process. State
// changes are short and serialized by one lock file; a whole check holds another, so two checks never overlap.
public sealed class FollowLock : IDisposable
{
    private readonly FileStream stream;
    private FollowLock(FileStream stream) => this.stream = stream;
    public void Dispose() => stream.Dispose();
    private static FollowLock? Take(string path, TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            try { return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(50);
            }
        }
    }
    public static FollowLock State(LibraryStore library) =>
        Take(Path.Combine(library.DataDirectory, "follow-state.lock"), TimeSpan.FromSeconds(20)) ?? throw new IOException("Auto-download is busy in another BlackPearl window; try again.");
    public static FollowLock? Check(LibraryStore library) => Take(Path.Combine(library.DataDirectory, "follow-check.lock"), TimeSpan.Zero);
}

public sealed class EpisodeFollowing
{
    public delegate Task<FollowSource?> SeasonSource(MediaItem member, CancellationToken ct);
    // How often the provider's episode list is read again, how long releases are searched for after an episode airs, and
    // how long a sent episode may take to reach the library before it counts as stalled.
    public static readonly TimeSpan ListingAge = TimeSpan.FromHours(3), GiveUpAfter = TimeSpan.FromDays(7), StallAfter = TimeSpan.FromDays(3);
    private readonly LibraryStore library;
    private readonly IReadOnlyList<AcquisitionPlugin> plugins;
    private readonly SeasonSource seasons;
    private readonly Func<AcquisitionPlugin, AcquisitionRequest, CancellationToken, Task<AcquisitionResult>> run;
    private readonly Func<DateTimeOffset> clock;

    public EpisodeFollowing(LibraryStore library, IReadOnlyList<AcquisitionPlugin> plugins, SeasonSource seasons,
        Func<AcquisitionPlugin, AcquisitionRequest, CancellationToken, Task<AcquisitionResult>>? run = null, Func<DateTimeOffset>? clock = null)
    {
        this.library = library; this.plugins = plugins; this.seasons = seasons; this.run = run ?? ContentAcquisition.RunAsync; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    public LibraryStore Library => library;
    private long Now => clock().ToUnixTimeMilliseconds();
    private DateOnly Today => DateOnly.FromDateTime(clock().UtcDateTime);
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public static bool IsToken(string? token) => token is { Length: 32 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static TimeSpan Backoff(int attempts) => TimeSpan.FromHours(Math.Min(6, 1 << Math.Clamp(attempts - 1, 0, 3)));
    private static LibraryFolder[] Folders(LibraryStore library)
    {
        try { return Wire.Read<LibraryFolder[]>(library.Setting("folders") ?? "[]"); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return []; }
    }
    // The plugin a followed show downloads with: interactive, since only those can show releases before downloading.
    public static AcquisitionPlugin? PluginFor(IEnumerable<AcquisitionPlugin> plugins, FollowedShow show) =>
        plugins.FirstOrDefault(x => x.Interactive && x.Id.Equals(show.PluginId, StringComparison.OrdinalIgnoreCase) && x.Supports(ContentAcquisition.ContentType(show.Kind)));

    // Reads the provider's episode list for a show: TMDB or TVmaze, in the user's provider order, by the IDs the
    // show's files or wanted title already have.
    public static async Task<FollowSource?> ProviderSeasonsAsync(LibraryStore library, MediaItem member, string? token, CancellationToken ct)
    {
        var options = MetadataOptions.Load(library);
        using var scope = MetadataHttp.BeginScope(library, true, options);
        var key = LibraryIdentity.ShowKey(member);
        var members = library.All().Where(x => x.Series is not null && LibraryIdentity.ShowKey(x) == key).Prepend(member).ToArray();
        var wanted = library.Wanted(key);
        // The provider the show's files were matched with comes first: its numbering is the one they follow.
        var matched = members.Where(x => !x.IsVirtual).Select(x => x.MetadataProvider).FirstOrDefault(x => x is "tmdb" or "tvmaze");
        foreach (var provider in options.ProviderOrder.Prepend(matched).OfType<string>().Where(p => p is "tmdb" or "tvmaze").Distinct())
        {
            var id = members.Select(x => LibraryIdentity.ProviderId(x, provider)).Append(wanted is null ? null : wanted.Provider == provider ? wanted.ProviderId.ToString(CultureInfo.InvariantCulture)
                : wanted.ProviderIds?.GetValueOrDefault(provider)).Select(x => int.TryParse(x, out var n) && n > 0 ? n : 0).FirstOrDefault(x => x > 0);
            if (id == 0) continue;
            MetadataSeason[] details;
            if (provider == "tmdb")
            {
                if (TmdbCredentials.Resolve(token) is null) continue;
                using var tmdb = new MetadataClient(token, options: options);
                details = member.NumberingOrder == "dvd" ? await tmdb.DvdSeasonsAsync(id, ct) : await tmdb.SeasonsAsync(id, ct);
            }
            else
            {
                using var http = MetadataHttp.CreateClient();
                details = await SeriesMetadata.TvmazeSeasons(http, id, ct);
            }
            if (details.Length > 0) return new(provider, id, details);
        }
        return null;
    }

    // Brings every followed show up to date: new episodes that aired, episodes that reached the library, and releases for
    // the episodes still waiting. Ready lists the episodes whose releases were found in this check, for one notification.
    public async Task<FollowCheckResult> CheckAsync(CancellationToken ct)
    {
        using var running = FollowLock.Check(library);
        if (running is null) return new(null, null, "Another check is already running.");
        var settings = FollowStore.Settings(library);
        var shows = FollowStore.Shows(library);
        if (!settings.Enabled || shows.Count == 0) return new(null, null, settings.Enabled ? "No shows are followed." : "Auto-download is off.");
        var items = library.All().Where(x => !x.IsMusic).ToList(); var folders = Folders(library);
        var token = NewToken(); var attention = new List<(FollowedShow Show, FollowEpisode Episode)>();
        int checkedShows = 0, problems = 0;
        foreach (var show in shows)
        {
            ct.ThrowIfCancellationRequested();
            try { attention.AddRange(await CheckShow(show.Key, items, folders, settings, token, ct)); checkedShows++; }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
            {
                problems++;
                FollowStore.Update(library, show.Key, x => x with { Problem = "The last check failed: " + ex.Message, CheckedAt = Now });
                PlaybackDiagnostics.Record("follow-check-failed", new { show = show.Title, error = ex.GetType().Name });
            }
        }
        library.Setting(FollowStore.CheckedKey, Now.ToString(CultureInfo.InvariantCulture));
        var ready = Notice(token);
        return new(ready, AttentionNotice(attention), $"Checked {checkedShows} {(checkedShows == 1 ? "show" : "shows")}" +
            (ready is null ? "" : $" · {ready.Episodes.Length} ready to download") + (problems == 0 ? "" : $" · {problems} failed"));
    }

    private async Task<List<(FollowedShow, FollowEpisode)>> CheckShow(string key, List<MediaItem> items, LibraryFolder[] folders, FollowSettings settings, string token, CancellationToken ct)
    {
        var attention = new List<(FollowedShow, FollowEpisode)>();
        var show = FollowStore.Show(library, key);
        if (show is null) return attention;
        var members = items.Where(x => x.Series is not null && LibraryIdentity.ShowKey(x) == key).ToList();
        if (members.Count == 0) { FollowStore.Update(library, key, x => x with { Problem = "This show isn't in your library any more.", CheckedAt = Now }); return attention; }
        var files = members.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus).ToArray();
        var member = files.FirstOrDefault() ?? members[0];

        // The provider's episode list, read again every few hours.
        string? problem = null;
        var listing = show.Listing; var provider = show.ListingProvider; var listedAt = show.ListedAt;
        if (listing is null || Now - show.ListedAt > (long)ListingAge.TotalMilliseconds)
        {
            try
            {
                if (await seasons(member, ct) is { } source)
                {
                    listing = source.Seasons.Where(s => s.Number > 0).SelectMany(s => s.Episodes.Select(e => new FollowListing(s.Number, e.Number, e.AirDate))).ToArray();
                    provider = source.Provider; listedAt = Now;
                }
                else if (listing is null) problem = "BlackPearl doesn't know this show's episodes yet. Match it with TMDB or TVmaze using Find artwork / fix match.";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException or TaskCanceledException or KeyNotFoundException)
            { problem = "Couldn't read the show's episode list: " + ex.Message; }
        }
        listing ??= [];
        // Files matched with another provider still fit when their season and episode numbers are in this list.
        var disk = SeriesMetadata.OnDisk(files, listing.Select(x => (x.Season, x.Episode)), provider ?? "", sameProvider: false);
        bool OnDisk(FollowEpisode e) => disk.Physical.Contains((e.Season, e.Episode)) || disk.Unread.Contains(e.Episode);
        if (listing.Length > 0 && !disk.Fits)
            problem ??= $"Your files of this show are numbered differently from {(provider == "tvmaze" ? "TVmaze" : "TMDB")}, so BlackPearl can't tell which episodes are new.";

        // Episodes that reached the library, sent episodes that never did, and newly aired ones.
        var today = Today; var since = DateOnly.TryParse(show.Since, CultureInfo.InvariantCulture, out var start) ? start : today;
        var stalled = new List<FollowEpisode>();
        FollowEpisode[] Refresh(FollowEpisode[] tracked)
        {
            var result = tracked.Select(e =>
            {
                if (FollowStatus.Settled(e.Status) || e.Status == FollowStatus.Downloading) return e;
                if (OnDisk(e) || e.Status is FollowStatus.Sent or FollowStatus.Stalled && InFolder(e))
                    return e with { Status = FollowStatus.Arrived, Token = null, UpdatedAt = Now, Message = null };
                if (e.Status == FollowStatus.Sent && Now - e.SentAt > (long)StallAfter.TotalMilliseconds)
                {
                    var stuck = e with { Status = FollowStatus.Stalled, UpdatedAt = Now, Message = "Sent to the plugin " + (int)StallAfter.TotalDays + " days ago and not in your library yet." };
                    stalled.Add(stuck); return stuck;
                }
                return e;
            }).ToList();
            if (disk.Fits)
                foreach (var entry in listing)
                {
                    if (!DateOnly.TryParse(entry.AirDate, CultureInfo.InvariantCulture, out var aired) || aired > today) continue;
                    if (aired < since && entry.Season != show.BacklogSeason) continue;
                    if (result.Any(e => e.Season == entry.Season && e.Episode == entry.Episode)) continue;
                    var fresh = new FollowEpisode(entry.Season, entry.Episode, entry.AirDate, FollowStatus.Waiting, Now, UpdatedAt: Now);
                    if (!OnDisk(fresh)) result.Add(fresh);
                }
            return result.OrderBy(e => e.Season).ThenBy(e => e.Episode).ToArray();
        }
        show = FollowStore.Update(library, key, x => x with { Episodes = Refresh(x.Episodes), Listing = listing, ListingProvider = provider, ListedAt = listedAt, CheckedAt = Now, Problem = problem }) ?? show;
        attention.AddRange(stalled.Select(e => (show, e)));

        var plugin = PluginFor(plugins, show);
        if (plugin is null)
        {
            FollowStore.Update(library, key, x => x with { Problem = problem ?? "The plugin it downloads with isn't installed. Choose Change source to pick another." });
            return attention;
        }
        // Releases for the episodes that are due, one search per season.
        foreach (var group in show.Episodes.Where(e => e.Status == FollowStatus.Waiting && e.NextCheckAt <= Now).GroupBy(e => e.Season))
        {
            ct.ThrowIfCancellationRequested();
            var due = group.ToArray();
            var request = ContentAcquisition.ForEpisodes(member, group.Key, due.Select(e => new AcquisitionEpisode(e.Season, e.Episode, null, e.AirDate, false)).ToArray(), items, folders);
            if (request is null) { FollowStore.Update(library, key, x => x with { Problem = $"There's no library folder for {ContentAcquisition.ContentTypeName(ContentAcquisition.ContentType(show.Kind)).ToLowerInvariant()} to download into." }); break; }
            var options = Options(plugin, request, show);
            var (result, used) = await Releases(plugin, request, options, ct);
            var picks = result.Success && result.Stage == "releases" ? Pick(result.Choices ?? [], used.Query, ContentAcquisition.ExistingNames(request, items), due) : [];
            // A title found by searching is saved for the season, so later checks go straight to its releases.
            if (picks.Count > 0 && used != options && !string.IsNullOrWhiteSpace(used.SourceUrl)) library.Setting(ContentAcquisition.PreferenceKey(plugin.Id, request), Wire.Serialize(used));
            var startAt = settings.Countdown ? Now + (long)TimeSpan.FromMinutes(settings.Minutes).TotalMilliseconds : 0;
            var given = new List<FollowEpisode>();
            FollowStore.Update(library, key, x => x with
            {
                Episodes = x.Episodes.Select(e =>
                {
                    if (e.Status != FollowStatus.Waiting || !due.Any(d => d.Season == e.Season && d.Episode == e.Episode)) return e;
                    if (picks.TryGetValue((e.Season, e.Episode), out var pick))
                        return e with { Status = FollowStatus.Pending, Releases = [new(pick.Id, pick.Title)], Options = used, Token = token, NotifiedAt = Now, StartAt = startAt, UpdatedAt = Now,
                            Message = null, Attempts = e.Attempts + 1 };
                    var attempts = e.Attempts + 1;
                    var message = !result.Success ? result.Message : result.Stage == "shows" ? "Several provider titles could be this show. Choose one with Change source." : "No matching release yet.";
                    var aired = DateOnly.TryParse(e.AirDate, CultureInfo.InvariantCulture, out var day) ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() : e.SeenAt;
                    if (Now - Math.Max(aired, e.SeenAt) > (long)GiveUpAfter.TotalMilliseconds)
                    {
                        var lost = e with { Status = FollowStatus.NotFound, Attempts = attempts, UpdatedAt = Now, Message = "No release found in the week after it aired. " + message };
                        given.Add(lost); return lost;
                    }
                    return e with { Attempts = attempts, NextCheckAt = Now + (long)Backoff(attempts).TotalMilliseconds, UpdatedAt = Now, Message = message };
                }).ToArray()
            });
            attention.AddRange(given.Select(e => (show, e)));
        }
        return attention;
    }

    // Did a sent episode's file arrive in the folder it was sent to, before the library scanned it? A file written since
    // it was sent whose name is its release's name, give or take the episode number, counts.
    private static bool InFolder(FollowEpisode e)
    {
        if (e.Target is null || e.Releases is not { Length: > 0 } releases || !Directory.Exists(e.Target)) return false;
        try
        {
            return Directory.EnumerateFiles(e.Target, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Where(OpenedFiles.IsVideo).Any(path =>
                {
                    var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
                    var created = new DateTimeOffset(File.GetCreationTimeUtc(path)).ToUnixTimeMilliseconds();
                    var name = Path.GetFileNameWithoutExtension(path);
                    // Release names may or may not end in the file's extension.
                    return Math.Max(written, created) >= e.SentAt - 3_600_000 && releases.Any(r =>
                        (ContentAcquisition.NameDistance(r.Title, name) == 0 || ContentAcquisition.NameDistance(r.Title, Path.GetFileName(path)) == 0) &&
                        (ContentAcquisition.EpisodeInName(name) is not { } code || code == e.Code));
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // A season's options: those the picker saved for it, or else the ones chosen at setup. A provider title or episode
    // offset belongs to the season it was chosen for, so another season searches again.
    private AcquisitionOptions Options(AcquisitionPlugin plugin, AcquisitionRequest request, FollowedShow show)
    {
        try { if (library.Setting(ContentAcquisition.PreferenceKey(plugin.Id, request)) is { } json) return Wire.Read<AcquisitionOptions>(json); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        var options = show.Options ?? new AcquisitionOptions(request.Title);
        return options with { Query = string.IsNullOrWhiteSpace(options.Query) ? request.Title : options.Query, SourceUrl = null, EpisodeOffset = null,
            ProviderSeason = options.ProviderSeason is null ? null : request.Season };
    }
    // Lists the releases for a request: of the saved provider title, or of the title a search finds that is clearly this show.
    private async Task<(AcquisitionResult Result, AcquisitionOptions Used)> Releases(AcquisitionPlugin plugin, AcquisitionRequest request, AcquisitionOptions options, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.SourceUrl)) return (await run(plugin, request with { Action = "releases", Options = options }, ct), options);
        var search = options with { SourceUrl = null };
        var found = await run(plugin, request with { Action = "search", Options = search }, ct);
        if (!found.Success || found.Stage != "shows") return (found, search);
        if (ContentAcquisition.BestTitle(found.Choices ?? [], [request.Title, request.OriginalTitle, options.Query], request.Year) is not { } title) return (found, search);
        var chosen = search with { Query = title.Title, SourceUrl = title.Id };
        return (await run(plugin, request with { Action = "releases", Options = chosen }, ct), chosen);
    }
    // The best matching release of each due episode, as the picker's Select best matches would tick them.
    internal static Dictionary<(int, int), AcquisitionChoice> Pick(AcquisitionChoice[] choices, string? query, IReadOnlyCollection<string> existing, IReadOnlyList<FollowEpisode> due)
    {
        var picks = new Dictionary<(int, int), AcquisitionChoice>();
        foreach (var choice in ContentAcquisition.RankChoices(choices, query, existing).Where(x => x.Matches && !x.Assignable))
        {
            FollowEpisode? episode = null;
            if (Regex.Match(choice.EpisodeCode ?? "", @"(?i)^s(\d+)e(\d+)$") is { Success: true } code)
                episode = due.FirstOrDefault(e => e.Season == int.Parse(code.Groups[1].Value, CultureInfo.InvariantCulture) && e.Episode == int.Parse(code.Groups[2].Value, CultureInfo.InvariantCulture));
            else if (int.TryParse(choice.Episode, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) episode = due.FirstOrDefault(e => e.Episode == number);
            else if (due.Count == 1) episode = due[0];
            if (episode is not null) picks.TryAdd((episode.Season, episode.Episode), choice);
        }
        return picks;
    }

    // Episodes waiting for the user, across all followed shows.
    public IEnumerable<(FollowedShow Show, FollowEpisode Episode)> Pending() => FollowStore.Shows(library).SelectMany(s => s.Episodes.Where(e => e.Status == FollowStatus.Pending).Select(e => (s, e)));
    public bool Knows(string token) => IsToken(token) && Pending().Any(x => x.Episode.Token == token);
    public FollowEpisodeRef[] WithToken(string token) => IsToken(token) ? Pending().Where(x => x.Episode.Token == token).Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToArray() : [];
    // Countdowns that ran out, including those of a check that ended before its countdown did.
    public FollowEpisodeRef[] DueCountdowns() => Pending().Where(x => x.Episode.StartAt > 0 && x.Episode.StartAt <= Now)
        .Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToArray();

    // Skips episodes that are waiting for the user.
    public int Skip(IEnumerable<FollowEpisodeRef> episodes)
    {
        var count = 0;
        foreach (var show in episodes.GroupBy(x => x.Show))
            FollowStore.Update(library, show.Key, x => x with { Episodes = x.Episodes.Select(e =>
            {
                if (e.Status != FollowStatus.Pending || !show.Any(r => r.Season == e.Season && r.Episode == e.Episode)) return e;
                count++; return e with { Status = FollowStatus.Skipped, Token = null, StartAt = 0, UpdatedAt = Now };
            }).ToArray() });
        return count;
    }
    // Looks for an episode's releases again at the next check, after it failed, wasn't found or never arrived.
    public void Retry(FollowEpisodeRef episode) => FollowStore.Update(library, episode.Show, x => x with { Episodes = x.Episodes.Select(e =>
        e.Season == episode.Season && e.Episode == episode.Episode && e.Status is FollowStatus.Failed or FollowStatus.NotFound or FollowStatus.Stalled or FollowStatus.Skipped
            ? e with { Status = FollowStatus.Waiting, NextCheckAt = 0, Attempts = 0, Releases = null, Token = null, StartAt = 0, Message = null, SeenAt = Now, UpdatedAt = Now } : e).ToArray() });

    // Downloads the releases found for episodes that are waiting for the user: one acquire per season, with exactly the
    // release IDs the user was shown. An episode someone else already approved or skipped is left alone.
    public async Task<FollowOutcome[]> ApproveAsync(IEnumerable<FollowEpisodeRef> episodes, CancellationToken ct)
    {
        var outcomes = new List<FollowOutcome>();
        var items = library.All().Where(x => !x.IsMusic).ToList(); var folders = Folders(library);
        foreach (var group in episodes.GroupBy(x => x.Show))
        {
            var claimed = new List<FollowEpisode>();
            var show = FollowStore.Update(library, group.Key, x => x with { Episodes = x.Episodes.Select(e =>
            {
                if (e.Status != FollowStatus.Pending || e.Releases is not { Length: > 0 } || !group.Any(r => r.Season == e.Season && r.Episode == e.Episode)) return e;
                var taken = e with { Status = FollowStatus.Downloading, StartAt = 0, UpdatedAt = Now }; claimed.Add(taken); return taken;
            }).ToArray() });
            if (show is null || claimed.Count == 0) continue;
            foreach (var season in claimed.GroupBy(e => e.Season))
            {
                var batch = season.ToArray();
                var (success, message, target) = await Acquire(show, batch, items, folders, ct);
                outcomes.Add(new(show.Title, batch.Length == 1 ? $"{show.Title} {batch[0].Code}" : $"{show.Title} S{season.Key:00} · {batch.Length} episodes", success, message));
                FollowStore.Update(library, show.Key, x => x with { Episodes = x.Episodes.Select(e =>
                    e.Status != FollowStatus.Downloading || !batch.Any(b => b.Season == e.Season && b.Episode == e.Episode) ? e
                    : success ? e with { Status = FollowStatus.Sent, SentAt = Now, Target = target, Token = null, Message = message, UpdatedAt = Now }
                    : e with { Status = FollowStatus.Failed, Token = null, Message = message, UpdatedAt = Now }).ToArray() });
            }
        }
        return outcomes.ToArray();
    }
    private async Task<(bool Success, string Message, string? Target)> Acquire(FollowedShow show, FollowEpisode[] batch, List<MediaItem> items, LibraryFolder[] folders, CancellationToken ct)
    {
        if (PluginFor(plugins, show) is not { } plugin) return (false, "The plugin it downloads with isn't installed.", null);
        var member = items.Where(x => x.Series is not null && LibraryIdentity.ShowKey(x) == show.Key).OrderBy(x => x.IsVirtual).FirstOrDefault();
        if (member is null) return (false, "This show isn't in your library any more.", null);
        var request = ContentAcquisition.ForEpisodes(member, batch[0].Season, batch.Select(e => new AcquisitionEpisode(e.Season, e.Episode, null, e.AirDate, false)).ToArray(), items, folders);
        if (request is null) return (false, "There's no library folder to download into.", null);
        var options = batch[0].Options ?? Options(plugin, request, show);
        try
        {
            var result = await run(plugin, request with { Action = "acquire", Options = options, SelectedReleases = batch.SelectMany(e => e.Releases ?? []).Select(r => r.Id).Distinct().ToArray() }, ct);
            return (result.Success, result.Message, request.SeasonDirectory ?? request.TargetDirectory);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return (false, ex.Message, null); }
    }

    // The notification for the episodes this check found releases for.
    private FollowNotice? Notice(string token)
    {
        var found = Pending().Where(x => x.Episode.Token == token).ToArray();
        if (found.Length == 0) return null;
        var startAt = found.Max(x => x.Episode.StartAt);
        var lines = found.Take(3).Select(x => $"{x.Show.Title} {x.Episode.Code} · {x.Episode.Releases![0].Title}").ToList();
        if (found.Length > 3) lines.Add($"and {found.Length - 3} more");
        var heading = found.Length == 1 ? "A new episode is ready to download" : $"{found.Length} new episodes are ready to download";
        var footer = startAt > 0 ? "Downloads at " + DateTimeOffset.FromUnixTimeMilliseconds(startAt).ToLocalTime().ToString("t", CultureInfo.CurrentCulture) + " unless you skip it."
            : "Nothing downloads until you choose Download.";
        return new("ready", token, heading, lines.ToArray(), footer, startAt, found.Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToArray());
    }
    private static FollowNotice? AttentionNotice(List<(FollowedShow Show, FollowEpisode Episode)> episodes)
    {
        if (episodes.Count == 0) return null;
        var lines = episodes.Take(3).Select(x => $"{x.Show.Title} {x.Episode.Code} · " + (x.Episode.Status == FollowStatus.Stalled ? "hasn't arrived yet" : "no release found")).ToList();
        if (episodes.Count > 3) lines.Add($"and {episodes.Count - 3} more");
        return new("attention", NewToken(), episodes.Count == 1 ? "An episode needs your attention" : $"{episodes.Count} episodes need your attention", lines.ToArray(),
            "Open Downloads in BlackPearl to retry or choose another source.", 0, episodes.Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToArray());
    }
    public static FollowNotice? OutcomeNotice(FollowOutcome[] outcomes)
    {
        if (outcomes.Length == 0) return null;
        var failed = outcomes.Count(x => !x.Success);
        var heading = failed == 0 ? outcomes.Length == 1 ? "Download started" : $"{outcomes.Length} downloads started" : failed == outcomes.Length ? "Couldn't start the download" : $"{failed} of {outcomes.Length} downloads couldn't start";
        var lines = outcomes.Take(3).Select(x => x.Name + " · " + x.Message).ToList();
        if (outcomes.Length > 3) lines.Add($"and {outcomes.Length - 3} more");
        return new("outcome", NewToken(), heading, lines.ToArray(), failed == 0 ? "The episodes join your library when the download finishes." : "Open Downloads in BlackPearl to retry.", 0, []);
    }
}

// What auto-download asks Windows for: a notification (toast XML, which Windows 10 and 11 both accept) whose buttons
// open a link that starts BlackPearl, and an hourly task that runs the background check.
public static class FollowNotifications
{
    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";
    public static string Link(string scheme, string action, string? token = null) => $"{scheme}:{action}" + (token is null ? "" : "?token=" + token);
    // A link from a notification's button: what to do and the token of the episodes it is for. Null when it isn't ours.
    public static (string Action, string? Token)? ParseLink(string scheme, string? link)
    {
        if (link is null || !link.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(link[(scheme.Length + 1)..], @"^/*(approve|skip|open|desktop)/?(?:\?token=([0-9a-f]{32}))?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var token = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : null;
        var action = match.Groups[1].Value.ToLowerInvariant();
        return action != "open" && token is null ? null : (action, token);
    }
    public static string ToastXml(FollowNotice notice, string scheme)
    {
        var text = new StringBuilder();
        text.Append("<text>").Append(Escape(notice.Heading)).Append("</text>");
        text.Append("<text>").Append(Escape(string.Join("\n", notice.Lines))).Append("</text>");
        if (notice.Footer is { } footer) text.Append("<text placement=\"attribution\">").Append(Escape(footer)).Append("</text>");
        var open = Escape(Link(scheme, "open", notice.Kind == "ready" ? notice.Token : null));
        var actions = notice.Kind == "ready"
            ? $"<action content=\"{(notice.StartAt > 0 ? "Download now" : "Download")}\" activationType=\"protocol\" arguments=\"{Escape(Link(scheme, "approve", notice.Token))}\"/>" +
              $"<action content=\"Review\" activationType=\"protocol\" arguments=\"{open}\"/>" +
              $"<action content=\"Skip\" activationType=\"protocol\" arguments=\"{Escape(Link(scheme, "skip", notice.Token))}\"/>"
            : $"<action content=\"Open Downloads\" activationType=\"protocol\" arguments=\"{open}\"/>";
        // A notice waiting for the user stays on screen until they act on it, rather than slipping into Action Center.
        var scenario = notice.Kind == "ready" && notice.StartAt == 0 ? " scenario=\"reminder\"" : "";
        return $"<toast launch=\"{open}\" activationType=\"protocol\"{scenario}><visual><binding template=\"ToastGeneric\">{text}</binding></visual><actions>{actions}</actions></toast>";
    }
    // The hourly background check, and one shortly after signing in, for the current user only. Runs on battery, waits
    // for a network, and starts as soon as it can after a time the computer was off or asleep.
    public static string TaskXml(string user, string command, string arguments, DateTime start) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Checks the shows you follow in BlackPearl for new episodes and asks before downloading them.</Description></RegistrationInfo>
          <Triggers>
            <TimeTrigger><StartBoundary>{start:yyyy-MM-ddTHH:mm:ss}</StartBoundary><Enabled>true</Enabled><Repetition><Interval>PT1H</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger>
            <LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId><Delay>PT2M</Delay></LogonTrigger>
          </Triggers>
          <Principals><Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>{Escape(command)}</Command><Arguments>{Escape(arguments)}</Arguments></Exec></Actions>
        </Task>
        """;
}
