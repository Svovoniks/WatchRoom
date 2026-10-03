namespace Watchroom.Core;

// No local paths or original library IDs cross the peer connection.
public record SharedLibraryItem(string Id, string Title, string Kind, int? Year,
    string? Series, int? Season, int? Episode, string? Overview, bool Available, string? Thumbnail = null)
{
    public string Caption => Episode is { } ep ? $"{Series} · S{Season:00} E{ep:00}" : $"{Kind} · {Year?.ToString() ?? "Local video"}";
    public string DisplayTitle => Episode is { } ep ? $"{Title} · S{Season:00} E{ep:00}" : Title;
}
public record GuestAccess(bool Browse = false, bool Queue = false, bool Start = false);
public record RoomQueueEntry(string Id, string LibraryId, string Title, string AddedBy);
public record LibraryQueue(long Revision, RoomQueueEntry[] Entries);
public record LibraryCommand(string RequestId, string Action, long Revision, string? ItemId = null, string? EntryId = null, int Index = 0);
public record LibraryResult(string RequestId, bool Success, string Text);
public record LibraryActivity(long AtUnixMs, string Actor, string Text);
public record GuestLibraryView(long Sequence, bool Browsing, string Search, string Category,
    int Columns, int Rows, int Page, string[] VisibleIds, string? SelectedId, string? HoveredId, double GridWidth = 0, double GridHeight = 0);
public record LibraryPageRequest(long Generation, int Offset);
public record LibraryCatalogHeader(long Generation, int Count);
public record LibraryCatalogItem(long Generation, SharedLibraryItem Item);
public record LibraryPageEnd(long Generation, int NextOffset);
public record LibraryStart(string PeerId, LibraryCommand Command, SharedLibraryItem Item, long CatalogGeneration);

// Called on the UI dispatcher (or another single owner). The host owns all mutations.
public sealed class RoomLibrary(bool host, string selfId)
{
    public const int MaxCatalog = 10000;
    public const int PageSize = 20;
    public Dictionary<string, SharedLibraryItem> Catalog { get; } = new();
    public Dictionary<string, GuestAccess> Access { get; } = new();
    public Dictionary<string, GuestLibraryView> Views { get; } = new();
    public LibraryQueue Queue { get; private set; } = new(0, []);
    public GuestAccess OwnAccess { get; private set; } = new();
    public long Generation { get; private set; }
    private readonly Dictionary<string, string> names = new();
    private readonly Dictionary<string, LibraryResult> results = new();
    private readonly Dictionary<string, LibraryStart> pending = new();
    private readonly Dictionary<string, long> viewUpdated = new();
    private readonly Dictionary<string, long> transfers = new();
    private long transferSequence;
    private readonly Stack<RoomQueueEntry[]> undo = new();
    private readonly List<LibraryActivity> history = [];
    public IReadOnlyList<LibraryActivity> History => history;
    private int received;
    public event Action<string, WireMessage>? Send;
    public event Action? Changed;
    public event Action<LibraryActivity>? Activity;
    public event Action<LibraryResult>? Result;
    public event Action<LibraryStart>? StartRequested;
    private string Name(string id) => names.GetValueOrDefault(id, id == selfId ? "Host" : "Guest");
    private void Emit<T>(string peer, string type, T data) => Send?.Invoke(peer, new(type, Data: Wire.Serialize(data)));
    private void Log(string peer, string text)
    {
        var activity = new LibraryActivity(Wire.Now, Name(peer), text);
        Record(activity);
        foreach (var id in Access.Keys) Emit(id, "library-activity", activity);
    }
    private void Record(LibraryActivity activity)
    {
        history.Add(activity); if (history.Count > 200) history.RemoveAt(0); Activity?.Invoke(activity);
    }
    public void SetPeople(IEnumerable<Participant> people)
    {
        var admitted = people.Where(p => p.Approved).ToArray();
        names.Clear(); foreach (var person in admitted) names[person.Id] = person.Name;
        foreach (var id in Access.Keys.Where(id => !names.ContainsKey(id)).ToArray())
        { Access.Remove(id); Views.Remove(id); viewUpdated.Remove(id); transfers.Remove(id); }
        Changed?.Invoke();
    }
    public void Publish(IEnumerable<SharedLibraryItem> items)
    {
        if (!host) throw new InvalidOperationException();
        Catalog.Clear(); foreach (var item in items.Take(MaxCatalog)) Catalog.Add(item.Id, item);
        Generation++;
        // Removed collections cannot remain actionable through old queue entries.
        Queue = new(Queue.Revision + 1, Queue.Entries.Where(e => Catalog.ContainsKey(e.LibraryId)).ToArray());
        undo.Clear(); Views.Clear(); viewUpdated.Clear();
        foreach (var id in Access.Keys) Sync(id);
        Changed?.Invoke();
    }
    public void SetAccess(string peer, GuestAccess access)
    {
        if (!host || !names.ContainsKey(peer) || peer == selfId) throw new InvalidOperationException("Guest is not admitted");
        Access[peer] = access.Browse ? access : new();
        if (!access.Browse) { Views.Remove(peer); viewUpdated.Remove(peer); }
        Sync(peer); Log(selfId, $"changed {Name(peer)}'s library permissions"); Changed?.Invoke();
    }
    public void PeerReady(string peer)
    {
        if (host) { Access.TryAdd(peer, new()); Sync(peer); }
        else Emit(peer, "library-hello", 1);
    }
    private void Sync(string peer)
    {
        var access = Access.GetValueOrDefault(peer, new());
        var transfer = ++transferSequence; transfers[peer] = transfer;
        Emit(peer, "library-access", access);
        Emit(peer, "library-catalog", new LibraryCatalogHeader(transfer, access.Browse ? Catalog.Count : 0));
        Emit(peer, "library-queue", Queue);
    }
    private void BroadcastQueue()
    {
        foreach (var id in Access.Keys) Emit(id, "library-queue", Queue);
        Changed?.Invoke();
    }
    public void Receive(string peer, WireMessage message)
    {
        if (host)
        {
            if (!names.ContainsKey(peer) || peer == selfId) return;
            switch (message.Type)
            {
                case "library-hello": Access.TryAdd(peer, new()); Sync(peer); break;
                case "library-page":
                    var request = Wire.Read<LibraryPageRequest>(message.Data!);
                    if (Access.GetValueOrDefault(peer)?.Browse != true || request.Generation != transfers.GetValueOrDefault(peer) || request.Offset < 0 || request.Offset > Catalog.Count) return;
                    foreach (var item in Catalog.Values.Skip(request.Offset).Take(PageSize))
                        Emit(peer, "library-item", new LibraryCatalogItem(request.Generation, item));
                    Emit(peer, "library-page-end", new LibraryPageEnd(request.Generation, Math.Min(Catalog.Count, request.Offset + PageSize)));
                    break;
                case "library-command": Execute(peer, Wire.Read<LibraryCommand>(message.Data!)); break;
                case "library-view":
                    var view = Wire.Read<GuestLibraryView>(message.Data!);
                    if (Access.GetValueOrDefault(peer)?.Browse != true || view.VisibleIds is null || view.Search is null || view.Category is null || view.Columns is < 1 or > 8 || view.Rows is < 1 or > 4 ||
                        view.VisibleIds.Length > view.Columns * view.Rows || view.Page < 0 || view.Search.Length > 200 || view.Category.Length > 40 ||
                        view.VisibleIds.Any(id => !Catalog.ContainsKey(id)) ||
                        (view.HoveredId is not null && !view.VisibleIds.Contains(view.HoveredId)) ||
                        !double.IsFinite(view.GridWidth) || !double.IsFinite(view.GridHeight) || view.GridWidth is < 0 or > 16000 || view.GridHeight is < 0 or > 16000 ||
                        (view.SelectedId is not null && !Catalog.ContainsKey(view.SelectedId))) return;
                    if (Views.TryGetValue(peer, out var previous) && previous.Sequence >= view.Sequence) return;
                    viewUpdated[peer] = Wire.Now;
                    Views[peer] = view; Changed?.Invoke(); break;
            }
            return;
        }
        // RoomClient admits only the host-to-guest peer connection.
        switch (message.Type)
        {
            case "library-access": OwnAccess = Wire.Read<GuestAccess>(message.Data!); if (!OwnAccess.Browse) { Catalog.Clear(); Views.Clear(); } Changed?.Invoke(); break;
            case "library-catalog":
                var header = Wire.Read<LibraryCatalogHeader>(message.Data!);
                if (header.Count is < 0 or > MaxCatalog) return;
                Generation = header.Generation; received = header.Count; Catalog.Clear(); Changed?.Invoke();
                if (OwnAccess.Browse && received > 0) Emit(peer, "library-page", new LibraryPageRequest(Generation, 0));
                break;
            case "library-item":
                var entry = Wire.Read<LibraryCatalogItem>(message.Data!);
                if (OwnAccess.Browse && entry.Generation == Generation && Catalog.Count < received) Catalog[entry.Item.Id] = entry.Item;
                break;
            case "library-page-end":
                var end = Wire.Read<LibraryPageEnd>(message.Data!);
                if (end.Generation != Generation || !OwnAccess.Browse) return;
                Changed?.Invoke();
                if (end.NextOffset < received) Emit(peer, "library-page", new LibraryPageRequest(Generation, end.NextOffset));
                break;
            case "library-queue":
                var queue = Wire.Read<LibraryQueue>(message.Data!);
                if (queue.Revision >= Queue.Revision && queue.Entries.Length <= 50) { Queue = queue; Changed?.Invoke(); } break;
            case "library-result": Result?.Invoke(Wire.Read<LibraryResult>(message.Data!)); break;
            case "library-activity": Record(Wire.Read<LibraryActivity>(message.Data!)); break;
        }
    }
    public void Request(string hostId, string action, string? item = null, string? entry = null, int index = 0)
    {
        var command = new LibraryCommand(Guid.NewGuid().ToString("N"), action, Queue.Revision, item, entry, index);
        if (host) Execute(selfId, command); else Emit(hostId, "library-command", command);
    }
    public void SendView(string hostId, GuestLibraryView view)
    {
        if (!host && OwnAccess.Browse) Emit(hostId, "library-view", view);
    }
    public void ExpireViews(long now)
    {
        if (!host) return;
        var expired = viewUpdated.Where(pair => now - pair.Value > 8000).Select(pair => pair.Key).ToArray();
        foreach (var id in expired) { Views.Remove(id); viewUpdated.Remove(id); }
        if (expired.Length > 0) Changed?.Invoke();
    }
    private bool Allowed(string peer, string action) => peer == selfId ||
        Access.TryGetValue(peer, out var access) && access.Browse && (action is "play" or "next" ? access.Start : access.Queue);
    private string Key(string peer, LibraryCommand command) => peer + ":" + command.RequestId;
    private void Finish(string peer, LibraryCommand command, bool success, string text)
    {
        var result = new LibraryResult(command.RequestId, success, text);
        if (results.Count >= 256) results.Remove(results.Keys.First());
        results[Key(peer, command)] = result;
        if (peer == selfId) Result?.Invoke(result); else Emit(peer, "library-result", result);
    }
    private void Execute(string peer, LibraryCommand command)
    {
        if (string.IsNullOrEmpty(command.RequestId) || command.RequestId.Length > 64 || command.Action is null || command.Action.Length > 20) return;
        var key = Key(peer, command);
        if (results.TryGetValue(key, out var cached)) { if (peer != selfId) Emit(peer, "library-result", cached); return; }
        if (pending.ContainsKey(key)) return;
        if (!Allowed(peer, command.Action)) { Finish(peer, command, false, "The host has not granted this permission."); return; }
        if (command.Revision != Queue.Revision) { Finish(peer, command, false, "The queue changed. Try again."); if (peer != selfId) Emit(peer, "library-queue", Queue); return; }
        var entries = Queue.Entries.ToList(); var text = "";
        switch (command.Action)
        {
            case "play": case "next":
                var entry = command.Action == "next" ? entries.FirstOrDefault() : null;
                var id = entry?.LibraryId ?? command.ItemId ?? "";
                if (!Catalog.TryGetValue(id, out var item) || !item.Available) { Finish(peer, command, false, "Title is unavailable."); return; }
                if (pending.Count > 0) { Finish(peer, command, false, "Another video is starting. Try again shortly."); return; }
                var start = new LibraryStart(peer, command with { EntryId = entry?.Id }, item, Generation);
                pending[key] = start; Log(peer, "is starting " + item.DisplayTitle); StartRequested?.Invoke(start); return;
            case "add":
                if (entries.Count >= 50 || !Catalog.TryGetValue(command.ItemId ?? "", out var added) || !added.Available)
                { Finish(peer, command, false, "Title unavailable or queue full."); return; }
                entries.Add(new(Guid.NewGuid().ToString("N"), added.Id, added.DisplayTitle, Name(peer))); text = "added " + added.DisplayTitle; break;
            case "remove": case "move":
                var position = entries.FindIndex(e => e.Id == command.EntryId);
                if (position < 0) { Finish(peer, command, false, "Queue entry no longer exists."); return; }
                var moved = entries[position]; entries.RemoveAt(position);
                if (command.Action == "move") entries.Insert(Math.Clamp(command.Index, 0, entries.Count), moved);
                text = (command.Action == "move" ? "moved " : "removed ") + moved.Title; break;
            case "clear": entries.Clear(); text = "cleared the queue"; break;
            case "undo" when peer == selfId && undo.Count > 0:
                entries = undo.Pop().ToList(); text = "undid a queue edit"; break;
            default: Finish(peer, command, false, "Unknown or unavailable action."); return;
        }
        if (command.Action != "undo") { if (undo.Count >= 50) undo.Clear(); undo.Push(Queue.Entries); }
        Queue = new(Queue.Revision + 1, entries.ToArray()); BroadcastQueue(); Log(peer, text); Finish(peer, command, true, text);
    }
    public bool CanCommit(LibraryStart start) => pending.ContainsKey(Key(start.PeerId, start.Command)) &&
        Allowed(start.PeerId, start.Command.Action) && start.CatalogGeneration == Generation &&
        start.Command.Revision == Queue.Revision && Catalog.ContainsKey(start.Item.Id);
    public void CompleteStart(LibraryStart start, bool success, string? error = null)
    {
        success &= CanCommit(start);
        pending.Remove(Key(start.PeerId, start.Command));
        if (success && start.Command.EntryId is not null)
        {
            Queue = new(Queue.Revision + 1, Queue.Entries.Where(e => e.Id != start.Command.EntryId).ToArray());
            undo.Clear(); BroadcastQueue();
        }
        Log(start.PeerId, success ? "started " + start.Item.DisplayTitle : "could not start " + start.Item.DisplayTitle);
        Finish(start.PeerId, start.Command, success, success ? "Started " + start.Item.DisplayTitle : error ?? "Start cancelled because permissions, library or queue changed.");
    }
}
