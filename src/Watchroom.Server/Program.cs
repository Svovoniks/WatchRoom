using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
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
// Behind the bundled reverse proxy, rate limits apply to the client address it reports; only loopback proxies are trusted.
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.UseRateLimiter();
// Half-open sockets close after a missed pong so a returning host is not rejected for minutes.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(30) });
var rooms = new ConcurrentDictionary<string, Room>();
int Setting(string name, int fallback) => int.TryParse(builder.Configuration[name], out var value) && value > 0 ? value : fallback;
// Temporary rooms cannot be resumed by their host, so an empty one only waits briefly for rejoining guests.
var emptyRoomGraceMs = Setting("WATCHROOM_EMPTY_ROOM_SECONDS", 300) * 1000L;
var persistentPerHour = Setting("WATCHROOM_PERSISTENT_ROOMS_PER_HOUR", 20);
const int TemporaryRoomLimit = 1000, PersistentRoomLimit = 10000;
var persistentCreations = new ConcurrentDictionary<string, Queue<long>>();
bool AllowPersistent(string address, long now)
{
    var times = persistentCreations.GetOrAdd(address, _ => new());
    lock (times)
    {
        while (times.Count > 0 && now - times.Peek() > 3600000) times.Dequeue();
        if (times.Count >= persistentPerHour) return false;
        times.Enqueue(now); return true;
    }
}
void SweepRooms(long now)
{
    foreach (var pair in rooms)
        if (!pair.Value.Persistent)
            lock (pair.Value.Gate)
                if (pair.Value.Expires < now || pair.Value.People.Count == 0 && now - pair.Value.EmptySince >= emptyRoomGraceMs) rooms.TryRemove(pair);
    foreach (var pair in persistentCreations)
        lock (pair.Value) if (pair.Value.All(at => now - at > 3600000)) persistentCreations.TryRemove(pair);
}
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Setting("WATCHROOM_SWEEP_SECONDS", 60)));
    try { while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping)) SweepRooms(Wire.Now); }
    catch (OperationCanceledException) { }
});
var persistentRooms = new PersistentRooms(builder.Configuration["WATCHROOM_DATA"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
foreach (var saved in persistentRooms.All())
{
    var restored = new Room(saved.Code, true) { SharedControls = saved.SharedControls, Name = saved.Name };
    restored.ApprovedGuests.UnionWith(saved.ApprovedGuests ?? []); rooms[saved.Code] = restored;
    foreach (var guest in saved.Guests ?? []) restored.GuestNames[guest.Id] = guest.Name;
}
void SaveRoom(Room room) { if (room.Persistent) persistentRooms.Update(room.Code, room.Guests(), room.SharedControls, room.Name); }
var cloudflare = CloudflareTurn.Create(builder.Configuration["CLOUDFLARE_TURN_KEY_ID"], builder.Configuration["CLOUDFLARE_TURN_API_TOKEN"],
    builder.Configuration["CLOUDFLARE_TURN_API"] ?? "https://rtc.live.cloudflare.com/v1", app.Logger);
if (cloudflare is not null) await cloudflare.RefreshAsync(app.Lifetime.ApplicationStopping);
string[] IceServers(string peer)
{
    var urls = new List<string>();
    var stun = builder.Configuration["WATCHROOM_STUN"];
    if (!string.IsNullOrWhiteSpace(stun)) urls.Add(stun);
    if (cloudflare is not null) urls.AddRange(cloudflare.Current());
    var turn = builder.Configuration["WATCHROOM_TURN"];
    var secret = builder.Configuration["WATCHROOM_TURN_SECRET"];
    if (!string.IsNullOrWhiteSpace(turn) && !string.IsNullOrWhiteSpace(secret))
    {
        var username = $"{DateTimeOffset.UtcNow.AddHours(8).ToUnixTimeSeconds()}:{peer}";
        var credential = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username)));
        foreach (var address in turn.Split(';', StringSplitOptions.RemoveEmptyEntries)) urls.Add(CloudflareTurn.WithCredentials(address, username, credential));
    }
    return urls.ToArray();
}
bool forceRelay = builder.Configuration["WATCHROOM_FORCE_RELAY"] == "true";
app.MapGet("/health", () => Results.Ok(new { status = "ok", protocol = 1, persistentRooms = true,
    relayConfigured = cloudflare?.HasRelay == true || !string.IsNullOrEmpty(builder.Configuration["WATCHROOM_TURN"]),
    stunConfigured = cloudflare is not null || !string.IsNullOrEmpty(builder.Configuration["WATCHROOM_STUN"]) }));
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
            // Only live temporary rooms count toward the temporary cap; persistent rooms have their own limits.
            if (first.Target == "persistent" ? persistentRooms.Count >= PersistentRoomLimit || !AllowPersistent(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", Wire.Now)
                : rooms.Values.Count(x => !x.Persistent && x.Expires >= Wire.Now) >= TemporaryRoomLimit)
                throw new InvalidDataException(first.Target == "persistent" ? "Persistent room limit reached. Try again later." : "Server room limit reached");
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
        var guestKey = host ? null : first.Sender is { Length: 64 } key && key.All(Uri.IsHexDigit) ? key : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        member = new Member(Guid.NewGuid().ToString("N"), name, host, socket, lifetime, guestKey);
        var sender = member.SendLoop();
        lock (room.Gate)
        {
            // The sweep removes rooms under this lock; never admit a member into a removed room.
            if (!rooms.TryGetValue(room.Code, out var live) || live != room) throw new InvalidDataException("Invitation is invalid or expired");
            if (room.People.Count >= 9) throw new InvalidDataException("Room is full");
            if (host && room.People.Values.Any(x => x.Host)) throw new InvalidDataException("The host is already connected to this room");
            if (!host && room.ApprovedGuests.Contains(member.GuestHash!) && room.People.Values.Count(x => x.Approved) < 5 && !room.People.Values.Any(x => x.GuestHash == member.GuestHash)) member.Approved = true;
            room.People.Add(member.Id, member);
            member.Send(new(member.Approved && !host ? "admitted" : "welcome", Data: Wire.Serialize(new Welcome(room.Code, member.Id, host, member.Approved ? IceServers(member.Id) : [], forceRelay, hostKey, guestKey))));
            room.Snapshot();
            if (!host && member.Approved) room.People.Values.FirstOrDefault(x => x.Host)?.Send(new("connect", Target: member.Id));
            if (host) foreach (var guest in room.People.Values.Where(x => !x.Host && x.Approved)) member.Send(new("connect", Target: guest.Id));
        }
        // Token bucket: the host's burst covers resume signaling for every guest; sustained floods still disconnect.
        var (burst, perSecond) = host ? (400.0, 160.0) : (120.0, 80.0);
        var tokens = burst; var refilled = Wire.Now;
        while (!lifetime.IsCancellationRequested)
        {
            var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
            var now = Wire.Now; tokens = Math.Min(burst, tokens + (now - refilled) * perSecond / 1000); refilled = now;
            if (--tokens < 0) throw new InvalidDataException("Message rate limit exceeded");
            lock (room.Gate)
            {
                if (message.Type == "ping") { member.Send(new("pong", Number: message.Number, Data: Wire.Now.ToString())); continue; }
                if (!member.Approved) continue;
                switch (message.Type)
                {
                    case "reconnect" when !member.Host && member.AllowBroadcast():
                        room.Snapshot();
                        room.People.Values.FirstOrDefault(x => x.Host)?.Send(new("connect", Target: member.Id));
                        break;
                    case "admit" when member.Host:
                        if (room.People.TryGetValue(message.Target ?? "", out var guest) && !guest.Approved && room.People.Values.Count(x => x.Approved) < 5)
                        {
                            guest.Approved = true;
                            room.ApprovedGuests.Add(guest.GuestHash!); room.GuestNames[guest.GuestHash!] = guest.Name; SaveRoom(room);
                            guest.Send(new("admitted", Data: Wire.Serialize(new Welcome(room.Code, guest.Id, false, IceServers(guest.Id), forceRelay, GuestKey: guest.GuestKey))));
                            member.Send(new("connect", Target: guest.Id)); room.Snapshot();
                        }
                        break;
                    case "remove" when member.Host:
                        var removed = room.People.Values.FirstOrDefault(x => !x.Host && (x.Id == message.Target || x.GuestHash == message.Target));
                        if (removed is not null)
                        { room.ApprovedGuests.Remove(removed.GuestHash!); room.GuestNames.Remove(removed.GuestHash!); SaveRoom(room); removed.Approved = false; removed.Lifetime.Cancel(); room.Broadcast(new("peer-left", Sender: removed.Id)); room.Snapshot(); }
                        else if (room.ApprovedGuests.Remove(message.Target ?? ""))
                        { room.GuestNames.Remove(message.Target!); SaveRoom(room); room.Snapshot(); }
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
                    case "settings" when member.Host:
                    case "controls" when member.Host:
                        if (message.Text is not null)
                        {
                            if (string.IsNullOrWhiteSpace(message.Text) || message.Text.Trim().Length > 100 || message.Text.Any(char.IsControl)) throw new InvalidDataException("Invalid room name");
                            room.Name = message.Text.Trim();
                        }
                        room.SharedControls = message.Number == 1; SaveRoom(room); room.Snapshot(); break;
                    case "queue" when member.Host:
                        var queue = Wire.Read<string[]>(message.Data!);
                        if (queue.Length > 1000 || queue.Any(x => x is null || x.Length > 300)) throw new InvalidDataException("Invalid queue");
                        room.Queue = queue; room.Snapshot(); break;
                    case "playback" when (member.Host || room.SharedControls) && member.AllowBroadcast():
                        var desired = Wire.Read<PlaybackState>(message.Data!);
                        if (room.Media is null || desired.MediaId != room.Media.Id || desired.PositionMs < 0 || desired.PositionMs > 7 * 86400000L) break;
                        room.ResumeWhenReady = desired.Playing && room.People.Values.Any(x => x.Approved && !x.Ready);
                        room.Playback = desired with { Revision = ++room.Revision, Playing = desired.Playing && !room.ResumeWhenReady, AtUnixMs = Wire.Now + (desired.Playing && !room.ResumeWhenReady ? 200 : 0) };
                        room.Broadcast(new("playback", Data: Wire.Serialize(room.Playback))); break;
                    // Readiness broadcasts reach every member; ignore unchanged state and bound each member's rate.
                    case "buffering" when (member.Ready || room.Playback is { Playing: true }) && member.AllowBroadcast():
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
                    case "ready" when !member.Ready && member.AllowBroadcast():
                        member.Ready = true; room.TryResume(); room.Snapshot(); break;
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
                    room.Media = null; room.Playback = null; room.Queue = []; room.ResumeWhenReady = false;
                    room.Broadcast(new("peer-left", Sender: member.Id)); room.Snapshot();
                }
                else if (registered) { room.Broadcast(new("peer-left", Sender: member.Id)); room.TryResume(); room.Snapshot(); }
                if (registered && room.People.Count == 0) room.EmptySince = Wire.Now;
            }
        }
        lifetime.Cancel(); socket.Abort();
    }
});
await app.RunAsync();

// Cloudflare issues shared short-lived TURN credentials through an authenticated API; the server fetches them in the
// background so admission never waits on Cloudflare, and every credential handed out stays valid for at least 12 hours.
sealed class CloudflareTurn(string keyId, string token, string api, ILogger logger)
{
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24), RefreshBefore = TimeSpan.FromHours(12);
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    string[] urls = ["stun:stun.cloudflare.com:3478"];
    DateTimeOffset expires = DateTimeOffset.MinValue, retryAfter = DateTimeOffset.MinValue;
    int refreshing;
    public bool HasRelay => urls.Any(x => x.StartsWith("turn", StringComparison.Ordinal));
    public static CloudflareTurn? Create(string? keyId, string? token, string api, ILogger logger) =>
        string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(token) ? null : new(keyId.Trim(), token.Trim(), api.TrimEnd('/'), logger);
    public string[] Current()
    {
        var now = DateTimeOffset.UtcNow;
        if (now > expires - RefreshBefore && now > retryAfter) _ = RefreshAsync(CancellationToken.None);
        // Expired relay credentials only produce failed allocations, so fall back to STUN alone.
        return now < expires ? urls : ["stun:stun.cloudflare.com:3478"];
    }
    public async Task RefreshAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/turn/keys/{Uri.EscapeDataString(keyId)}/credentials/generate-ice-servers")
            { Content = JsonContent.Create(new { ttl = (int)Lifetime.TotalSeconds }) };
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, cancellation);
            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var servers = json.RootElement.GetProperty("iceServers");
            var parsed = Parse(Items(servers));
            if (parsed.Length == 0) throw new InvalidDataException("Cloudflare returned no ICE servers");
            urls = parsed; expires = DateTimeOffset.UtcNow + Lifetime;
            logger.LogInformation("Cloudflare TURN credentials refreshed ({Count} servers)", parsed.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
        {
            retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            logger.LogWarning("Cloudflare TURN credentials unavailable: {Error}", ex.Message);
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }
    static IEnumerable<System.Text.Json.JsonElement> Items(System.Text.Json.JsonElement value) =>
        value.ValueKind == System.Text.Json.JsonValueKind.Array ? value.EnumerateArray() : [value];
    static string[] Parse(IEnumerable<System.Text.Json.JsonElement> servers)
    {
        var result = new List<string>();
        foreach (var server in servers)
        {
            var username = server.TryGetProperty("username", out var u) ? u.GetString() : null;
            var credential = server.TryGetProperty("credential", out var c) ? c.GetString() : null;
            if (!server.TryGetProperty("urls", out var list)) continue;
            foreach (var entry in Items(list))
            {
                // Port 53 duplicates the standard ports and is commonly intercepted by local DNS resolvers.
                if (entry.GetString() is not { Length: > 0 } url || System.Text.RegularExpressions.Regex.IsMatch(url, @":53(\?|$)")) continue;
                if (url.StartsWith("stun:", StringComparison.Ordinal)) result.Add(url);
                else if (url.StartsWith("turn", StringComparison.Ordinal) && username is not null && credential is not null) result.Add(WithCredentials(url, username, credential));
            }
        }
        return result.ToArray();
    }
    // libdatachannel uses TURN URLs with escaped userinfo.
    public static string WithCredentials(string address, string username, string credential)
    {
        var separator = address.IndexOf(':');
        return address[..(separator + 1)] + "//" + Uri.EscapeDataString(username) + ":" + Uri.EscapeDataString(credential) + "@" + address[(separator + 1)..].TrimStart('/');
    }
}

sealed class Member(string id, string name, bool host, WebSocket socket, CancellationTokenSource lifetime, string? guestKey = null)
{
    public string? GuestKey => guestKey;
    public string? GuestHash { get; } = guestKey is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guestKey)));
    public string Id => id; public string Name => name; public bool Host { get; } = host;
    public bool Approved = host;
    public bool Ready;
    private double broadcastTokens = 20; private long broadcastRefilled = Wire.Now;
    // Called under the room lock for member messages that broadcast to everyone.
    public bool AllowBroadcast()
    {
        var now = Wire.Now; broadcastTokens = Math.Min(20, broadcastTokens + (now - broadcastRefilled) * 4 / 1000.0); broadcastRefilled = now;
        if (broadcastTokens < 1) return false;
        broadcastTokens--; return true;
    }
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
    public object Gate { get; } = new(); public string Code => code;
    public bool Persistent { get; } = persistent;
    public string? Name;
    public long Expires { get; } = persistent ? long.MaxValue : Wire.Now + 6 * 3600000; public long Revision;
    public long EmptySince = Wire.Now;
    public Dictionary<string, Member> People { get; } = new();
    public HashSet<string> ApprovedGuests { get; } = [];
    public Dictionary<string, string> GuestNames { get; } = [];
    public AdmittedGuest[] Guests() => ApprovedGuests.Select(id => new AdmittedGuest(id, GuestNames.GetValueOrDefault(id, "Guest"))).ToArray();
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
            var visible = People.Values.Where(x => p.Host || x.Approved || x.Id == p.Id).Select(x => new Participant(x.Id, x.Name, x.Host, x.Approved, x.Ready, p.Host ? x.GuestHash : null)).ToArray();
            p.Send(new("snapshot", Data: Wire.Serialize(new RoomSnapshot(visible, p.Approved ? Media : null, p.Approved ? Playback : null, SharedControls, p.Approved ? Queue : [], p.Host ? Guests() : null, Name))));
        }
    }
}
