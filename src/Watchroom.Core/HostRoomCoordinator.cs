namespace Watchroom.Core;

internal sealed record HostRoomState(long Sequence, long HostNowMs, RoomSnapshot Snapshot);

// Accessed by RoomClient's single control event loop. Discovery supplies identity
// and admission; the host owns all playback, readiness, queue, and chat state.
internal sealed class HostRoomCoordinator(string host)
{
    private readonly Dictionary<string, Participant> people = [];
    private readonly HashSet<string> departed = [];
    private readonly HashSet<string> scopedReadiness = [];
    private readonly Dictionary<string, (long At, int Count)> limits = [];
    private long revision, generation;
    private bool resumeWhenReady;
    public AdmittedGuest[] AdmittedGuests { get; set; } = [];
    public RoomSnapshot Snapshot => new(people.Values.ToArray(), Media, Playback, SharedControls, Queue, AdmittedGuests, Name, LibraryBrowsing, LibraryActivity);
    public bool LibraryBrowsing { get; private set; }
    public LibraryBrowseActivity? LibraryActivity { get; private set; }
    public bool ExpireLibrary(long now)
    {
        if (LibraryActivity is null || now - LibraryActivity.UpdatedAt < 45000) return false;
        LibraryActivity = null; return true;
    }
    public void DescribeLibrary(string location, string query, long now)
    {
        if (LibraryActivity is { } activity) LibraryActivity = activity with { Location = location, Query = query, UpdatedAt = now };
    }
    public void DescribeLibraryView(LibraryBrowseView view, long now)
    {
        if (view.LoadedCount is < 0 or > 100000 || !double.IsFinite(view.ScrollFraction) || view.ScrollFraction is < 0 or > 1 ||
            view.HoveredId?.Length > 128 || view.SelectedId?.Length > 128) throw new InvalidDataException("Invalid browsing view");
        if (LibraryActivity is { } activity) LibraryActivity = activity with { View = view, UpdatedAt = now };
    }
    public string? Name { get; private set; }
    internal static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 100 && !name.Any(char.IsControl);
    public SharedMedia? Media { get; private set; }
    public PlaybackState? Playback { get; private set; }
    public bool SharedControls { get; private set; }
    public string[] Queue { get; private set; } = [];
    public bool Approved(string id) => people.TryGetValue(id, out var p) && p.Approved;
    public void Discover(Participant[] members, long now)
    {
        var ids = members.Select(p => p.Id).ToHashSet();
        foreach (var id in people.Keys.Where(id => !ids.Contains(id)).ToArray()) { people.Remove(id); scopedReadiness.Remove(id); }
        foreach (var p in members)
            if (!departed.Contains(p.Id)) people[p.Id] = p with { Ready = people.GetValueOrDefault(p.Id)?.Ready ?? false };
        if (LibraryActivity is { } activity && !Approved(activity.Peer)) LibraryActivity = null;
        TryResume(now);
    }
    public void Depart(string id, long now)
    {
        departed.Add(id); people.Remove(id); limits.Remove(id); scopedReadiness.Remove(id);
        if (LibraryActivity?.Peer == id) LibraryActivity = null;
        TryResume(now);
    }
    public bool Apply(string sender, WireMessage message, long now, out WireMessage? announcement)
    {
        announcement = null;
        ExpireLibrary(now);
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
            case "catalog-permission" when sender == host:
                LibraryBrowsing = message.Number == 1;
                if (!LibraryBrowsing) LibraryActivity = null;
                return true;
            case "catalog-release" when sender == host || LibraryActivity is null || LibraryActivity.Peer == sender:
                LibraryActivity = null; return true;
            case "catalog-renew" when LibraryActivity?.Peer == sender:
                LibraryActivity = LibraryActivity with { UpdatedAt = now }; return true;
            case "catalog-follow" when LibraryActivity?.Peer == sender && LibraryBrowsing:
            case "catalog-artwork" when LibraryActivity?.Peer == sender && LibraryBrowsing:
            case "catalog-watch" when LibraryActivity?.Peer == sender && LibraryBrowsing:
            case "catalog-view" when LibraryActivity?.Peer == sender && LibraryBrowsing:
                return true;
            case "catalog-browse" when sender != host && LibraryBrowsing:
                if (LibraryActivity is not null && LibraryActivity.Peer != sender) return false;
                LibraryActivity = LibraryActivity is { } owned ? owned with { UpdatedAt = now } : new(sender, people[sender].Name, "All titles", "", now); return true;
            case "room-name" when sender == host:
                if (!ValidName(message.Text)) return false;
                Name = message.Text!.Trim(); return true;
            case "media" when sender == host:
                var media = Wire.Read<SharedMedia>(message.Data!);
                if (!ValidMedia(media)) throw new InvalidDataException("Invalid shared media");
                Media = media; resumeWhenReady = false; generation++;
                foreach (var p in people.Values.ToArray()) people[p.Id] = p with { Ready = false };
                Playback = new(++revision, media.Id, false, 0, now, RequestedPlaying: false, Generation: generation);
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
                if (desired.Seek)
                {
                    generation++;
                    // Legacy peers cannot acknowledge a seek while paused. They
                    // retain their existing buffering-based readiness behavior.
                    foreach (var p in people.Values.Where(p => scopedReadiness.Contains(p.Id)).ToArray()) people[p.Id] = p with { Ready = false };
                }
                resumeWhenReady = desired.Playing && people.Values.Any(p => p.Approved && !p.Ready);
                Playback = desired with { RequestedPlaying = desired.Playing, Generation = generation, Revision = ++revision, Playing = desired.Playing && !resumeWhenReady, AtUnixMs = now + (desired.Playing && !resumeWhenReady ? 200 : 0) };
                return true;
            case "ready":
                if (!CurrentReadiness(sender, message)) return true;
                people[sender] = people[sender] with { Ready = true }; TryResume(now); return true;
            case "buffering":
                if (!CurrentReadiness(sender, message)) return true;
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
    private bool CurrentReadiness(string sender, WireMessage message)
    {
        // Older peers omit scope. Scoped peers cannot revive an obsolete seek.
        if (message.Data is null) return !scopedReadiness.Contains(sender);
        var scope = Wire.Read<PlaybackReadiness>(message.Data);
        var current = scope.MediaId == Media?.Id && scope.Generation == generation;
        if (current) scopedReadiness.Add(sender);
        if (!current) PlaybackDiagnostics.Record("readiness-stale", new { message.Type, scope.Generation, currentGeneration = generation });
        return current;
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
