using System.Text.Json;

namespace Watchroom.Core;

public record LibraryFolder(string Path, string Kind = "Mixed")
{
    public override string ToString() => $"{Path} · {Kind}";
}
public record MediaItem(string Id, string Path, string Title, string Kind, int? Year = null,
    string? Series = null, int? Season = null, int? Episode = null, string? Poster = null,
    string? Overview = null, bool Available = true, bool Matched = false, string? SeriesPoster = null, string? SeasonPoster = null, string? PosterSource = null,
    string? SeasonTitle = null, string? SeasonOverview = null, string? EpisodeTitle = null, string? EpisodeOverview = null,
    string? EpisodePoster = null, string? AirDate = null, int? RuntimeMinutes = null, string? SeasonSource = null, string? EpisodeSource = null,
    string? MetadataProvider = null, int? MetadataId = null, long MetadataFetchedAt = 0, string? MetadataKind = null, string? MetadataType = null,
    string? ShowId = null, string? SeasonId = null, string? SeriesPath = null, int? EpisodeEnd = null, int? AbsoluteEpisode = null,
    string NumberingOrder = "aired", Dictionary<string, string>? ProviderIds = null, bool MetadataLocked = false,
    string[]? LockedFields = null, string[]? LocalMetadataFields = null, string? OriginalTitle = null, string[]? Genres = null,
    string[]? Cast = null, double? Rating = null, bool IsVirtual = false, int? AirsBeforeSeason = null,
    int? AirsBeforeEpisode = null, int? AirsAfterSeason = null, string? NumberingConflict = null, long ArtworkFetchedAt = 0,
    string? LocalPosterUrl = null, string? LocalSeasonPosterUrl = null, string? LocalEpisodePosterUrl = null, int? AbsoluteEpisodeEnd = null,
    int? SourcePart = null, int? SourcePartEpisode = null, int? SourcePartEpisodeEnd = null,
    string? NumberingSource = null, string? SourceLibraryKind = null, bool IsExtra = false, string? GeneratedEpisodePoster = null, string? EpisodePosterQualityPath = null)
{
    public string Caption => Series is not null ? $"{Series} · {(Season is null ? "Season unknown" : $"S{Season:00}")} {(Episode is null ? "Episode unknown" : $"E{Episode:00}" + (EpisodeEnd > Episode ? $"–E{EpisodeEnd:00}" : ""))}" : $"{Kind} · {Year?.ToString() ?? "Local video"}";
    public string DisplayTitle => Series ?? Title;
    public string QueueTitle => Episode is null ? Title : Caption;
    public string EpisodeDisplayTitle => Episode is { } ep ? $"Episode {ep}" + (EpisodeEnd > ep ? $"–{EpisodeEnd}" : "") + (string.IsNullOrWhiteSpace(EpisodeTitle) ? "" : " · " + EpisodeTitle) : Title;
    public string? DetailOverview => new[] { EpisodeOverview, SeasonOverview, Overview }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    public override string ToString() => Episode is not null ? Caption + " · " + Title : Title;
}
public record SavedRoom(string Id, string Name, string Server, string Code, string? HostKey = null)
{
    public string Caption => (HostKey is null ? "Guest" : "Host") + " · " + (Uri.TryCreate(Server, UriKind.Absolute, out var address) ? address.Host : Server);
    public override string ToString() => Name + " · " + Caption;
}
public record SavedQueue(string Id, string Name, string[] MediaIds);
public record SharedMedia(string Id, string Title, long Length, string Extension, SharedMedia[]? Subtitles = null);
public record Participant(string Id, string Name, bool IsHost, bool Approved, bool Ready = false, string? GuestId = null);
public record PlaybackState(long Revision, string MediaId, bool Playing, long PositionMs, long AtUnixMs, string? CommandId = null);
public record WireMessage(string Type, string? Target = null, string? Sender = null,
    string? Text = null, string? Data = null, long Number = 0);
public record Welcome(string Room, string Peer, bool Host, string[] IceServers, bool ForceRelay, string? HostKey = null, string? GuestKey = null);
public record AdmittedGuest(string Id, string Name);
public record RoomSnapshot(Participant[] People, SharedMedia? Media, PlaybackState? Playback, bool SharedControls, string[]? Queue = null, AdmittedGuest[]? AdmittedGuests = null);

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Empty message");
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public static class SyncMath
{
    public static long TargetPosition(PlaybackState state, long now) => Math.Max(0,
        state.PositionMs + (state.Playing ? Math.Max(0, now - state.AtUnixMs) : 0));
    // Aim for a <= 500 ms p95 pair difference in the measured LAN/VM fixture.
    // The hard bound is per client; live pair/frame measurements remain required.
    // Native time can be sparse or ahead of decoded frames (notably silent AVI).
    // Keep the established policy by default until the tighter policy is validated
    // across those inputs as well as ordinary H.264/AAC media.
    public static bool UseTunedPolicy { get; } = Environment.GetEnvironmentVariable("WATCHROOM_SYNC_EXPERIMENTAL") == "1";
    public static long HardSeekMs => UseTunedPolicy ? 600 : 1200;
    public static float Correction(long driftMs) => UseTunedPolicy ? TunedCorrection(driftMs) : EstablishedCorrection(driftMs);
    public static float EstablishedCorrection(long driftMs) => Math.Abs(driftMs) < 100 ? 1f : driftMs > 0 ? 1.03f : .97f;
    public static float TunedCorrection(long driftMs) => Math.Abs(driftMs) < 80 ? 1f :
        1f + (float)Math.Clamp(driftMs / 4000d, -.05, .05);
}
