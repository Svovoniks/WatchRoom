using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Watchroom.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
var rooms = new ConcurrentDictionary<string, Room>();
var persistentRooms = new PersistentRooms(builder.Configuration["WATCHROOM_DATA"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
foreach (var saved in persistentRooms.All()) rooms[saved.Code] = new Room(saved.Code, true);
string[] IceServers(string peer)
{
    var urls = new List<string>();
    var stun = builder.Configuration["WATCHROOM_STUN"];
    if (!string.IsNullOrWhiteSpace(stun)) urls.Add(stun);
    var turn = builder.Configuration["WATCHROOM_TURN"];
    var secret = builder.Configuration["WATCHROOM_TURN_SECRET"];
    if (!string.IsNullOrWhiteSpace(turn) && !string.IsNullOrWhiteSpace(secret))
    {
        var username = $"{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()}:{peer}";
        var credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        foreach (var address in turn.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // libdatachannel uses TURN URLs with escaped userinfo.
            var separator = address.IndexOf(':');
            urls.Add(address[..(separator + 1)] + "//" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(credential) + "@" + address[(separator + 1)..].TrimStart('/'));
        }
    }
    return urls.ToArray();
}
bool forceRelay = builder.Configuration["WATCHROOM_FORCE_RELAY"] == "true";
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1, persistentRooms = true, relayConfigured = !string.IsNullOrEmpty(builder.Configuration["WATCHROOM_TURN"]) }));
app.Map("/room", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    lifetime.CancelAfter(TimeSpan.FromHours(6));
    Room? room = null; Member? member = null;
    try
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); handshake.CancelAfter(TimeSpan.FromSeconds(10));
        var first = await SocketMessages.ReadAsync(socket, handshake.Token);
        if (first is null || first.Type is not ("create" or "join" or "resume")) throw new InvalidDataException("Expected create, join or resume");
        var name = (first.Text ?? "Guest").Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl)) throw new InvalidDataException("Invalid display name");
        var host = first.Type is "create" or "resume";
        string? hostKey = null;
        if (first.Type == "create")
        {
            if (rooms.Count >= 1000) throw new InvalidDataException("Server room limit reached");
            room = new Room(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)), first.Target == "persistent");
            if (room.Persistent) hostKey = persistentRooms.Create(room.Code);
            rooms[room.Code] = room;
        }
        else if (!rooms.TryGetValue((first.Data ?? "").Trim().ToUpperInvariant(), out room) || room.Expires < Wire.Now) throw new InvalidDataException("Invitation is invalid or expired");
        if (first.Type == "resume")
        {
            if (!room!.Persistent || !persistentRooms.Verify(room.Code, first.Target)) throw new InvalidDataException("Host key is invalid");
            hostKey = first.Target;
        }
        member = new Member(Guid.NewGuid().ToString("N"), name, host, socket, lifetime);
        var sender = member.SendLoop();
        lock (room.Gate)
        {
            if (room.Closed || room.People.Count >= 9) throw new InvalidDataException("Room is full or closed");
            if (host && room.People.Values.Any(x => x.Host)) throw new InvalidDataException("The host is already connected to this room");
            if (!host && !room.People.Values.Any(x => x.Host)) throw new InvalidDataException("The host is offline. Try again when they reopen the room.");
            room.People.Add(member.Id, member);
            member.Send(new("welcome", Data: Wire.Serialize(new Welcome(room.Code, member.Id, host, host ? IceServers(member.Id) : [], forceRelay, hostKey))));
            room.Snapshot();
        }
        var messages = 0; var window = Wire.Now;
        while (!lifetime.IsCancellationRequested)
        {
            var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
            if (Wire.Now - window > 1000) { messages = 0; window = Wire.Now; }
            if (++messages > 80) throw new InvalidDataException("Message rate limit exceeded");
            lock (room.Gate)
            {
                if (message.Type == "ping") { member.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
                if (!member.Approved) continue;
                switch (message.Type)
                {
                    case "admit" when member.Host:
                        if (room.People.TryGetValue(message.Target ?? "", out var guest) && !guest.Approved && room.People.Values.Count(x => x.Approved) < 5)
                        {
                            guest.Approved = true;
                            guest.Send(new("admitted", Data: Wire.Serialize(new Welcome(room.Code, guest.Id, false, IceServers(guest.Id), forceRelay))));
                            member.Send(new("connect", Target: guest.Id)); room.Snapshot();
                        }
                        break;
                    case "remove" when member.Host:
                        if (room.People.TryGetValue(message.Target ?? "", out var removed) && !removed.Host)
                        { removed.Approved = false; removed.Lifetime.Cancel(); room.Broadcast(new("peer-left", Sender: removed.Id)); room.Snapshot(); }
                        break;
                    case "signal":
                        if (room.People.TryGetValue(message.Target ?? "", out var target) && target.Approved && (target.Host || member.Host)) target.Send(message with { Sender = member.Id });
                        break;
                    case "media" when member.Host:
                        var media = Wire.Read<SharedMedia>(message.Data!);
                        if (media.Length <= 0 || media.Title.Length > 300 || media.Id.Length > 128 || media.Extension.Length > 16) throw new InvalidDataException("Invalid media");
                        room.Media = media; room.ResumeWhenReady = false;
                        foreach (var p in room.People.Values) p.Ready = false;
                        room.Playback = new(++room.Revision, media.Id, false, 0, Wire.Now); room.Snapshot();
                        break;
                    case "controls" when member.Host:
                        room.SharedControls = message.Number == 1; room.Snapshot(); break;
                    case "queue" when member.Host:
                        var queue = Wire.Read<string[]>(message.Data!);
                        if (queue.Length > 1000 || queue.Any(x => x is null || x.Length > 300)) throw new InvalidDataException("Invalid queue");
                        room.Queue = queue; room.Snapshot(); break;
                    case "playback" when member.Host || room.SharedControls:
                        var desired = Wire.Read<PlaybackState>(message.Data!);
                        if (room.Media is null || desired.MediaId != room.Media.Id || desired.PositionMs < 0 || desired.PositionMs > 7 * 86400000L) break;
                        room.ResumeWhenReady = desired.Playing && room.People.Values.Any(x => x.Approved && !x.Ready);
                        room.Playback = desired with { Revision = ++room.Revision, Playing = desired.Playing && !room.ResumeWhenReady, AtUnixMs = Wire.Now + (desired.Playing && !room.ResumeWhenReady ? 200 : 0) };
                        room.Broadcast(new("playback", Data: Wire.Serialize(room.Playback))); break;
                    case "buffering":
                        member.Ready = false;
                        if (room.Playback is { Playing: true } current)
                        {
                            room.ResumeWhenReady = true;
                            room.Playback = current with { Revision = ++room.Revision, Playing = false, PositionMs = SyncMath.TargetPosition(current, Wire.Now), AtUnixMs = Wire.Now };
                            room.Broadcast(new("playback", Data: Wire.Serialize(room.Playback)));
                            room.Broadcast(new("notice", Text: $"{member.Name} is buffering. Playback resumes when everyone is ready."));
                        }
                        room.Snapshot();
                        break;
                    case "ready":
                        member.Ready = true; room.TryResume(); room.Snapshot(); break;
                    case "chat":
                        var text = message.Text?.Trim();
                        if (!string.IsNullOrEmpty(text) && text.Length <= 1000) room.Broadcast(new("chat", Sender: member.Id, Text: member.Name + ": " + text));
                        break;
                }
            }
        }
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
    {
        if (member is not null) { try { await member.SendAndWaitAsync(new("error", Text: ex.Message)); } catch (Exception) { } }
        else if (socket.State == WebSocketState.Open) await SocketMessages.SendAsync(socket, new("error", Text: ex.Message), CancellationToken.None);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
    finally
    {
        if (room is not null && member is not null)
        {
            lock (room.Gate)
            {
                var registered = room.People.Remove(member.Id);
                if (registered && member.Host)
                {
                    if (!room.Persistent) { room.Closed = true; rooms.TryRemove(room.Code, out _); }
                    foreach (var other in room.People.Values) other.Lifetime.Cancel();
                    room.People.Clear(); room.Media = null; room.Playback = null; room.Queue = []; room.SharedControls = false; room.ResumeWhenReady = false;
                }
                else if (registered) { room.Broadcast(new("peer-left", Sender: member.Id)); room.TryResume(); room.Snapshot(); }
            }
        }
        lifetime.Cancel(); socket.Abort();
    }
});
await app.RunAsync();

sealed class Member(string id, string name, bool host, WebSocket socket, CancellationTokenSource lifetime)
{
    public string Id => id; public string Name => name; public bool Host { get; } = host;
    public bool Approved = host;
    public bool Ready;
    public CancellationTokenSource Lifetime => lifetime;
    private readonly Channel<(WireMessage Message, TaskCompletionSource? Sent)> outgoing = Channel.CreateBounded<(WireMessage, TaskCompletionSource?)>(128);
    public void Send(WireMessage message) { if (!outgoing.Writer.TryWrite((message, null))) lifetime.Cancel(); }
    public async Task SendAndWaitAsync(WireMessage message)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!outgoing.Writer.TryWrite((message, sent))) throw new IOException("Room send queue full");
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
    }
    public async Task SendLoop()
    {
        try { await foreach (var msg in outgoing.Reader.ReadAllAsync(lifetime.Token)) { await SocketMessages.SendAsync(socket, msg.Message, lifetime.Token); msg.Sent?.TrySetResult(); } }
        catch (Exception) { lifetime.Cancel(); }
    }
}
sealed class Room(string code, bool persistent = false)
{
    public object Gate { get; } = new(); public string Code => code; public bool Closed;
    public bool Persistent { get; } = persistent;
    public long Expires { get; } = persistent ? long.MaxValue : Wire.Now + 6 * 3600000; public long Revision;
    public Dictionary<string, Member> People { get; } = new();
    public SharedMedia? Media; public PlaybackState? Playback; public bool SharedControls; public bool ResumeWhenReady; public string[] Queue = [];
    public void TryResume()
    {
        if (!ResumeWhenReady || Playback is null || People.Values.Any(x => x.Approved && !x.Ready)) return;
        ResumeWhenReady = false;
        Playback = Playback with { Revision = ++Revision, Playing = true, AtUnixMs = Wire.Now + 200 };
        Broadcast(new("playback", Data: Wire.Serialize(Playback)));
    }
    public void Broadcast(WireMessage message) { foreach (var p in People.Values.Where(x => x.Approved)) p.Send(message); }
    public void Snapshot()
    {
        foreach (var p in People.Values)
        {
            var visible = People.Values.Where(x => p.Host || x.Approved || x.Id == p.Id).Select(x => new Participant(x.Id, x.Name, x.Host, x.Approved, x.Ready)).ToArray();
            p.Send(new("snapshot", Data: Wire.Serialize(new RoomSnapshot(visible, p.Approved ? Media : null, p.Approved ? Playback : null, SharedControls, p.Approved ? Queue : []))));
        }
    }
}
