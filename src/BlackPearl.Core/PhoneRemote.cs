using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DataChannelDotnet;
using DataChannelDotnet.Bindings;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;

namespace BlackPearl.Core;

// The phone remote (PHONE-REMOTE.md): BlackPearl Mobile pairs with this PC once and then controls it over a direct
// WebRTC link, at home or away. The room server only passes signed signaling between them.
public static class PhoneRemoteProtocol
{
    public const int Version = 1;
    public const string Label = "remote-v1";
    // The second channel, for video and picture bytes, so a long read never holds up a command.
    public const string MediaLabel = "media-v1";
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    public static string PcId(string hostKey) => PersistentRooms.Hash(hostKey)[..32];
    public static string NewHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    static byte[] Text(string direction, string phone, string session, string kind, string payload) =>
        Encoding.UTF8.GetBytes($"bp-remote-v1\n{direction}\n{phone}\n{session}\n{kind}\n{payload}");
    public static string Sign(string keyHex, string direction, string phone, string session, string kind, string payload) =>
        System.Buffers.Text.Base64Url.EncodeToString(HMACSHA256.HashData(Convert.FromHexString(keyHex), Text(direction, phone, session, kind, payload)));
    public static bool Verify(string keyHex, string direction, RemoteSignal signal)
    {
        if (string.IsNullOrEmpty(signal.Auth)) return false;
        byte[] supplied;
        try { supplied = System.Buffers.Text.Base64Url.DecodeFromChars(signal.Auth); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Convert.FromHexString(keyHex), Text(direction, signal.Phone, signal.Session, signal.Kind, signal.Payload));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
    public static string PairingLink(string pcId, string code, string server, string pcName) =>
        $"blackpearl://pair?v={Version}&pc={pcId}&code={code}&server={Uri.EscapeDataString(server)}&name={Uri.EscapeDataString(pcName)}";
    public static Uri ServerSocket(string server)
    {
        var uri = new Uri(RoomAddress.ValidateServer(server) + "/remote");
        return new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" }.Uri;
    }
    internal static bool IsHex(string? text, int length) => text is { } value && value.Length == length && value.All(Uri.IsHexDigit);
}

public record RemoteWelcome(string[] IceServers, bool ForceRelay);
public record RemoteSignal(string Phone, string Session, string Kind, string Payload, string Auth);
public record RemoteSdp(string Type, string Sdp);
public record RemoteIce(string Candidate, string? Mid);
public record PairedPhone(string Id, string Name, string Secret, long PairedAt, long LastSeen);
// What a phone asked for. Value is a number or a bool for the actions that take one.
public record RemoteCommand(string Action, JsonElement? Value)
{
    public double? Number => Value is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;
}
public record RemoteRoom(string Name, int People);
// The saved queue the PC plays from, and the playing video's place in it (PHONE-REMOTE.md, "Queues").
public record RemoteQueue(string Id, string Name, int Index);
// What the phone shows. Null fields are left out when nothing is loaded. MediaId names the library video the PC plays on
// its own (not in a room), for the phone to take it over.
public record RemoteState(bool Media, string? Title = null, string? Detail = null, bool? Playing = null, long? PositionMs = null, long? DurationMs = null,
    int Volume = 100, bool Muted = false, string Loop = "off", bool Fullscreen = false, bool HasPrevious = false, bool HasNext = false, bool CanControl = true, RemoteRoom? Room = null,
    string? MediaId = null, RemoteQueue? Queue = null)
{
    public string Type => "state";
}

public interface IPairedPhoneStore
{
    IReadOnlyList<PairedPhone> Load();
    void Save(IReadOnlyList<PairedPhone> phones);
}

public sealed class PhoneRemoteOptions
{
    public required string Server { get; init; }
    public required string HostKey { get; init; }
    public required string PcName { get; init; }
    public required string AppVersion { get; init; }
    public required IPairedPhoneStore Store { get; init; }
    // Whether paired phones may start videos and change queues on their own; sent in welcome.
    public bool CanStart { get; init; } = true;
    // How long a PC replaced by another copy of its library waits before taking the phone remote back.
    public TimeSpan ReplacedWait { get; init; } = TimeSpan.FromMinutes(1);
    // What this PC offers beyond the remote, sent in welcome: "library" (browse), "watch" (play on the phone).
    public IReadOnlyList<string> Features { get; init; } = [];
}

// One phone's link. PhoneId is "pair" until a pairing session pairs.
public sealed class PhoneRemoteSession
{
    internal PhoneRemoteSession(string phoneId, string session, string signalingPeer, string key, RtcPeerConnection peer)
    { PhoneId = phoneId; SignalAs = phoneId; Session = session; SignalingPeer = signalingPeer; Key = key; Peer = peer; }
    // The phone id its signals are signed under: "pair" for a session that began with a pairing code, even once paired.
    internal string SignalAs { get; }
    public string PhoneId { get; internal set; }
    public string Session { get; }
    public string? Device { get; internal set; }
    public bool Routed { get; internal set; }
    public bool Relayed { get; internal set; }
    internal string SignalingPeer { get; set; }
    internal string Key { get; }
    internal RtcPeerConnection Peer { get; }
    internal IRtcDataChannel? Channel { get; set; }
    internal PhoneMediaServer? Media { get; set; }
    internal bool Greeted { get; set; }
    public bool Open => Channel?.IsOpen == true && Greeted;
}

public sealed class PhoneRemoteHost : IDisposable
{
    private readonly PhoneRemoteOptions options;
    private readonly object gate = new();
    private readonly Dictionary<string, PhoneRemoteSession> sessions = new();
    private readonly HashSet<string> rejected = new();
    private readonly CancellationTokenSource lifetime = new();
    private Channel<WireMessage> outgoing = Channel.CreateUnbounded<WireMessage>();
    private string[] iceServers = [];
    private bool forceRelay;
    private string? pairingCode;
    private DateTimeOffset pairingExpires;
    private List<PairedPhone> phones;
    private Task? running;
    private int disposed;

    public PhoneRemoteHost(PhoneRemoteOptions options)
    {
        this.options = options;
        phones = options.Store.Load().ToList();
        PcId = PhoneRemoteProtocol.PcId(options.HostKey);
    }

    public string PcId { get; }
    public string Status { get; private set; } = "Starting";
    public bool ServerReady { get; private set; }
    // Raised on threads other than the UI's.
    public event Action? Changed;
    public event Action<PhoneRemoteSession, RemoteCommand>? Command;
    public event Action<PairedPhone>? Paired;
    // Any other message from a paired phone, such as browse or watch: its type and the whole message.
    public event Action<PhoneRemoteSession, string, JsonElement>? Request;
    // A paired phone's link closed.
    public event Action<PhoneRemoteSession>? Disconnected;
    // Answers the phone's reads on its media channel. Called on threads other than the UI's.
    public PhoneResourceResolver? ResolveMedia { get; set; }
    // Called for every phone that opens a link, to send it the current state. Null sends nothing; the caller then
    // publishes the state itself.
    public Func<RemoteState?>? CurrentState { get; set; }

    public IReadOnlyList<PairedPhone> Phones { get { lock (gate) return phones.ToArray(); } }
    public IReadOnlyList<PhoneRemoteSession> Connected { get { lock (gate) return sessions.Values.Where(s => s.Open && s.PhoneId != "pair").ToArray(); } }

    public void Start() => running ??= Task.Run(() => RunAsync(lifetime.Token));

    // A new one-use pairing link, valid for ten minutes; the previous one stops working.
    public string BeginPairing()
    {
        lock (gate)
        {
            pairingCode = PhoneRemoteProtocol.NewHex(16);
            pairingExpires = DateTimeOffset.UtcNow + PhoneRemoteProtocol.PairingLifetime;
            return PhoneRemoteProtocol.PairingLink(PcId, pairingCode, RoomAddress.ValidateServer(options.Server), options.PcName);
        }
    }
    public void CancelPairing() { lock (gate) pairingCode = null; }
    public bool PairingOpen { get { lock (gate) return pairingCode is not null && DateTimeOffset.UtcNow < pairingExpires; } }

    // Removes a phone. Its signatures stop verifying at once and its open link closes.
    public void Forget(string phoneId)
    {
        PhoneRemoteSession? open;
        lock (gate)
        {
            if (phones.RemoveAll(p => p.Id == phoneId) == 0) return;
            options.Store.Save(phones);
            sessions.Remove(phoneId, out open);
        }
        if (open is not null) { Close(open); Disconnected?.Invoke(open); }
        Changed?.Invoke();
    }

    public void Publish(RemoteState state)
    {
        var text = Wire.Serialize(state);
        PhoneRemoteSession[] open;
        // Only paired phones: a pairing session hasn't been granted anything yet.
        lock (gate) open = sessions.Values.Where(s => s.Open && s.PhoneId != "pair").ToArray();
        foreach (var session in open) Send(session, text);
    }

    public void SendError(PhoneRemoteSession session, string code, string message) =>
        Send(session, Wire.Serialize(new { type = "error", code, message }));

    // Sends a message to one phone. One larger than the channel's 64 KiB limit throws.
    public void SendMessage(PhoneRemoteSession session, object message)
    {
        var text = Wire.Serialize(message);
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new InvalidDataException("Phone message is too large");
        Send(session, text);
    }

    private void Send(PhoneRemoteSession session, string text)
    {
        try { if (session.Channel?.IsOpen == true) session.Channel.Send(text); }
        catch (Exception ex) when (ex.GetType().Namespace == "DataChannelDotnet.Exceptions" || ex is ObjectDisposedException or InvalidOperationException) { }
    }

    private void SetStatus(string status, bool ready)
    {
        Status = status; ServerReady = ready;
        Changed?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            bool refused = false, replaced = false;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(PhoneRemoteProtocol.ServerSocket(options.Server), connect.Token);
                }
                await SocketMessages.SendAsync(socket, new("remote-pc", Data: options.HostKey, Number: PhoneRemoteProtocol.Version), ct);
                var queue = outgoing = Channel.CreateUnbounded<WireMessage>();
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var writer = WriteAsync(socket, queue, connection.Token);
                try
                {
                    while (await SocketMessages.ReadAsync(socket, connection.Token) is { } message)
                    {
                        switch (message.Type)
                        {
                            case "remote-welcome":
                                var welcome = Wire.Read<RemoteWelcome>(message.Data ?? "{}");
                                lock (gate) { iceServers = welcome.IceServers ?? []; forceRelay = welcome.ForceRelay; }
                                delay = TimeSpan.FromSeconds(1);
                                SetStatus("Ready", true);
                                break;
                            case "signal" when message.Sender is { Length: > 0 } sender && message.Data is { Length: > 0 } data:
                                // Anyone who knows this PC's id can send signals, so none of them may end this loop.
                                try { HandleSignal(sender, Wire.Read<RemoteSignal>(data)); }
                                catch (Exception ex) { PlaybackDiagnostics.Record("remote-signal-rejected", new { error = ex.GetType().Name }); }
                                break;
                            case "error":
                                refused = true;
                                SetStatus(message.Text ?? "The room server refused the phone remote.", false);
                                break;
                            // This PC's id connected again from elsewhere: another BlackPearl with this library, such as a
                            // copy of it on another PC. Taking the id straight back would have the two replace each
                            // other every second, so this one waits.
                            case "closed":
                                replaced = true;
                                SetStatus("Another BlackPearl with this library is using the phone remote. This one tries again in a minute.", false);
                                break;
                        }
                    }
                }
                finally { connection.Cancel(); try { await writer; } catch { } }
                if (!refused && !replaced) SetStatus("Reconnecting to the room server", false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidDataException or HttpRequestException)
            {
                // A room server from before the phone remote serves rooms but not /remote.
                if (await ServesRoomsOnly(ct)) { refused = true; SetStatus("Your room server doesn't support the phone remote yet. Whoever runs it needs to update it.", false); }
                else SetStatus("Can't reach the room server. Phones can't connect until it's back.", false);
            }
            catch (ArgumentException ex) { SetStatus(ex.Message, false); refused = true; }
            try { await Task.Delay(refused ? TimeSpan.FromMinutes(2) : replaced ? options.ReplacedWait : delay, ct); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    private static readonly HttpClient health = new() { Timeout = TimeSpan.FromSeconds(10) };
    private async Task<bool> ServesRoomsOnly(CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await health.GetStringAsync(RoomAddress.ValidateServer(options.Server) + "/health", ct));
            // Any other server answers with whatever it likes, such as "ok"; only a room server's object counts.
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("protocol", out _) && !json.RootElement.TryGetProperty("remote", out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or ArgumentException or InvalidOperationException or UriFormatException) { return false; }
    }

    private static async Task WriteAsync(ClientWebSocket socket, Channel<WireMessage> queue, CancellationToken ct)
    {
        try { await foreach (var message in queue.Reader.ReadAllAsync(ct)) await SocketMessages.SendAsync(socket, message, ct); }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private void Signal(PhoneRemoteSession session, string kind, string payload)
    {
        var auth = PhoneRemoteProtocol.Sign(session.Key, "pc", session.SignalAs, session.Session, kind, payload);
        outgoing.Writer.TryWrite(new("signal", Target: session.SignalingPeer,
            Data: Wire.Serialize(new RemoteSignal(session.SignalAs, session.Session, kind, payload, auth))));
    }

    private void Reject(string peer, RemoteSignal signal, string reason)
    {
        lock (gate) { if (rejected.Count > 1000) rejected.Clear(); if (!rejected.Add(signal.Session)) return; }
        outgoing.Writer.TryWrite(new("signal", Target: peer, Data: Wire.Serialize(new RemoteSignal(signal.Phone, signal.Session, "rejected", reason, ""))));
    }

    internal void HandleSignal(string peer, RemoteSignal signal)
    {
        // Missing fields arrive as null.
        if (signal.Phone is not { Length: > 0 and <= 64 } || signal.Kind is null || signal.Payload is not { Length: <= 60000 } || !PhoneRemoteProtocol.IsHex(signal.Session, 32)) return;
        string key, sessionKey;
        lock (gate)
        {
            // A session begun with a pairing code keeps signing with it after pairing, under its new phone id.
            if (signal.Phone == "pair" && sessions.Values.FirstOrDefault(s => s.SignalAs == "pair" && s.Session == signal.Session) is { } pairing)
            { key = pairing.Key; sessionKey = pairing.PhoneId == "pair" ? "pair:" + signal.Session : pairing.PhoneId; }
            else if (signal.Phone == "pair")
            { key = pairingCode is not null && DateTimeOffset.UtcNow < pairingExpires ? pairingCode : ""; sessionKey = "pair:" + signal.Session; }
            else { key = phones.FirstOrDefault(p => p.Id == signal.Phone)?.Secret ?? ""; sessionKey = signal.Phone; }
        }
        if (key.Length == 0)
        {
            // Only an answer to a well-formed request: it is unsigned and grants nothing.
            if (signal.Kind == "sdp") Reject(peer, signal, signal.Phone == "pair" ? "pairing-expired" : "unknown-phone");
            return;
        }
        if (!PhoneRemoteProtocol.Verify(key, "phone", signal)) return;
        PhoneRemoteSession? session, replaced = null;
        lock (gate)
        {
            sessions.TryGetValue(sessionKey, out session);
            if (session is not null && session.Session != signal.Session)
            {
                if (signal.Kind != "sdp") return;
                replaced = session; session = null; sessions.Remove(sessionKey);
            }
            else if (session is not null) session.SignalingPeer = peer;
        }
        if (replaced is not null) { Close(replaced); if (replaced.PhoneId != "pair") Disconnected?.Invoke(replaced); }
        if (signal.Kind == "sdp")
        {
            var sdp = Wire.Read<RemoteSdp>(signal.Payload);
            if (sdp.Type != "offer" || sdp.Sdp is not { Length: > 0 } || session is not null) return;
            session = Open(sessionKey, signal.Phone, signal.Session, peer, key);
            if (session is null) return;
            // A signed offer can still be one the native library can't read.
            try { session.Peer.SetRemoteDescription(new RtcDescription { Type = RtcDescriptionType.Offer, Sdp = sdp.Sdp }); }
            catch (Exception) { Drop(sessionKey, session); throw; }
        }
        else if (signal.Kind == "ice" && session is not null)
        {
            var ice = Wire.Read<RemoteIce>(signal.Payload);
            if (ice.Candidate is not { Length: > 0 }) return;
            var line = ice.Candidate.StartsWith("a=", StringComparison.Ordinal) ? ice.Candidate : "a=" + ice.Candidate;
            try { session.Peer.AddRemoteCandidate(new RtcCandidate { Content = line, Mid = ice.Mid ?? "0" }); }
            catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("DataChannelDotnet") == true) { }
        }
    }

    private PhoneRemoteSession? Open(string sessionKey, string phoneId, string sessionId, string peer, string key)
    {
        string[] servers; bool relay;
        lock (gate)
        {
            if (sessions.Count >= 16) return null;
            servers = iceServers; relay = forceRelay;
        }
        var connection = new RtcPeerConnection(new RtcPeerConfiguration
        {
            IceServers = servers, MaxMessageSize = 65536, Mtu = 1200,
            TransportPolicy = relay ? rtcTransportPolicy.RTC_TRANSPORT_POLICY_RELAY : rtcTransportPolicy.RTC_TRANSPORT_POLICY_ALL
        });
        var session = new PhoneRemoteSession(phoneId, sessionId, peer, key, connection);
        lock (gate) sessions[sessionKey] = session;
        connection.OnLocalDescriptionSafe += (_, description) =>
            Signal(session, "sdp", Wire.Serialize(new RemoteSdp(description.Type == RtcDescriptionType.Answer ? "answer" : "offer", description.Sdp)));
        connection.OnCandidateSafe += (_, candidate) =>
            Signal(session, "ice", Wire.Serialize(new RemoteIce(candidate.Content.StartsWith("a=", StringComparison.Ordinal) ? candidate.Content[2..] : candidate.Content, candidate.Mid)));
        connection.OnConnectionStateChange += (_, state) =>
        {
            if (state == rtcState.RTC_CONNECTED && connection.TryGetSelectedCandidatePair(out var pair))
            {
                session.Routed = true;
                session.Relayed = pair.LocalCandidate?.Contains("typ relay") == true || pair.RemoteCandidate?.Contains("typ relay") == true;
            }
            if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED or rtcState.RTC_DISCONNECTED) Drop(sessionKey, session);
            Changed?.Invoke();
        };
        connection.OnDataChannel += (_, channel) => Attach(sessionKey, session, channel);
        PlaybackDiagnostics.Record("remote-session-opened", new { pairing = phoneId == "pair" });
        return session;
    }

    private void Attach(string sessionKey, PhoneRemoteSession session, IRtcDataChannel channel)
    {
        if (channel.Label == PhoneRemoteProtocol.MediaLabel && session.Media is null)
        {
            var media = session.Media = new PhoneMediaServer(session, channel, (s, resource, ct) =>
                s.Greeted && s.PhoneId != "pair" && ResolveMedia is { } resolve ? resolve(s, resource, ct) : Task.FromResult<PhoneResource?>(null));
            // The media channel can close on its own; a new one then takes its place.
            channel.OnClose += _ => { if (session.Media == media) { session.Media = null; media.Dispose(); } };
            return;
        }
        if (channel.Label != PhoneRemoteProtocol.Label || session.Channel is not null) { try { channel.Dispose(); } catch { } return; }
        session.Channel = channel;
        channel.OnClose += _ => Drop(sessionKey, session);
        channel.OnTextReceivedSafe += (_, text) =>
        {
            try
            {
                if (Encoding.UTF8.GetByteCount(text.Text) > 65536) return;
                using var json = JsonDocument.Parse(text.Text);
                Receive(sessionKey, session, json.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { PlaybackDiagnostics.Record("remote-message-rejected", new { error = ex.GetType().Name }); }
        };
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void Receive(string sessionKey, PhoneRemoteSession session, JsonElement message)
    {
        switch (Text(message, "type"))
        {
            case "hello":
                var protocol = message.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var number) ? number : 0;
                if (protocol != PhoneRemoteProtocol.Version)
                {
                    Send(session, Wire.Serialize(new { type = "error", code = "protocol", message = protocol > PhoneRemoteProtocol.Version
                        ? "This phone app is newer than BlackPearl on the PC. Update BlackPearl on the PC." : "Update BlackPearl Mobile to control this PC." }));
                    Close(session); return;
                }
                session.Device = Text(message, "device") is { Length: > 0 and <= 80 } device ? device : null;
                Send(session, Wire.Serialize(new { type = "welcome", protocol = PhoneRemoteProtocol.Version, app = options.AppVersion, pc = options.PcName, pcId = PcId, canStart = options.CanStart, features = options.Features }));
                session.Greeted = true;
                if (session.PhoneId != "pair") { Seen(session.PhoneId); SendState(session); }
                Changed?.Invoke();
                break;
            case "pair" when session.Greeted:
                PairedPhone paired;
                lock (gate)
                {
                    if (session.PhoneId != "pair" || pairingCode != session.Key || DateTimeOffset.UtcNow >= pairingExpires)
                    {
                        Send(session, Wire.Serialize(new { type = "error", code = "pairing", message = "That pairing code has expired. Show a new one on the PC." }));
                        return;
                    }
                    var name = Text(message, "name")?.Trim() is { Length: > 0 } given ? given[..Math.Min(given.Length, 60)] : session.Device ?? "Phone";
                    paired = new(PhoneRemoteProtocol.NewHex(16), name, PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
                    phones.Add(paired);
                    options.Store.Save(phones);
                    pairingCode = null;
                    session.PhoneId = paired.Id;
                    sessions.Remove(sessionKey);
                    if (sessions.Remove(paired.Id, out var stale)) Close(stale);
                    sessions[paired.Id] = session;
                }
                Send(session, Wire.Serialize(new { type = "paired", phoneId = paired.Id, secret = paired.Secret, pc = options.PcName, pcId = PcId }));
                Paired?.Invoke(paired);
                SendState(session);
                Changed?.Invoke();
                break;
            case "command" when session.Greeted && session.PhoneId != "pair":
                var action = Text(message, "action");
                if (action is null or { Length: > 40 }) return;
                JsonElement? value = message.TryGetProperty("value", out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.Clone() : null;
                Command?.Invoke(session, new RemoteCommand(action, value));
                break;
            case { Length: > 0 and <= 40 } type when session.Greeted && session.PhoneId != "pair" && type is not ("hello" or "pair" or "command"):
                Request?.Invoke(session, type, message.Clone());
                break;
        }
    }

    private void SendState(PhoneRemoteSession session)
    {
        if (CurrentState?.Invoke() is { } state) Send(session, Wire.Serialize(state));
    }

    private void Seen(string phoneId)
    {
        lock (gate)
        {
            var index = phones.FindIndex(p => p.Id == phoneId);
            if (index < 0) return;
            phones[index] = phones[index] with { LastSeen = Wire.Now };
            options.Store.Save(phones);
        }
    }

    private void Drop(string sessionKey, PhoneRemoteSession session)
    {
        bool removed;
        lock (gate)
        {
            removed = sessions.TryGetValue(session.PhoneId == "pair" ? sessionKey : session.PhoneId, out var current) && current == session;
            if (removed) sessions.Remove(session.PhoneId == "pair" ? sessionKey : session.PhoneId);
        }
        if (removed)
        {
            Close(session);
            if (session.PhoneId != "pair") Disconnected?.Invoke(session);
            Changed?.Invoke();
        }
    }

    // Native callbacks can run while their connection is closing; close on the thread pool.
    private static void Close(PhoneRemoteSession session) => _ = Task.Run(() =>
    {
        session.Media?.Dispose();
        try { session.Channel?.Dispose(); } catch { }
        try { session.Peer.Dispose(); } catch { }
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        PhoneRemoteSession[] open;
        lock (gate) { open = sessions.Values.ToArray(); sessions.Clear(); }
        foreach (var session in open) Close(session);
    }
}
