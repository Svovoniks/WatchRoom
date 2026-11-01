using System.IO;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// In a room, guests' Queues page shows the host's saved queues. Guests add, remove, reorder and create; the host
// applies each change to its own queues and shares the result. Only the host plays, renames or deletes queues.
public partial class MainWindow
{
    private readonly DispatcherTimer queueShareTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private long queueShareRevision;
    // Guests the host has sent its queues to, so newly admitted guests are caught up once.
    private readonly HashSet<string> queueSharePeers = [];
    // Guest side: the host's queues, and the host's videos they list.
    private SharedQueues? hostQueues;
    private List<SavedQueue> hostQueueList = [];
    private Dictionary<string, MediaItem> hostQueueMedia = [];
    private QueueStateAssembler queueAssembler = new();
    // Checks connect two windows directly instead of over a peer connection.
    internal Action<string, WireMessage>? queueOutbox;

    private bool ShowingHostQueues => hostQueues is not null && room is { IsConnected: true, Identity.Host: false };
    // The queues the Queues page shows and edits: the host's for a guest in a room, otherwise this app's own.
    private List<SavedQueue> QueueSource => ShowingHostQueues ? hostQueueList : savedQueues;
    private Dictionary<string, MediaItem> QueueMedia() => ShowingHostQueues ? hostQueueMedia : MediaById();
    private string HostName => room?.Snapshot?.People.FirstOrDefault(person => person.IsHost)?.Name ?? "The host";

    private void InitializeQueueSharing() => queueShareTimer.Tick += (_, _) => { queueShareTimer.Stop(); ShareQueues(); };
    // Edits come in bursts (a reorder saves and republishes the queue); share once they settle.
    private void ScheduleQueueShare()
    {
        if (room is not { IsConnected: true, Identity.Host: true } || queueSharePeers.Count == 0) return;
        queueShareTimer.Stop(); queueShareTimer.Start();
    }
    private void SendToPeer(string peer, WireMessage message)
    {
        if (queueOutbox is { } outbox) outbox(peer, message); else room!.SendLibrary(peer, message);
    }
    private void ShareQueues(string? only = null)
    {
        if (room is not { IsConnected: true, Identity.Host: true } client) return;
        var guests = (client.Snapshot?.People ?? []).Where(person => person.Approved && !person.IsHost && (only is null || person.Id == only)).Select(person => person.Id).ToArray();
        if (guests.Length == 0) return;
        WireMessage[] messages;
        try { messages = QueueSharing.Messages(queueShareRevision = Math.Max(queueShareRevision + 1, Wire.Now), SharedQueueState()); }
        catch (InvalidDataException ex) { SetStatus(ex.Message); return; }
        foreach (var guest in guests)
        {
            queueSharePeers.Add(guest);
            // A guest whose connection is not ready yet receives the queues once its library channel opens.
            try { foreach (var message in messages) SendToPeer(guest, message); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        }
    }
    private SharedQueues SharedQueueState()
    {
        var byId = MediaById();
        var media = savedQueues.SelectMany(saved => saved.MediaIds).Distinct().Select(id => byId.GetValueOrDefault(id)).OfType<MediaItem>()
            .Select(item => new SharedQueueMedia(item.Id, item.Title, item.Kind, item.Year, item.Series, item.Season, item.Episode, item.EpisodeEnd,
                item.EpisodeTitle, item.RuntimeMinutes, item.Available, item.IsVirtual, item.IsBonus)).ToArray();
        var active = savedQueues.FirstOrDefault(saved => saved.Id == activeQueueId);
        var playing = active is null ? -1 : SavedEntryIndex(active, byId, queuePosition);
        return new(savedQueues.Select(saved => new SharedQueue(saved.Id, saved.Name, saved.MediaIds)).ToArray(), media, active?.Id, playing);
    }
    private void ShareQueuesWithNewGuests(RoomSnapshot snapshot)
    {
        if (room?.Identity?.Host != true) return;
        var admitted = snapshot.People.Where(person => person.Approved && !person.IsHost).Select(person => person.Id).ToHashSet();
        queueSharePeers.IntersectWith(admitted);
        foreach (var guest in admitted.Except(queueSharePeers).ToArray()) ShareQueues(guest);
    }

    private void ReceiveQueueMessage(RoomClient client, string sender, WireMessage message)
    {
        if (client.Identity?.Host == true) { ReceiveGuestQueueEdit(client, sender, message); return; }
        if (message.Type == QueueSharing.ErrorMessage) { ShowQueueFeedback(message.Text ?? "The host could not change the queue."); return; }
        if (message.Type != QueueSharing.StateMessage) return;
        SharedQueues? state;
        try { state = queueAssembler.Add(message); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException) { return; }
        if (state is null) return;
        hostQueues = state;
        hostQueueList = state.Queues.Select(shared => new SavedQueue(shared.Id, shared.Name, shared.MediaIds)).ToList();
        hostQueueMedia = [];
        foreach (var item in state.Media)
            hostQueueMedia.TryAdd(item.Id, new MediaItem(item.Id, "", item.Title, item.Kind, item.Year, item.Series, item.Season, item.Episode,
                Available: item.Available, RuntimeMinutes: item.RuntimeMinutes, EpisodeEnd: item.EpisodeEnd, EpisodeTitle: item.EpisodeTitle,
                IsVirtual: item.IsVirtual, IsBonus: item.IsBonus));
        if (queueConfirmation is { } confirmed) { SetStatus(confirmed); queueConfirmation = null; }
        RefreshSavedItems();
    }
    private void ReceiveGuestQueueEdit(RoomClient client, string sender, WireMessage message)
    {
        if (message.Type != QueueSharing.EditMessage) return;
        var guest = client.Snapshot?.People.FirstOrDefault(person => person.Id == sender && person.Approved && !person.IsHost);
        if (guest is null) return;
        string? problem;
        try
        {
            var edit = Wire.Read<QueueEdit>(message.Data ?? "");
            problem = QueueSharing.Problem(edit) ?? ApplyGuestQueueEdit(edit, guest.Name);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or NotSupportedException) { problem = "That queue change was not understood."; }
        if (problem is null) return;
        // The guest's view may be out of date: send the reason and the current queues.
        try { SendToPeer(sender, new(QueueSharing.ErrorMessage, Text: problem)); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        ShareQueues(sender);
    }
    private string? ApplyGuestQueueEdit(QueueEdit edit, string guest)
    {
        var byId = MediaById();
        MediaItem[] Videos(IEnumerable<string>? ids) => (ids ?? []).Select(id => byId.GetValueOrDefault(id)).OfType<MediaItem>().ToArray();
        switch (edit.Action)
        {
            case "order":
                if (savedQueues.FirstOrDefault(saved => saved.Id == edit.QueueId) is not { } ordered) return "That queue no longer exists.";
                if (QueueSharing.Fingerprint(ordered.MediaIds) != edit.Fingerprint) return "The queue changed before your change arrived. Try again.";
                var order = edit.Order!;
                if (order.Distinct().Count() != order.Length || order.Any(index => index < 0 || index >= ordered.MediaIds.Length)) return "That queue change was not understood.";
                ApplyQueueOrder(ordered, order);
                SetStatus($"{guest} {(order.Length < ordered.MediaIds.Length ? "removed a video from" : "reordered")} {ordered.Name}.");
                return null;
            case "add":
                if (savedQueues.FirstOrDefault(saved => saved.Id == edit.QueueId) is not { } target) return "That queue no longer exists.";
                var added = AddToSavedQueue(target, Videos(edit.MediaIds));
                if (added == 0) return "Those videos are not available in the host's library right now.";
                SetStatus($"{guest} added {VideoCount(added)} to {target.Name}.");
                return null;
            case "create":
                var created = new SavedQueue(Guid.NewGuid().ToString("N"), edit.Name!.Trim(), []);
                savedQueues.Add(created);
                var initial = Videos(edit.MediaIds);
                if (initial.Length == 0 || AddToSavedQueue(created, initial) == 0) { SaveQueues(); RefreshQueuePickers(); }
                SetStatus($"{guest} created {created.Name}.");
                return null;
        }
        return "That queue change was not understood.";
    }

    // Guest side: changes go to the host, which shares the updated queues back.
    private bool SendQueueEdit(QueueEdit edit)
    {
        if (room is not { IsConnected: true }) return false;
        try { SendToPeer(HostPeerId(), new(QueueSharing.EditMessage, Data: Wire.Serialize(edit))); return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { ShowQueueFeedback(ex.Message); return false; }
    }
    private void SendQueueOrder(SavedQueue saved, int[] order)
    {
        var index = hostQueueList.FindIndex(queue => queue.Id == saved.Id);
        if (index < 0 || !SendQueueEdit(new("order", saved.Id, QueueSharing.Fingerprint(saved.MediaIds), order))) return;
        // Shown at once; the host's copy replaces it when it arrives.
        hostQueueList[index] = saved with { MediaIds = order.Select(entry => saved.MediaIds[entry]).ToArray() };
        RefreshSavedItems();
    }
    private void SendQueueAdd(SavedQueue saved, MediaItem[] videos)
    {
        if (videos.Length == 0) return;
        if (SendQueueEdit(new("add", saved.Id, MediaIds: videos.Select(video => video.Id).ToArray())))
        { SetStatus($"Adding {VideoCount(videos.Length)} to {saved.Name}…"); queueConfirmation = $"Added to {saved.Name}."; }
    }
    private void SendQueueCreate(string name, MediaItem[] videos)
    {
        if (SendQueueEdit(new("create", Name: name, MediaIds: videos.Select(video => video.Id).ToArray())))
        { SetStatus($"Creating {name}…"); queueConfirmation = $"Created {name} in {HostName}'s queues."; }
    }
    // The host answers a refused change with the reason and then its current queues; the reason stays up a few
    // seconds so refreshing the queue does not replace it at once.
    private (string Text, long Until)? queueNotice;
    // Replaces "Adding…" or "Creating…" once the host's updated queues arrive.
    private string? queueConfirmation;
    private string? QueueNotice => queueNotice is { } notice && Environment.TickCount64 < notice.Until ? notice.Text : null;
    private void ShowQueueFeedback(string text)
    {
        queueConfirmation = null;
        SetStatus(text);
        queueNotice = (text, Environment.TickCount64 + 6000);
        if (SavedQueueFeedback is not null) SavedQueueFeedback.Text = text;
    }
    private void ClearHostQueues()
    {
        queueShareTimer.Stop(); queueSharePeers.Clear();
        hostQueues = null; hostQueueList = []; hostQueueMedia = []; queueAssembler = new();
        if (openQueueId is not null && savedQueues.All(saved => saved.Id != openQueueId)) openQueueId = null;
        RefreshSavedItems();
    }
}
