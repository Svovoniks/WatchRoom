namespace Watchroom.Core;

// What a room watched last, saved on each participant's computer. LibraryId lets the host reopen the file.
public record RoomSession(string Title, long PositionMs, long DurationMs, bool Playing, long UpdatedAt,
    string[]? People = null, string? Host = null, string? LibraryId = null);

public record RoomPresence(string Text, bool Everyone);

public static class RoomSessions
{
    public const int MaxPeople = 12;

    // Snapshots come from the host, so a connected participant's record always follows the host's state.
    public static RoomSession? Observe(RoomSession? stored, RoomSnapshot snapshot, string self, bool host, long serverNow, long now,
        long durationMs = 0, string? libraryId = null)
    {
        if (snapshot.Media is not { } media)
            return !host && snapshot.LastSession is { } shared && Valid(shared) ? shared with { LibraryId = null } : stored;
        var same = stored?.Title == media.Title ? stored : null;
        var playback = snapshot.Playback is { } state && state.MediaId == media.Id ? state : null;
        // While the host reopens a video the room briefly reports its start; keep the saved position until it seeks.
        var position = playback is null || playback.Stop?.Reason == "loading" ? same?.PositionMs ?? 0 : SyncMath.TargetPosition(playback, serverNow);
        var people = snapshot.People.Where(p => p.Approved && p.Id != self).Select(p => p.Name)
            .Concat(same?.People ?? []).Distinct(StringComparer.Ordinal).Take(MaxPeople).ToArray();
        return new(media.Title, position, durationMs > 0 ? durationMs : same?.DurationMs ?? 0, playback?.WantsPlayback == true, now, people,
            snapshot.People.FirstOrDefault(p => p.IsHost)?.Name ?? same?.Host, host ? libraryId ?? same?.LibraryId : null);
    }

    public static bool Valid(RoomSession session) => session.Title is { Length: > 0 and <= 300 } && session.PositionMs is >= 0 and <= 604800000 &&
        session.DurationMs is >= 0 and <= 604800000 && (session.People is null || session.People.Length <= MaxPeople && session.People.All(name => name is { Length: > 0 and <= 40 })) &&
        session.Host is null or { Length: <= 40 } && session.LibraryId is null or { Length: <= 128 };

    // Who the room is still waiting for: people from the last session who have not rejoined, and anyone still loading the video.
    public static RoomPresence Presence(RoomSnapshot snapshot, string self, string selfName, IEnumerable<string>? expected = null)
    {
        var others = snapshot.People.Where(p => p.Id != self).ToArray();
        var admitted = others.Where(p => p.Approved).ToArray();
        var loading = snapshot.Media is null ? [] : admitted.Where(p => !p.Ready).Select(p => p.Name);
        var absent = (expected ?? []).Where(name => name != selfName && !admitted.Any(p => p.Name == name));
        var waiting = loading.Concat(absent).Distinct(StringComparer.Ordinal).ToArray();
        var asking = others.Where(p => !p.Approved).Select(p => p.Name).Distinct(StringComparer.Ordinal).ToArray();
        var text = waiting.Length > 0 ? "Waiting for " + Names(waiting)
            : admitted.Length == 0 ? "Only you are here"
            : (admitted.Length == 1 ? admitted[0].Name + " is here" : "Everyone's here · " + Names(admitted.Select(p => p.Name).ToArray()));
        if (asking.Length > 0) text += " · " + Names(asking) + (asking.Length == 1 ? " is" : " are") + " asking to join";
        return new(text, waiting.Length == 0 && asking.Length == 0 && admitted.Length > 0);
    }

    public static string Names(string[] names) => names.Length switch
    {
        0 => "",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        3 => names[0] + ", " + names[1] + " and " + names[2],
        _ => names[0] + ", " + names[1] + " and " + (names.Length - 2) + " others",
    };
}
