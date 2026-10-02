using System.Collections.Concurrent;
using System.Threading.Channels;
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

internal record RangeRequest(string Id, string Media, long Offset, int Count);
internal record RangeReply(string Id, byte[]? Bytes, string? Error = null);

public sealed class PeerTransport : IDisposable
{
    private readonly RtcPeerConnection peer;
    private IRtcDataChannel? channel;
    private IRtcDataChannel? controls;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> pending = new();
    private readonly SemaphoreSlim window = new(8);
    private readonly Channel<RangeRequest> requests = Channel.CreateBounded<RangeRequest>(32);
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
        try { peer = new RtcPeerConnection(new RtcPeerConfiguration
        {
            IceServers = iceServers, MaxMessageSize = 65536,
            TransportPolicy = relayOnly ? rtcTransportPolicy.RTC_TRANSPORT_POLICY_RELAY : rtcTransportPolicy.RTC_TRANSPORT_POLICY_ALL
        }); }
        catch (DllNotFoundException ex) when (OperatingSystem.IsWindows()) { throw new IOException("The Windows media transport or its Visual C++ runtime is missing. Republish using build.ps1 -Publish or reinstall the complete Watchroom package.", ex); }
        peer.OnLocalDescriptionSafe += (_, d) => Signal?.Invoke("sdp", Wire.Serialize(d));
        peer.OnCandidateSafe += (_, c) => Signal?.Invoke("ice", Wire.Serialize(c));
        peer.OnDataChannel += (_, c) => Attach(c);
        peer.OnConnectionStateChange += (_, s) =>
        {
            if (s == rtcState.RTC_CONNECTED && peer.TryGetSelectedCandidatePair(out var pair))
                Status?.Invoke(pair.LocalCandidate?.Contains("typ relay") == true || pair.RemoteCandidate?.Contains("typ relay") == true ? "Connected through relay" : "Connected directly");
            else Status?.Invoke(s.ToString().Replace("RTC_", ""));
            if (s is rtcState.RTC_FAILED or rtcState.RTC_CLOSED or rtcState.RTC_DISCONNECTED)
            {
                var error = new IOException("Peer connection interrupted");
                opened.TrySetException(error);
                controlOpened.TrySetException(error);
                CloseControls();
                foreach (var p in pending.Values) p.TrySetException(error);
            }
        };
        _ = ServeAsync();
    }
    public void StartOffer()
    {
        // Reliable, ordered commands have their own SCTP stream and never enter
        // the media range request queue.
        Attach(peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "watchroom-control-v2" }));
        Attach(peer.CreateDataChannel(new RtcCreateDataChannelArgs { Label = "media" }));
    }
    public void ReceiveSignal(string type, string data)
    {
        if (type == "sdp") peer.SetRemoteDescription(Wire.Read<RtcDescription>(data));
        else if (type == "ice") peer.AddRemoteCandidate(Wire.Read<RtcCandidate>(data));
    }
    private void Attach(IRtcDataChannel data)
    {
        if (data.Label == "watchroom-control-v2") { AttachControls(data); return; }
        if (data.Label != "media" || channel is not null) { data.Dispose(); return; }
        channel = data;
        data.OnOpen += _ => opened.TrySetResult();
        if (data.IsOpen) opened.TrySetResult();
        data.OnClose += _ => { foreach (var p in pending.Values) p.TrySetException(new IOException("Media channel closed")); };
        data.OnTextReceivedSafe += (_, text) =>
        {
            try
            {
                if (text.Text.Length > 65536) return;
                var message = Wire.Read<WireMessage>(text.Text);
                if (message.Type == "read")
                {
                    var req = Wire.Read<RangeRequest>(message.Data!);
                    if (!requests.Writer.TryWrite(req)) SendReply(new(req.Id, null, "Too many requests"));
                }
                else if (message.Type == "bytes")
                {
                    var reply = Wire.Read<RangeReply>(message.Data!);
                    if (pending.TryGetValue(reply.Id, out var waiter))
                    {
                        if (reply.Error is not null) waiter.TrySetException(new IOException(reply.Error));
                        else waiter.TrySetResult(reply.Bytes ?? []);
                    }
                }
            }
            catch (Exception ex) { Status?.Invoke("Invalid peer message: " + ex.GetType().Name); }
        };
    }
    private void AttachControls(IRtcDataChannel data)
    {
        if (controls is not null) { data.Dispose(); return; }
        controls = data;
        data.OnOpen += _ => controlOpened.TrySetResult();
        if (data.IsOpen) controlOpened.TrySetResult();
        data.OnClose += _ => CloseControls();
        data.OnTextReceivedSafe += (_, text) =>
        {
            try
            {
                if (text.Text.Length > 65536) { CloseControls(); return; }
                ControlMessage?.Invoke(Wire.Read<WireMessage>(text.Text));
            }
            catch (Exception ex) { Status?.Invoke("Invalid control message: " + ex.GetType().Name); CloseControls(); }
        };
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
        controls!.Send(text);
    }
    private void SendReply(RangeReply reply) => channel?.Send(Wire.Serialize(new WireMessage("bytes", Data: Wire.Serialize(reply))));
    private async Task ServeAsync()
    {
        try
        {
            await foreach (var request in requests.Reader.ReadAllAsync(lifetime.Token))
            {
                try
                {
                    if (request.Count is < 1 or > 32768) throw new InvalidDataException();
                    var source = ResolveMedia?.Invoke(request.Media) ?? throw new UnauthorizedAccessException();
                    var bytes = await source.ReadAsync(request.Offset, request.Count, lifetime.Token);
                    SendReply(new(request.Id, bytes));
                }
                catch (Exception) when (!lifetime.IsCancellationRequested) { try { SendReply(new(request.Id, null, "Media unavailable or not authorized")); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async Task<byte[]> ReadAsync(string media, long offset, int count, CancellationToken ct)
    {
        await Ready.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await window.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            channel!.Send(Wire.Serialize(new WireMessage("read", Data: Wire.Serialize(new RangeRequest(id, media, offset, count)))));
            var bytes = await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            if (bytes.Length != count) throw new IOException("Incomplete peer range");
            return bytes;
        }
        finally { pending.TryRemove(id, out _); window.Release(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); requests.Writer.TryComplete();
        foreach (var p in pending.Values) p.TrySetException(new IOException("Peer disposed"));
        opened.TrySetCanceled();
        controlOpened.TrySetCanceled();
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
