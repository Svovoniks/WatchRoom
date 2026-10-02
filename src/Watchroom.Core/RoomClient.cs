using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Watchroom.Core;

public sealed class RoomClient : IAsyncDisposable
{
    private readonly LibraryStore? settings;
    public RoomClient(LibraryStore? settings = null) => this.settings = settings;
    private string RoomSetting(string code, string field) => "room:" + ServerAddress + ":" + code.ToUpperInvariant() + ":" + field;
    private IRoomConnection? connection;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource discoveryLifetime = new();
    private readonly Channel<WireMessage> outgoing = Channel.CreateBounded<WireMessage>(128);
    private sealed record RoomEvent(string Kind, string? Peer = null, WireMessage? Message = null, PeerTransport? Transport = null);
    private readonly Channel<RoomEvent> events = Channel.CreateBounded<RoomEvent>(256);
    private readonly ConcurrentDictionary<string, PeerTransport> peers = new();
    private readonly ConcurrentDictionary<string, byte> negotiated = new();
    private readonly TaskCompletionSource directConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HostRoomCoordinator? coordinator;
    private string? hostPeer;
    private string displayName = "";
    private string? initialRoomName;
    private long stateSequence, receivedSequence;
    private bool admitted;
    private bool roomControlsRestored;
    private int discoveryFailed;
    private Task? readTask, sendTask, controlTask, pingTask;
    private int disposed;
    private readonly TaskCompletionSource<Welcome> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsConnected => Identity is not null && !lifetime.IsCancellationRequested;
    public string? ServerAddress { get; private set; }
    public Welcome? Identity { get; private set; }
    public RoomSnapshot? Snapshot { get; private set; }
    public Task DirectControlsReady => directConnected.Task;
    public bool DiscoveryOnline => Volatile.Read(ref discoveryFailed) == 0 && connection is not null;
    public IMediaSource? HostedMedia { get; set; }
    private readonly ConcurrentDictionary<string, IMediaSource> assets = new();
    public void SetHostedFile(string path, string title)
    {
        assets.Clear();
        var file = new FileMediaSource(path, title);
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!))
        {
            var name = Path.GetFileNameWithoutExtension(sidecar);
            if (!(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))) continue;
            if (!new[] { ".srt", ".ass", ".ssa" }.Contains(Path.GetExtension(sidecar).ToLowerInvariant()) || new FileInfo(sidecar).Length is <= 0 or > 8388608 || assets.Count >= 12) continue;
            var asset = new FileMediaSource(sidecar, Path.GetFileName(sidecar)); assets[asset.Media.Id] = asset;
        }
        HostedMedia = new MediaWithSubtitles(file, assets.Values.Select(x => x.Media).ToArray());
    }
    private readonly ServerClock clock = new();
    private long pingId;
    public long ServerNowMs => clock.Now;
    public long ServerOffsetMs => clock.OffsetMs;
    public event Action<WireMessage>? Message;
    public event Action<string>? Status;
    public async Task ConnectAsync(string server, string name, string? invitation = null, string? hostKey = null, bool persistent = false, string? roomName = null)
    {
        server = RoomAddress.ValidateServer(server);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a display name between 1 and 40 characters in Settings.");
        ServerAddress = server;
        displayName = name.Trim();
        if (roomName is not null && !HostRoomCoordinator.ValidName(roomName))
            throw new ArgumentException("Enter a room name between 1 and 100 characters without control characters.");
        initialRoomName = roomName?.Trim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var uri = new Uri(server.TrimEnd('/') + "/room");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidOperationException("Use an HTTPS server address. HTTP is allowed only on localhost for development.");
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        var guestKey = invitation is not null && hostKey is null ? settings?.Setting(RoomSetting(invitation, "guestKey")) : null;
        var first = new WireMessage(hostKey is not null ? "resume" : invitation is null ? "create" : "join", Target: hostKey ?? (persistent ? "persistent" : null), Sender: guestKey, Text: name, Data: invitation);
        using var probe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using var health = await probe.GetAsync(server.TrimEnd('/') + "/health", deadline.Token);
        if ((int)health.StatusCode is >= 300 and < 400 || health.Content.Headers.ContentType?.MediaType == "text/html")
            throw new IOException("This Site requires browser sign-in. Ask its owner to enable access for Windows clients, or use the local server.");
        var transport = health.IsSuccessStatusCode && health.Content.Headers.ContentType?.MediaType == "application/json"
            ? await health.Content.ReadAsStringAsync(lifetime.Token) : "";
        if (persistent || hostKey is not null)
        {
            using var capabilities = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(transport) ? "{}" : transport);
            if (!capabilities.RootElement.TryGetProperty("persistentRooms", out var support) || support.ValueKind != System.Text.Json.JsonValueKind.True)
                throw new IOException("This server needs the persistent-room update. Update the room service or choose an updated server in Settings.");
        }
        if (transport.Contains("http-poll", StringComparison.Ordinal))
        {
            using var capabilities = System.Text.Json.JsonDocument.Parse(transport);
            connection = await SitesRoomConnection.OpenAsync(new Uri(server.TrimEnd('/') + "/"), first, deadline.Token,
                capabilities.RootElement.TryGetProperty("idempotentRequests", out var dedup) && dedup.ValueKind == System.Text.Json.JsonValueKind.True);
        }
        else
        {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try { await socket.ConnectAsync(builder.Uri, deadline.Token); }
        catch { socket.Dispose(); throw; }
        connection = new WebSocketRoomConnection(socket);
        await connection.SendAsync(first, lifetime.Token);
        }
        controlTask = ControlLoop(); sendTask = SendLoop(); readTask = ReadLoop();
        pingTask = PingLoop();
        try { await connected.Task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException ex)
        {
            await DisposeAsync();
            // A rejection also cancels the room lifetime. Preserve its specific
            // error rather than reporting the racing deadline cancellation.
            if (connected.Task.IsFaulted) await connected.Task;
            throw new IOException("Room connection timed out. Rejoin to reconnect.", ex);
        }
        catch { await DisposeAsync(); throw; }
    }
    public void Send(WireMessage message)
    {
        if (!IsConnected) throw new IOException("Room connection is closed. Rejoin the room to reconnect.");
        if (message.Type == "remove" && !DiscoveryOnline) throw new IOException("Reconnect to the room service before revoking saved guest access.");
        if (message.Type is "media" or "controls" or "room-name" or "queue" or "playback" or "ready" or "buffering" or "chat" or "remove")
        {
            if (Identity?.Host != true && !directConnected.Task.IsCompletedSuccessfully)
                throw new IOException("Waiting for the host's direct control connection. All participants need the updated app.");
            if (!events.Writer.TryWrite(new("local", Message: message))) throw new IOException("Room control queue is full");
        }
        else QueueDiscovery(message);
        if (PlaybackDiagnostics.Enabled && message.Type == "playback") PlaybackDiagnostics.Record("control-queued", new { message.Data, transport = "peer" });
    }
    private void QueueDiscovery(WireMessage message)
    {
        if (!DiscoveryOnline || !outgoing.Writer.TryWrite(message)) throw new IOException("Room discovery is offline. New joins are unavailable.");
    }
    private async Task SendLoop()
    {
        try
        {
            await foreach (var msg in outgoing.Reader.ReadAllAsync(discoveryLifetime.Token))
            {
                await connection!.SendAsync(msg, discoveryLifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or HttpRequestException)
        { if (!lifetime.IsCancellationRequested) DiscoveryLost(ex.Message); }
    }
    private async Task PingLoop()
    {
        try
        {
            var tick = 0;
            while (!lifetime.IsCancellationRequested)
            {
                if (tick++ % 5 == 0 && DiscoveryOnline)
                    try { QueueDiscovery(new("ping", Number: tick)); } catch (IOException ex) { DiscoveryLost(ex.Message); }
                if (Identity?.Host == false && directConnected.Task.IsCompletedSuccessfully)
                    Post(new("clock", Message: new("clock-ping", Number: Interlocked.Increment(ref pingId))));
                await Task.Delay(2000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { FailRoom("Room controls failed: " + ex.Message); }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (!discoveryLifetime.IsCancellationRequested)
            {
                var message = await connection!.ReadAsync(discoveryLifetime.Token); if (message is null) break;
                await events.Writer.WriteAsync(new("discovery", Message: message), lifetime.Token);
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested) { DiscoveryLost(ex.Message); }
        finally { if (!lifetime.IsCancellationRequested) DiscoveryLost("Room discovery connection closed"); }
    }
    private void Post(RoomEvent item)
    {
        if (!lifetime.IsCancellationRequested && !events.Writer.TryWrite(item)) FailRoom("Room control queue overflow. Rejoin the room.");
    }
    private void DiscoveryLost(string reason)
    {
        if (Interlocked.Exchange(ref discoveryFailed, 1) != 0) return;
        discoveryLifetime.Cancel(); connection?.Abort();
        Post(new("discovery-lost", Message: new("notice", Text: reason)));
    }
    private void FailRoom(string reason)
    {
        connected.TrySetException(new IOException(reason)); directConnected.TrySetException(new IOException(reason));
        lifetime.Cancel(); discoveryLifetime.Cancel(); connection?.Abort();
        Status?.Invoke("Disconnected — " + reason);
    }
    private async Task ControlLoop()
    {
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(lifetime.Token))
            {
                if (item.Transport is not null && (!peers.TryGetValue(item.Peer!, out var current) || current != item.Transport)) continue;
                try
                {
                    switch (item.Kind)
                    {
                        case "discovery": HandleDiscovery(item.Message!); break;
                        case "discovery-lost":
                            if (Identity is null || Identity.Host == false && !directConnected.Task.IsCompletedSuccessfully)
                                FailRoom(item.Message?.Text ?? "Room discovery disconnected");
                            else Status?.Invoke("Room discovery is offline. Existing peer playback continues; new joins are unavailable.");
                            break;
                        case "local": HandleLocal(item.Message!); break;
                        case "opened": SendPeer(item.Peer!, new("control-hello", Number: 2)); break;
                        case "peer": HandlePeer(item.Peer!, item.Message!); break;
                        case "closed": DepartPeer(item.Peer!); break;
                        case "timeout":
                            if (!negotiated.ContainsKey(item.Peer!)) DepartPeer(item.Peer!, "Direct room controls did not connect. Update all participants and rejoin.");
                            break;
                        case "clock":
                            if (hostPeer is not null && negotiated.ContainsKey(hostPeer))
                            { clock.Sent(item.Message!.Number); SendPeer(hostPeer, item.Message); }
                            break;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or IOException or ArgumentException)
                {
                    if (item.Kind == "peer" && Identity?.Host == true)
                        SendPeer(item.Peer!, new("error", Text: "Invalid or unauthorized room command"));
                    else if (item.Kind == "local") Message?.Invoke(new("error", Text: ex.Message));
                    else FailRoom(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { FailRoom("Room controls failed: " + ex.Message); }
    }
    private void HandleDiscovery(WireMessage message)
    {
        switch (message.Type)
        {
            case "welcome": case "admitted":
                var welcome = Wire.Read<Welcome>(message.Data!);
                if (!welcome.Host && welcome.GuestKey is null && Identity?.Room == welcome.Room)
                    welcome = welcome with { GuestKey = Identity.GuestKey };
                Identity = welcome; admitted = Identity.Host || message.Type == "admitted";
                if (Identity.GuestKey is { Length: 64 } key) settings?.Setting(RoomSetting(Identity.Room, "guestKey"), key);
                if (Identity.Host && coordinator is null)
                {
                    hostPeer = Identity.Peer; coordinator = new(Identity.Peer);
                    coordinator.Discover([new(Identity.Peer, displayName, true, true)], clock.Now);
                    var storedName = initialRoomName ?? settings?.Setting(RoomSetting(Identity.Room, "name"));
                    if (!HostRoomCoordinator.ValidName(storedName)) storedName = "Room " + Identity.Room[..6];
                    coordinator.Apply(Identity.Peer, new("room-name", Text: storedName), clock.Now, out _);
                    settings?.Setting(RoomSetting(Identity.Room, "name"), coordinator.Name!);
                    var storedControls = settings?.Setting(RoomSetting(Identity.Room, "sharedControls"));
                    roomControlsRestored = storedControls is not null;
                    if (storedControls == "true")
                        coordinator.Apply(Identity.Peer, new("controls", Number: 1), clock.Now, out _);
                    if (roomControlsRestored) QueueDiscovery(new("settings", Text: coordinator.Name, Number: coordinator.SharedControls ? 1 : 0));
                    Snapshot = coordinator.Snapshot; directConnected.TrySetResult();
                }
                connected.TrySetResult(Identity); Message?.Invoke(message); break;
            case "snapshot":
                var discovery = Wire.Read<RoomSnapshot>(message.Data!);
                if (Identity?.Host == true)
                {
                    if (!roomControlsRestored)
                    {
                        coordinator!.Apply(Identity.Peer, new("controls", Number: discovery.SharedControls ? 1 : 0), clock.Now, out _);
                        settings?.Setting(RoomSetting(Identity.Room, "sharedControls"), discovery.SharedControls ? "true" : "false");
                        roomControlsRestored = true;
                        QueueDiscovery(new("settings", Text: coordinator.Name, Number: coordinator.SharedControls ? 1 : 0));
                    }
                    coordinator!.AdmittedGuests = discovery.AdmittedGuests ?? [];
                    coordinator!.Discover(discovery.People, clock.Now); PublishHostState();
                }
                else
                {
                    hostPeer = discovery.People.SingleOrDefault(p => p.IsHost && p.Approved)?.Id ?? hostPeer;
                    // Once bootstrapped, only the host's direct channel can update
                    // the room. Sites snapshots contain stale readiness/playback.
                    if (!directConnected.Task.IsCompletedSuccessfully)
                    { Snapshot = new(discovery.People, null, null, false, [], Name: HostRoomCoordinator.ValidName(discovery.Name) ? discovery.Name : Snapshot?.Name); Message?.Invoke(new("snapshot", Data: Wire.Serialize(Snapshot))); }
                }
                break;
            case "connect":
                if (Identity?.Host == true)
                {
                    coordinator!.Discover(coordinator.Snapshot.People.Select(p => p.Id == message.Target ? p with { Approved = true } : p).ToArray(), clock.Now);
                    CreatePeer(message.Target!, true); PublishHostState();
                }
                break;
            case "signal":
                if (!admitted || Identity?.Host == false && message.Sender != hostPeer) return;
                var peer = peers.TryGetValue(message.Sender!, out var existing) ? existing : CreatePeer(message.Sender!, false);
                peer.ReceiveSignal(message.Text!, message.Data!); break;
            case "peer-left": if (message.Sender is not null) DepartPeer(message.Sender); break;
            case "error":
                if (Identity is null) FailRoom(message.Text ?? "Could not join this room");
                else Message?.Invoke(message);
                break;
            // Playback, queues, chat and clock replies from discovery are ignored.
        }
    }
    private void HandleLocal(WireMessage message)
    {
        if (Identity?.Host == true)
        {
            if (message.Type == "remove")
            { if (message.Target != Identity.Peer && message.Target is not null) { DepartPeer(message.Target); if (DiscoveryOnline) QueueDiscovery(message); } return; }
            ApplyHostCommand(Identity.Peer, message);
        }
        else if (hostPeer is not null && negotiated.ContainsKey(hostPeer)) SendPeer(hostPeer, message);
        else throw new IOException("The host's control channel is not connected. Rejoin the room.");
    }
    private void HandlePeer(string sender, WireMessage message)
    {
        if (Identity?.Host == true && !coordinator!.Approved(sender) || Identity?.Host == false && sender != hostPeer) return;
        if (message.Type == "control-hello")
        {
            if (message.Number != 2) { DepartPeer(sender, "Update all participants to use direct room controls."); return; }
            negotiated.TryAdd(sender, 0);
            connection?.UseDiscoveryPolling();
            if (Identity?.Host == true) PublishHostState();
            return;
        }
        if (!negotiated.ContainsKey(sender)) return;
        if (Identity?.Host == true)
        {
            if (message.Type == "clock-ping") { SendPeer(sender, new("clock-pong", Number: message.Number, Data: clock.Now.ToString())); return; }
            if (message.Type == "bye") { DepartPeer(sender); return; }
            ApplyHostCommand(sender, message);
        }
        else
        {
            switch (message.Type)
            {
                case "host-state":
                    var state = Wire.Read<HostRoomState>(message.Data!);
                    if (state.Sequence <= receivedSequence) return;
                    if (state.HostNowMs <= 0 || !state.Snapshot.People.Any(p => p.Id == sender && p.IsHost && p.Approved)) throw new InvalidDataException("Invalid host room state");
                    if (!directConnected.Task.IsCompletedSuccessfully) clock.Initialize(state.HostNowMs);
                    receivedSequence = state.Sequence; ApplySnapshot(state.Snapshot);
                    directConnected.TrySetResult();
                    break;
                case "clock-pong": if (long.TryParse(message.Data, out var now)) clock.Receive(message.Number, now); break;
                case "chat": case "notice": case "error": Message?.Invoke(message); break;
            }
        }
    }
    private void ApplyHostCommand(string sender, WireMessage message)
    {
        if (!coordinator!.Apply(sender, message, clock.Now, out var announcement))
        {
            var error = new WireMessage("error", Text: "The host controls this action, or the command is no longer valid.");
            if (sender == Identity!.Peer) Message?.Invoke(error); else SendPeer(sender, error);
            return;
        }
        if (sender == Identity!.Peer && message.Type == "controls")
        {
            roomControlsRestored = true;
            settings?.Setting(RoomSetting(Identity.Room, "sharedControls"), coordinator.SharedControls ? "true" : "false");
            if (DiscoveryOnline) QueueDiscovery(new("settings", Text: coordinator.Name, Number: coordinator.SharedControls ? 1 : 0));
        }
        if (sender == Identity!.Peer && message.Type == "room-name")
        {
            settings?.Setting(RoomSetting(Identity.Room, "name"), coordinator.Name!);
            if (DiscoveryOnline) QueueDiscovery(new("settings", Text: coordinator.Name, Number: coordinator.SharedControls ? 1 : 0));
        }
        PublishHostState();
        if (announcement is not null) { Message?.Invoke(announcement); Broadcast(announcement); }
        if (PlaybackDiagnostics.Enabled && message.Type == "playback") PlaybackDiagnostics.Record("control-sent", new { message.Data, transport = "peer" });
    }
    private void ApplySnapshot(RoomSnapshot snapshot)
    {
        var previous = Snapshot; Snapshot = snapshot;
        if (snapshot.Playback is { } playback && previous?.Playback is { } before && before.MediaId == playback.MediaId && before.Revision != playback.Revision)
            Message?.Invoke(new("playback", Data: Wire.Serialize(playback)));
        Message?.Invoke(new("snapshot", Data: Wire.Serialize(snapshot)));
    }
    private void PublishHostState()
    {
        var snapshot = coordinator!.Snapshot;
        ApplySnapshot(snapshot);
        var publicSnapshot = snapshot with { People = snapshot.People.Where(p => p.Approved).Select(p => p with { GuestId = null }).ToArray(), AdmittedGuests = null };
        Broadcast(new("host-state", Data: Wire.Serialize(new HostRoomState(++stateSequence, clock.Now, publicSnapshot))));
    }
    private void Broadcast(WireMessage message)
    {
        foreach (var id in negotiated.Keys.ToArray())
            if (coordinator!.Approved(id)) SendPeer(id, message);
    }
    private void SendPeer(string id, WireMessage message)
    {
        if (!peers.TryGetValue(id, out var peer) || !peer.IsControlOpen) { DepartPeer(id); return; }
        try { peer.SendControl(message); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { DepartPeer(id); }
    }
    private void DepartPeer(string id, string? reason = null)
    {
        negotiated.TryRemove(id, out _);
        if (peers.TryRemove(id, out var peer)) peer.Dispose();
        if (Identity?.Host == true)
        { coordinator!.Depart(id, clock.Now); PublishHostState(); Message?.Invoke(new("peer-left", Sender: id)); }
        else if (id == hostPeer) FailRoom(reason ?? "Host disconnected. Playback paused. Rejoin when the host returns.");
    }
    private PeerTransport CreatePeer(string id, bool offer)
    {
        if (Identity is null) throw new InvalidOperationException("Missing room identity");
        var peer = new PeerTransport(Identity.IceServers, Identity.ForceRelay);
        peer.ResolveMedia = media => Identity?.Host == true && negotiated.ContainsKey(id) && Snapshot?.People.Any(p => p.Id == id && p.Approved) == true
            ? HostedMedia?.Media.Id == media ? HostedMedia : assets.GetValueOrDefault(media) : null;
        peer.Signal += (type, data) => { try { QueueDiscovery(new("signal", Target: id, Text: type, Data: data)); } catch (IOException) { } };
        peer.Status += text => Status?.Invoke(text);
        peer.ControlMessage += message => Post(new("peer", id, message, peer));
        peer.ControlClosed += () => Post(new("closed", id, Transport: peer));
        peers[id] = peer;
        if (offer) peer.StartOffer();
        _ = MonitorPeer(id, peer);
        return peer;
    }
    private async Task MonitorPeer(string id, PeerTransport peer)
    {
        try
        {
            await peer.ControlReady.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            Post(new("opened", id, Transport: peer));
            await Task.Delay(15000, lifetime.Token);
            Post(new("timeout", id, Transport: peer));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Post(new("timeout", id, Transport: peer)); }
    }
    public async Task<IMediaSource> GetSourceAsync(SharedMedia media, CancellationToken ct)
    {
        if (Identity?.Host == true) return HostedMedia?.Media.Id == media.Id ? HostedMedia : assets.GetValueOrDefault(media.Id) ?? throw new InvalidOperationException("No shared media selected");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        await directConnected.Task.WaitAsync(deadline.Token);
        var peer = hostPeer is not null && peers.TryGetValue(hostPeer, out var host) ? host : throw new IOException("Host media connection is unavailable");
        await peer.Ready.WaitAsync(deadline.Token);
        return new RemoteMediaSource(peer, media);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Identity?.Host == false && hostPeer is not null && peers.TryGetValue(hostPeer, out var host) && host.IsControlOpen)
            try { host.SendControl(new("bye")); } catch { }
        lifetime.Cancel(); discoveryLifetime.Cancel(); outgoing.Writer.TryComplete(); events.Writer.TryComplete(); connection?.Abort();
        if (readTask is not null) { try { await readTask; } catch { } }
        if (sendTask is not null) { try { await sendTask; } catch { } }
        if (controlTask is not null) { try { await controlTask; } catch { } }
        if (pingTask is not null) { try { await pingTask; } catch { } }
        foreach (var peer in peers.Values) peer.Dispose(); peers.Clear();
        if (connection is not null) await connection.DisposeAsync();
        directConnected.TrySetCanceled();
    }
}

internal sealed class MediaWithSubtitles(FileMediaSource source, SharedMedia[] subtitles) : ILocalMediaSource
{
    public string LocalPath => source.LocalPath;
    public SharedMedia Media { get; } = source.Media with { Subtitles = subtitles };
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => source.ReadAsync(offset, count, ct);
}
