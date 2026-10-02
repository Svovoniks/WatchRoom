namespace Watchroom.Core;

internal sealed record HostRoomState(long Sequence, long HostNowMs, RoomSnapshot Snapshot);

// Accessed by RoomClient's single control event loop. Discovery supplies identity
// and admission; the host owns all playback, readiness, queue, and chat state.
internal sealed class HostRoomCoordinator(string host)
{
    private readonly Dictionary<string, Participant> people = [];
    private readonly HashSet<string> departed = [];
    private readonly Dictionary<string, (long At, int Count)> limits = [];
    private long revision;
    private bool resumeWhenReady;
    public AdmittedGuest[] AdmittedGuests { get; set; } = [];
    public RoomSnapshot Snapshot => new(people.Values.ToArray(), Media, Playback, SharedControls, Queue, AdmittedGuests);
    public SharedMedia? Media { get; private set; }
    public PlaybackState? Playback { get; private set; }
    public bool SharedControls { get; private set; }
    public string[] Queue { get; private set; } = [];
    public bool Approved(string id) => people.TryGetValue(id, out var p) && p.Approved;
    public void Discover(Participant[] members, long now)
    {
        var ids = members.Select(p => p.Id).ToHashSet();
        foreach (var id in people.Keys.Where(id => !ids.Contains(id)).ToArray()) people.Remove(id);
        foreach (var p in members)
            if (!departed.Contains(p.Id)) people[p.Id] = p with { Ready = people.GetValueOrDefault(p.Id)?.Ready ?? false };
        TryResume(now);
    }
    public void Depart(string id, long now)
    {
        departed.Add(id); people.Remove(id); limits.Remove(id); TryResume(now);
    }
    public bool Apply(string sender, WireMessage message, long now, out WireMessage? announcement)
    {
        announcement = null;
        if (!Approved(sender)) return false;
        if (sender != host)
        {
            var limit = limits.GetValueOrDefault(sender);
            limit = now - limit.At >= 1000 ? (now, 1) : (limit.At, limit.Count + 1);
            limits[sender] = limit;
            if (limit.Count > 80) throw new InvalidDataException("Too many room commands");
        }
        switch (message.Type)
        {
            case "media" when sender == host:
                var media = Wire.Read<SharedMedia>(message.Data!);
                if (!ValidMedia(media)) throw new InvalidDataException("Invalid shared media");
                Media = media; resumeWhenReady = false;
                foreach (var p in people.Values.ToArray()) people[p.Id] = p with { Ready = false };
                Playback = new(++revision, media.Id, false, 0, now);
                return true;
            case "controls" when sender == host: SharedControls = message.Number == 1; return true;
            case "queue" when sender == host:
                var queue = Wire.Read<string[]>(message.Data!);
                if (queue.Length > 1000 || queue.Any(x => x is null || x.Length > 300) || message.Data!.Length > 32768)
                    throw new InvalidDataException("Queue is too large");
                Queue = queue; return true;
            case "playback" when sender == host || SharedControls:
                var desired = Wire.Read<PlaybackState>(message.Data!);
                if (Media is null || desired.MediaId != Media.Id || desired.PositionMs is < 0 or > 604800000 || desired.CommandId?.Length > 64) return false;
                resumeWhenReady = desired.Playing && people.Values.Any(p => p.Approved && !p.Ready);
                Playback = desired with { Revision = ++revision, Playing = desired.Playing && !resumeWhenReady, AtUnixMs = now + (desired.Playing && !resumeWhenReady ? 200 : 0) };
                return true;
            case "ready":
                people[sender] = people[sender] with { Ready = true }; TryResume(now); return true;
            case "buffering":
                people[sender] = people[sender] with { Ready = false };
                if (Playback is { Playing: true } current)
                {
                    resumeWhenReady = true;
                    Playback = current with { Revision = ++revision, Playing = false, PositionMs = SyncMath.TargetPosition(current, now), AtUnixMs = now };
                    announcement = new("notice", Text: $"{people[sender].Name} is buffering. Playback resumes when everyone is ready.");
                }
                return true;
            case "chat":
                var text = message.Text?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length > 1000) return false;
                announcement = new("chat", Sender: sender, Text: people[sender].Name + ": " + text); return true;
            default: return false;
        }
    }
    private void TryResume(long now)
    {
        if (!resumeWhenReady || Playback is null || people.Values.Any(p => p.Approved && !p.Ready)) return;
        resumeWhenReady = false;
        Playback = Playback with { Revision = ++revision, Playing = true, AtUnixMs = now + 200 };
    }
    private static bool ValidMedia(SharedMedia m, bool sidecar = false) =>
        m.Id is { Length: > 0 and <= 128 } && m.Title is { Length: <= 300 } && m.Length > 0 && m.Extension is { Length: <= 16 } &&
        (sidecar ? m.Length <= 8388608 && m.Subtitles is null or { Length: 0 } :
            m.Subtitles is null || m.Subtitles.Length <= 12 && m.Subtitles.All(s => s is not null && ValidMedia(s, true)));
}
