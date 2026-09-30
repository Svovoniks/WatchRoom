using System.Text.Json;

namespace Watchroom.Core;

public record LibraryFolder(string Path, string Kind = "Mixed")
{
    public override string ToString() => $"{Path} · {Kind}";
}
public record MediaItem(string Id, string Path, string Title, string Kind, int? Year = null,
    string? Series = null, int? Season = null, int? Episode = null, string? Poster = null,
    string? Overview = null, bool Available = true, bool Matched = false)
{
    public string Caption => Episode is { } ep ? $"{Series} · S{Season:00} E{ep:00}" : $"{Kind} · {Year?.ToString() ?? "Local video"}";
    public string DisplayTitle => Episode is not null ? Series ?? Title : Title;
    public override string ToString() => Episode is not null ? Caption + " · " + Title : Title;
}
public record SharedMedia(string Id, string Title, long Length, string Extension, SharedMedia[]? Subtitles = null);
public record Participant(string Id, string Name, bool IsHost, bool Approved, bool Ready = false);
public record PlaybackState(long Revision, string MediaId, bool Playing, long PositionMs, long AtUnixMs);
public record WireMessage(string Type, string? Target = null, string? Sender = null,
    string? Text = null, string? Data = null, long Number = 0);
public record Welcome(string Room, string Peer, bool Host, string[] IceServers, bool ForceRelay);
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
    public static float Correction(long driftMs) => Math.Abs(driftMs) < 100 ? 1f : driftMs > 0 ? 1.03f : 0.97f;
}
