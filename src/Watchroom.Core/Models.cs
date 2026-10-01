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
    string? MetadataProvider = null, int? MetadataId = null, long MetadataFetchedAt = 0, string? MetadataKind = null, string? MetadataType = null)
{
    public string Caption => Episode is { } ep ? $"{Series} · S{Season:00} E{ep:00}" : $"{Kind} · {Year?.ToString() ?? "Local video"}";
    public string DisplayTitle => Episode is not null ? Series ?? Title : Title;
    public string QueueTitle => Episode is null ? Title : Caption;
    public string EpisodeDisplayTitle => Episode is { } ep ? $"Episode {ep}" + (string.IsNullOrWhiteSpace(EpisodeTitle) ? "" : " · " + EpisodeTitle) : Title;
    public string? DetailOverview => new[] { EpisodeOverview, SeasonOverview, Overview }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    public override string ToString() => Episode is not null ? Caption + " · " + Title : Title;
}
public record SavedRoom(string Id, string Name, string Server, string Code, string? HostKey = null)
{
    public string Caption => (HostKey is null ? "Guest" : "Host") + " · " + Server + " · " + Code;
}
public record SavedQueue(string Id, string Name, string[] MediaIds);
public record SharedMedia(string Id, string Title, long Length, string Extension, SharedMedia[]? Subtitles = null);
public record Participant(string Id, string Name, bool IsHost, bool Approved, bool Ready = false);
public record PlaybackState(long Revision, string MediaId, bool Playing, long PositionMs, long AtUnixMs);
public record WireMessage(string Type, string? Target = null, string? Sender = null,
    string? Text = null, string? Data = null, long Number = 0);
public record Welcome(string Room, string Peer, bool Host, string[] IceServers, bool ForceRelay, string? HostKey = null);
public record RoomSnapshot(Participant[] People, SharedMedia? Media, PlaybackState? Playback, bool SharedControls, string[]? Queue = null);

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
    public const long HardSeekMs = 600;
    public static float Correction(long driftMs) => Math.Abs(driftMs) < 80 ? 1f :
        1f + (float)Math.Clamp(driftMs / 4000d, -.05, .05);
}
