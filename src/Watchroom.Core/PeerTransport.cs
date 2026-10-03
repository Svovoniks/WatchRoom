using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Buffers.Binary;
using DataChannelDotnet;
using DataChannelDotnet.Data;
using DataChannelDotnet.Impl;
using DataChannelDotnet.Bindings;

namespace Watchroom.Core;

public interface IMediaSource
{
    SharedMedia Media { get; }
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct);
}

public interface ILocalMediaSource : IMediaSource
{
    string LocalPath { get; }
}

public sealed class FileMediaSource : ILocalMediaSource
{
    private readonly string path;
    public string LocalPath => path;
    public SharedMedia Media { get; }
    public FileMediaSource(string path, string? title = null, string? id = null)
    {
        this.path = Path.GetFullPath(path);
        Media = new(id ?? Guid.NewGuid().ToString("N"), title ?? Path.GetFileNameWithoutExtension(path), new FileInfo(path).Length, Path.GetExtension(path));
    }
    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || offset >= Media.Length || count is < 1 or > 262144) throw new InvalidDataException("Invalid media range");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var result = new byte[(int)Math.Min(count, Media.Length - offset)];
        int read = 0;
        while (read < result.Length)
        {
            int n = await RandomAccess.ReadAsync(handle, result.AsMemory(read), offset + read, ct);
            if (n == 0) throw new EndOfStreamException("Source file changed while streaming");
            read += n;
        }
        return result;
    }
}

internal record RangeRequest(string Id, string Media, long Offset, int Count, bool Binary = false);
internal record RangeReply(string Id, byte[]? Bytes, string? Error = null);

public sealed class PeerTransport : IDisposable
{
    public string DiagnosticId { get; } = Guid.NewGuid().ToString("N")[..8];
    private readonly RtcPeerConnection peer;
    private readonly RtcPeerConfiguration configuration;
    private RtcPeerConnection? mediaPeer;
    private IRtcDataChannel? legacyMedia;
    private readonly object mediaGate = new();
    private readonly TaskCompletionSource isolatedMediaOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool offering;
    private volatile bool isolateMedia;
    private bool isolatedChannelAttached;
    public bool UsesIsolatedMedia => isolateMedia && isolatedMediaOpened.Task.IsCompletedSuccessfully;
    private IRtcDataChannel? channel;
    private IRtcDataChannel? controls;
    private IRtcDataChannel? catalogChannel;
    private readonly TaskCompletionSource catalogOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task LibraryReady => Task.WhenAll(ControlReady, catalogOpened.Task);
    public void EnableLibraryChannel()
    {
        if (catalogChannel is null) Attach(peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "room-catalog-v1" }));
    }
    private sealed class PendingRange(int count)
    {
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] bytes = new byte[count];
        private readonly HashSet<int> received = [];
        private int length;
        public void Frame(int offset, int total, ReadOnlySpan<byte> data)
        {
            lock (received)
            {
                if (total != bytes.Length || offset < 0 || offset % MediaStreaming.FrameBytes != 0 || data.Length != Math.Min(MediaStreaming.FrameBytes, total - offset) || offset > total - data.Length)
                    throw new InvalidDataException("Invalid media frame");
                if (!received.Add(offset)) return;
                data.CopyTo(bytes.AsSpan(offset)); length += data.Length;
                if (length == total) Completion.TrySetResult(bytes);
            }
        }
    }
    private readonly ConcurrentDictionary<string, PendingRange> pending = new();
    private readonly ConcurrentDictionary<string, byte> serving = new();
    private readonly SemaphoreSlim window = new(MediaStreaming.Window);
    // Canceled HTTP seeks can leave earlier ranges queued at the host. Leave
    // bounded room for those while the guest starts its next read window.
    private readonly Channel<RangeRequest> requests = Channel.CreateBounded<RangeRequest>(MediaStreaming.Window * 4);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource controlOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int controlClosed;
    private int disposed;
    public Func<string, IMediaSource?>? ResolveMedia { get; set; }
    public event Action<string, string>? Signal;
    public event Action<string>? Status;
    public event Action<WireMessage>? ControlMessage;
    public event Action? ControlClosed;
    public Task Ready => opened.Task;
    public Task ControlReady => controlOpened.Task;
    public bool IsControlOpen => controls?.IsOpen == true && disposed == 0;

    public PeerTransport(string[] iceServers, bool relayOnly = false)
    {
        PlaybackDiagnostics.Record("transport-created", new { transport = DiagnosticId, iceServers = iceServers.Length, relayOnly });
        configuration = new RtcPeerConfiguration
        {
            IceServers = iceServers, MaxMessageSize = 65536, Mtu = 1200,
            TransportPolicy = relayOnly ? rtcTransportPolicy.RTC_TRANSPORT_POLICY_RELAY : rtcTransportPolicy.RTC_TRANSPORT_POLICY_ALL
        };
        try { peer = new RtcPeerConnection(configuration); }
        catch (DllNotFoundException ex) when (OperatingSystem.IsWindows()) { throw new IOException("The Windows media transport or its Visual C++ runtime is missing. Republish using build.ps1 -Publish or reinstall the complete Watchroom package.", ex); }
        peer.OnLocalDescriptionSafe += (_, d) => { PlaybackDiagnostics.Record("signal-local", new { transport = DiagnosticId, type = "sdp" }); Signal?.Invoke("sdp", Wire.Serialize(d)); };
        peer.OnCandidateSafe += (_, c) => { PlaybackDiagnostics.Record("signal-local", new { transport = DiagnosticId, type = "ice" }); Signal?.Invoke("ice", Wire.Serialize(c)); };
        peer.OnDataChannel += (_, c) => Attach(c);
        peer.OnConnectionStateChange += (_, s) =>
        {
            PlaybackDiagnostics.Record("transport-state", new { transport = DiagnosticId, state = s.ToString(), pendingRanges = pending.Count });
            if (s == rtcState.RTC_CONNECTED && peer.TryGetSelectedCandidatePair(out var pair))
            {
                PlaybackDiagnostics.Record("transport-route", new { transport = DiagnosticId, relay = pair.LocalCandidate?.Contains("typ relay") == true || pair.RemoteCandidate?.Contains("typ relay") == true });
                Status?.Invoke(pair.LocalCandidate?.Contains("typ relay") == true || pair.RemoteCandidate?.Contains("typ relay") == true ? "Connected through relay" : "Connected directly");
            }
            else Status?.Invoke(s.ToString().Replace("RTC_", ""));
            if (s is rtcState.RTC_FAILED or rtcState.RTC_CLOSED or rtcState.RTC_DISCONNECTED)
            {
                var error = new IOException("Peer connection interrupted");
                opened.TrySetException(error);
                controlOpened.TrySetException(error);
                CloseControls();
                foreach (var p in pending.Values) p.Completion.TrySetException(error);
            }
        };
        _ = ServeAsync();
    }
    public void StartOffer()
    {
        offering = true;
        // Reliable, ordered commands have their own SCTP stream and never enter
        // the media range request queue.
        Attach(peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "watchroom-control-v2" }));
        // Ranges are identified independently. Reliable unordered delivery avoids
        // holding later bytes behind retransmission of an earlier range.
        Attach(peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "media", Unordered = true }));
    }
    private void IsolateMedia()
    {
        lock (mediaGate)
        {
            if (mediaPeer is not null) return;
            isolateMedia = true;
            mediaPeer = new RtcPeerConnection(configuration);
            mediaPeer.OnLocalDescriptionSafe += (_, description) => controls!.Send(Wire.Serialize(new WireMessage("media-signal", Text: "sdp", Data: Wire.Serialize(description))));
            mediaPeer.OnCandidateSafe += (_, candidate) => controls!.Send(Wire.Serialize(new WireMessage("media-signal", Text: "ice", Data: Wire.Serialize(candidate))));
            mediaPeer.OnDataChannel += (_, data) => Attach(data);
            mediaPeer.OnConnectionStateChange += (_, state) =>
            {
                PlaybackDiagnostics.Record("media-transport-state", new { transport = DiagnosticId, state = state.ToString() });
                if (state == rtcState.RTC_CONNECTED && mediaPeer.TryGetSelectedCandidatePair(out var route))
                    PlaybackDiagnostics.Record("media-transport-route", new { transport = DiagnosticId, relay = route.LocalCandidate?.Contains("typ relay") == true || route.RemoteCandidate?.Contains("typ relay") == true });
                if (state is rtcState.RTC_FAILED or rtcState.RTC_CLOSED or rtcState.RTC_DISCONNECTED)
                {
                    var error = new IOException("Media connection interrupted");
                    isolatedMediaOpened.TrySetException(error);
                    foreach (var range in pending.Values) range.Completion.TrySetException(error);
                }
            };
            if (offering) Attach(mediaPeer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "media-v2", Unordered = true }));
            PlaybackDiagnostics.Record("media-transport-isolated", new { transport = DiagnosticId });
        }
    }
    public void ReceiveSignal(string type, string data)
    {
        PlaybackDiagnostics.Record("signal-received", new { transport = DiagnosticId, type });
        if (type == "sdp") peer.SetRemoteDescription(Wire.Read<RtcDescription>(data));
        else if (type == "ice") peer.AddRemoteCandidate(Wire.Read<RtcCandidate>(data));
    }
    private void Attach(IRtcDataChannel data)
    {
        PlaybackDiagnostics.Record("channel-created", new { transport = DiagnosticId, label = data.Label });
        data.OnOpen += _ => PlaybackDiagnostics.Record("channel-open", new { transport = DiagnosticId, label = data.Label });
        data.OnClose += _ => PlaybackDiagnostics.Record("channel-closed", new { transport = DiagnosticId, label = data.Label });
        if (data.Label == "room-catalog-v1")
        {
            if (catalogChannel is not null) { data.Dispose(); return; }
            catalogChannel = data;
            data.OnOpen += _ => catalogOpened.TrySetResult();
            if (data.IsOpen) catalogOpened.TrySetResult();
            data.OnTextReceivedSafe += (_, text) =>
            {
                try { if (System.Text.Encoding.UTF8.GetByteCount(text.Text) <= 60000) ControlMessage?.Invoke(Wire.Read<WireMessage>(text.Text)); }
                catch (Exception ex) { Status?.Invoke("Invalid library message: " + ex.GetType().Name); }
            };
            return;
        }
        if (data.Label == "watchroom-control-v2") { AttachControls(data); return; }
        Action markOpen;
        if (data.Label == "media-v2")
        {
            if (isolatedChannelAttached) { data.Dispose(); return; }
            isolatedChannelAttached = true;
            legacyMedia = channel; channel = data;
            markOpen = () => { isolatedMediaOpened.TrySetResult(); opened.TrySetResult(); };
        }
        else
        {
            if (data.Label != "media" || channel is not null) { data.Dispose(); return; }
            channel = data;
            markOpen = () => opened.TrySetResult();
        }
        data.OnClose += _ => { foreach (var p in pending.Values) p.Completion.TrySetException(new IOException("Media channel closed")); };
        data.OnBinaryReceivedSafe += (_, frame) =>
        {
            try
            {
                if (frame.Data.Length is < 25 or > MediaStreaming.FrameBytes + 24) throw new InvalidDataException("Invalid media frame size");
                var id = new Guid(frame.Data[..16]).ToString("N");
                if (pending.TryGetValue(id, out var range)) range.Frame(BinaryPrimitives.ReadInt32LittleEndian(frame.Data[16..]), BinaryPrimitives.ReadInt32LittleEndian(frame.Data[20..]), frame.Data[24..]);
            }
            catch (Exception ex) { PlaybackDiagnostics.Record("media-frame-rejected", new { transport = DiagnosticId, error = ex.GetType().Name }); }
        };
        data.OnTextReceivedSafe += (sender, text) =>
        {
            try
            {
                if (text.Text.Length > 65536) return;
                var message = Wire.Read<WireMessage>(text.Text);
                if (message.Type == "read")
                {
                    var req = Wire.Read<RangeRequest>(message.Data!);
                    if (req.Count is < 1 or > 32768 || req.Offset < 0 || !Guid.TryParseExact(req.Id, "N", out _)) throw new InvalidDataException("Invalid media range");
                    if (!serving.TryAdd(req.Id, 0)) return;
                    if (!requests.Writer.TryWrite(req)) { serving.TryRemove(req.Id, out _); SendReply(new(req.Id, null, "Too many requests")); }
                }
                else if (message.Type == "cancel-read") serving.TryRemove(message.Data ?? "", out _);
                else if (message.Type == "bytes")
                {
                    var reply = Wire.Read<RangeReply>(message.Data!);
                    if (pending.TryGetValue(reply.Id, out var waiter))
                    {
                        if (reply.Error is not null) waiter.Completion.TrySetException(new IOException(reply.Error));
                        else waiter.Completion.TrySetResult(reply.Bytes ?? []);
                    }
                }
            }
            catch (Exception ex) { Status?.Invoke("Invalid peer message: " + ex.GetType().Name); }
        };
        // A remote channel can already be open when attached. Install receive
        // callbacks before releasing readers or they can lose their first reply.
        data.OnOpen += _ => markOpen();
        if (data.IsOpen) markOpen();
    }
    private void AttachControls(IRtcDataChannel data)
    {
        if (controls is not null) { data.Dispose(); return; }
        controls = data;
        void ControlOpen()
        {
            data.Send(Wire.Serialize(new WireMessage("media-upgrade", Number: 2)));
            controlOpened.TrySetResult();
        }
        data.OnClose += _ => CloseControls();
        data.OnTextReceivedSafe += (_, text) =>
        {
            try
            {
                if (text.Text.Length > 65536) { PlaybackDiagnostics.Record("control-rejected", new { transport = DiagnosticId, reason = "oversized" }); CloseControls(); return; }
                var message = Wire.Read<WireMessage>(text.Text);
                if (message.Type == "media-upgrade" && message.Number == 2) { IsolateMedia(); return; }
                if (message.Type == "media-signal")
                {
                    IsolateMedia();
                    if (message.Text == "sdp") mediaPeer!.SetRemoteDescription(Wire.Read<RtcDescription>(message.Data!));
                    else if (message.Text == "ice") mediaPeer!.AddRemoteCandidate(Wire.Read<RtcCandidate>(message.Data!));
                    return;
                }
                ControlMessage?.Invoke(message);
            }
            catch (Exception ex) { PlaybackDiagnostics.Record("control-rejected", new { transport = DiagnosticId, error = ex.GetType().Name }); Status?.Invoke("Invalid control message: " + ex.GetType().Name); CloseControls(); }
        };
        data.OnOpen += _ => ControlOpen();
        if (data.IsOpen) ControlOpen();
    }
    private void CloseControls()
    {
        controlOpened.TrySetException(new IOException("Room control channel closed"));
        if (disposed == 0 && Interlocked.Exchange(ref controlClosed, 1) == 0) ControlClosed?.Invoke();
    }
    public void SendControl(WireMessage message)
    {
        if (!IsControlOpen) throw new IOException("Room controls are not connected. Rejoin the room.");
        var text = Wire.Serialize(message);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536) throw new InvalidDataException("Room control message is too large");
        if (message.Type is "library-item" or "library-page-end")
        {
            if (catalogChannel?.IsOpen != true) throw new IOException("Guest library is not connected");
            catalogChannel.Send(text);
        }
        else controls!.Send(text);
    }
    private void SendReply(RangeReply reply) => channel?.Send(Wire.Serialize(new WireMessage("bytes", Data: Wire.Serialize(reply))));
    private async Task ServeAsync()
    {
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(lifetime.Token))
            {
                if (!serving.ContainsKey(request.Id)) continue;
                try
                {
                    if (request.Count is < 1 or > 32768) throw new InvalidDataException();
                    var source = ResolveMedia?.Invoke(request.Media) ?? throw new UnauthorizedAccessException();
                    var bytes = await source.ReadAsync(request.Offset, request.Count, lifetime.Token);
                    if (bytes.Length != request.Count) throw new IOException("Incomplete source range");
                    if (request.Binary)
                    {
                        var frame = new byte[MediaStreaming.FrameBytes + 24];
                        Guid.ParseExact(request.Id, "N").TryWriteBytes(frame);
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20), bytes.Length);
                        for (var offset = 0; offset < bytes.Length && serving.ContainsKey(request.Id); offset += MediaStreaming.FrameBytes)
                        {
                            var count = Math.Min(MediaStreaming.FrameBytes, bytes.Length - offset);
                            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(16), offset);
                            bytes.AsSpan(offset, count).CopyTo(frame.AsSpan(24));
                            channel!.Send(frame.AsSpan(0, count + 24));
                        }
                    }
                    else SendReply(new(request.Id, bytes));
                }
                catch (Exception ex) when (!lifetime.IsCancellationRequested)
                {
                    PlaybackDiagnostics.Record("host-range-failed", new { transport = DiagnosticId, offset = request.Offset, count = request.Count, error = ex.GetType().Name });
                    try { SendReply(new(request.Id, null, "Media unavailable or not authorized")); } catch { }
                }
                finally { serving.TryRemove(request.Id, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task<byte[]> ReadAsync(string media, long offset, int count, CancellationToken ct)
    {
        if (count is < 1 or > 32768 || offset < 0) throw new InvalidDataException("Invalid media range");
        await Ready.WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (isolateMedia) await isolatedMediaOpened.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await window.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        var range = new PendingRange(count);
        pending[id] = range;
        try
        {
            // Old hosts ignore this optional field and return their JSON reply.
            channel!.Send(Wire.Serialize(new WireMessage("read", Data: Wire.Serialize(new RangeRequest(id, media, offset, count, Binary: true)))));
            var bytes = await range.Completion.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            if (bytes.Length != count) throw new IOException("Incomplete peer range");
            return bytes;
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Record("peer-range-failed", new { transport = DiagnosticId, offset, count, error = ex.GetType().Name });
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _); window.Release();
            if (!range.Completion.Task.IsCompleted && channel?.IsOpen == true)
                try { channel.Send(Wire.Serialize(new WireMessage("cancel-read", Data: id))); } catch { }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); requests.Writer.TryComplete();
        foreach (var p in pending.Values) p.Completion.TrySetException(new IOException("Peer disposed"));
        opened.TrySetCanceled();
        controlOpened.TrySetCanceled();
        catalogOpened.TrySetCanceled();
        isolatedMediaOpened.TrySetCanceled();
        try { legacyMedia?.Dispose(); } catch { }
        try { mediaPeer?.Dispose(); } catch { }
        try { catalogChannel?.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Catalog cleanup: {0}", ex.Message); }
        // A remotely closed native channel can reject disposal. Cleanup of one
        // departed peer must still release its connection and preserve the room.
        try { channel?.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Channel cleanup: {0}", ex.Message); }
        try { controls?.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Control cleanup: {0}", ex.Message); }
        try { peer.Dispose(); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Peer cleanup: {0}", ex.Message); }
    }
}

public sealed class RemoteMediaSource(PeerTransport peer, SharedMedia media) : IMediaSource
{
    public SharedMedia Media => media;
    public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct) => peer.ReadAsync(media.Id, offset, count, ct);
}
