using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Watchroom.Core;

public sealed class RoomClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<WireMessage[]> outgoing = Channel.CreateBounded<WireMessage[]>(128);
    private readonly ConcurrentDictionary<string, PeerTransport> peers = new();
    private Task? readTask, sendTask;
    public Welcome? Identity { get; private set; }
    public RoomSnapshot? Snapshot { get; private set; }
    public IMediaSource? HostedMedia { get; set; }
    private readonly ConcurrentDictionary<string, IMediaSource> assets = new();
    public void SetHostedFile(string path, string title)
    {
        ApplyHostedFile(PrepareHostedFile(path, title));
    }
    public static HostedFileSelection PrepareHostedFile(string path, string title)
    {
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
        var preparedAssets = new Dictionary<string, IMediaSource>();
        var file = new FileMediaSource(path, title);
        if (file.Media.Length <= 0) throw new InvalidDataException("Video file is empty");
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var sidecar in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!))
        {
            var name = Path.GetFileNameWithoutExtension(sidecar);
            if (!(name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))) continue;
            if (!new[] { ".srt", ".ass", ".ssa" }.Contains(Path.GetExtension(sidecar).ToLowerInvariant()) || new FileInfo(sidecar).Length is <= 0 or > 8388608 || preparedAssets.Count >= 12) continue;
            var asset = new FileMediaSource(sidecar, Path.GetFileName(sidecar)); preparedAssets[asset.Media.Id] = asset;
        }
        return new(new MediaWithSubtitles(file, preparedAssets.Values.Select(x => x.Media).ToArray()), preparedAssets);
    }
    public void ApplyHostedFile(HostedFileSelection selection)
    {
        assets.Clear(); foreach (var pair in selection.Assets) assets[pair.Key] = pair.Value;
        HostedMedia = selection.Media;
    }
    public long ServerOffsetMs { get; private set; }
    private long bestRtt = long.MaxValue;
    public event Action<WireMessage>? Message;
    public event Action<string>? Status;
    public event Action<string>? LibraryPeerReady;
    public event Action<string, WireMessage>? LibraryMessage;
    public void SendLibrary(string peerId, WireMessage message)
    {
        if (Snapshot?.People.Any(p => p.Id == peerId && p.Approved) != true ||
            !peers.TryGetValue(peerId, out var peer)) throw new IOException("Guest is no longer connected");
        peer.SendControl(message);
    }
    public async Task ConnectAsync(string server, string name, string? invitation = null)
    {
        var uri = new Uri(server.TrimEnd('/') + "/room");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidOperationException("Use an HTTPS server address. HTTP is allowed only on localhost for development.");
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "https" ? "wss" : "ws" };
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(builder.Uri, lifetime.Token);
        sendTask = SendLoop(); readTask = ReadLoop();
        Send(new(invitation is null ? "create" : "join", Text: name, Data: invitation));
        _ = PingLoop();
    }
    public void Send(WireMessage message)
    {
        if (!outgoing.Writer.TryWrite([message])) throw new IOException("Room connection is busy or closed");
    }
    public void PublishHostedFile(HostedFileSelection selection, bool play)
    {
        WireMessage[] batch = [new("media", Data: Wire.Serialize(selection.Media.Media)),
            new("playback", Data: Wire.Serialize(new PlaybackState(0, selection.Media.Media.Id, play, 0, 0)))];
        if (!outgoing.Writer.TryWrite(batch)) throw new IOException("Room connection is busy or closed");
        ApplyHostedFile(selection);
    }
    private async Task SendLoop()
    {
        try { await foreach (var batch in outgoing.Reader.ReadAllAsync(lifetime.Token)) foreach (var msg in batch) await SocketMessages.SendAsync(socket, msg, lifetime.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { lifetime.Cancel(); }
    }
    private async Task PingLoop()
    {
        try { while (!lifetime.IsCancellationRequested) { Send(new("ping", Number: Wire.Now)); await Task.Delay(2000, lifetime.Token); } } catch (Exception) { }
    }
    private async Task ReadLoop()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await SocketMessages.ReadAsync(socket, lifetime.Token); if (message is null) break;
                switch (message.Type)
                {
                    case "welcome": case "admitted": Identity = Wire.Read<Welcome>(message.Data!); break;
                    case "snapshot": Snapshot = Wire.Read<RoomSnapshot>(message.Data!); break;
                    case "connect": CreatePeer(message.Target!, true); break;
                    case "signal":
                        var peer = peers.TryGetValue(message.Sender!, out var existing) ? existing : CreatePeer(message.Sender!, false);
                        if (message.Text == "library-capability")
                        {
                            if (message.Data == "1" && Identity?.Host == true) peer.EnableLibraryChannel();
                        }
                        else peer.ReceiveSignal(message.Text!, message.Data!);
                        break;
                    case "peer-left": if (peers.TryRemove(message.Sender!, out var left)) left.Dispose(); break;
                    case "pong":
                        var rtt = Wire.Now - message.Number;
                        if (rtt < bestRtt && long.TryParse(message.Data, out var serverNow)) { bestRtt = rtt; ServerOffsetMs = serverNow - (message.Number + rtt / 2); }
                        break;
                }
                Message?.Invoke(message);
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested) { Status?.Invoke("Disconnected: " + ex.Message); }
        finally { lifetime.Cancel(); foreach (var peer in peers.Values) peer.Dispose(); peers.Clear(); Status?.Invoke("Disconnected — playback paused. Rejoin to reconnect."); }
    }
    private PeerTransport CreatePeer(string id, bool offer)
    {
        if (Identity is null) throw new InvalidOperationException("Missing room identity");
        var peer = new PeerTransport(Identity.IceServers, Identity.ForceRelay);
        peer.ResolveMedia = media => HostedMedia?.Media.Id == media ? HostedMedia : assets.GetValueOrDefault(media);
        peer.Signal += (type, data) => { try { Send(new("signal", Target: id, Text: type, Data: data)); } catch (IOException) { } };
        peer.Status += text => Status?.Invoke(text);
        peers[id] = peer;
        peer.ControlMessage += message =>
        {
            if (Snapshot?.People.Any(p => p.Id == id && p.Approved) == true)
                LibraryMessage?.Invoke(id, message with { Sender = id });
        };
        _ = NotifyLibraryReady(id, peer);
        // Old clients ignore unknown signal types. Do not give them an extra channel
        // until both ends advertise support (their old receiver assumes one channel).
        Send(new("signal", Target: id, Text: "library-capability", Data: "1"));
        if (offer) peer.StartOffer(false);
        return peer;
    }
    private async Task NotifyLibraryReady(string id, PeerTransport peer)
    {
        try { await peer.ControlReady.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token); LibraryPeerReady?.Invoke(id); }
        catch (Exception) { /* Older peers may not support the library channel. */ }
    }
    public async Task<IMediaSource> GetSourceAsync(SharedMedia media, CancellationToken ct)
    {
        if (Identity?.Host == true) return HostedMedia?.Media.Id == media.Id ? HostedMedia : assets.GetValueOrDefault(media.Id) ?? throw new InvalidOperationException("No shared media selected");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        while (peers.IsEmpty) await Task.Delay(100, deadline.Token);
        var peer = peers.Values.First(); await peer.Ready.WaitAsync(deadline.Token);
        return new RemoteMediaSource(peer, media);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); outgoing.Writer.TryComplete(); socket.Abort();
        if (readTask is not null) { try { await readTask; } catch { } }
        if (sendTask is not null) { try { await sendTask; } catch { } }
        foreach (var peer in peers.Values) peer.Dispose(); peers.Clear(); socket.Dispose();
    }
}

public record HostedFileSelection(IMediaSource Media, IReadOnlyDictionary<string, IMediaSource> Assets);

internal sealed class MediaWithSubtitles(IMediaSource source, SharedMedia[] subtitles) : IMediaSource
{
    public SharedMedia Media { get; } = source.Media with { Subtitles = subtitles };
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => source.ReadAsync(offset, count, ct);
}
