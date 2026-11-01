namespace BlackPearl.Core;

public static class RoomPlaybackStatus
{
    // Read the current readiness list so additional stalls and recoveries update
    // the explanation even when the paused playback revision stays the same.
    public static string? Describe(RoomSnapshot? snapshot, bool reconnecting = false)
    {
        if (reconnecting) return "Connection interrupted · Reconnecting to host…";
        if (snapshot?.Media is null || snapshot.Playback is not { Playing: false } state) return null;
        if (state.WantsPlayback || state.Stop?.Reason == "buffering")
        {
            var names = snapshot.People.Where(p => p.Approved && !p.Ready).Select(p => p.Name).ToArray();
            return names.Length > 0 ? "Waiting for " + string.Join(", ", names) + " to buffer · Playback resumes automatically"
                : "Waiting for playback to resume…";
        }
        return state.Stop?.Reason switch
        {
            "loading" => snapshot.People.Any(p => p.Approved && !p.Ready) ? "Loading video…" : "Ready to play",
            "stopped" => Actor("Stopped", state.Stop),
            "ended" => "Video ended",
            "paused" => Actor("Paused", state.Stop),
            _ => "Playback paused"
        };
    }

    private static string Actor(string action, PlaybackStop stop) => string.IsNullOrWhiteSpace(stop.Name)
        ? action : action + " by " + stop.Name;
}
