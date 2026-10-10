using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Watchroom.Core;

static class PeerRecoveryFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("WATCHROOM_TEST_NODE") ?? "node")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.GetFullPath("sites/tests/direct-control-server.mjs"));
        using var server = Process.Start(info)!;
        var errors = server.StandardError.ReadToEndAsync();
        var url = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("No recovery fixture URL");
        var output = server.StandardOutput.ReadToEndAsync();
        await using var host = new RoomClient();
        await using var guest = new RoomClient();
        try
        {
            var source = new InterruptedSource();
            host.HostedMedia = source;
            await host.ConnectAsync(url, "Recovery host");
            host.Send(new("media", Data: Wire.Serialize(source.Media)));
            await guest.ConnectAsync(url, "Recovery guest", host.Identity!.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer));
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(20));
            await Wait(() => guest.Snapshot?.Media?.Id == source.Media.Id);
            var playback = guest.Snapshot!.Playback;
            var remote = await guest.GetSourceAsync(source.Media, default);
            var cached = new CachedMediaSource(remote);
            check((await cached.ReadAsync(0, 100, default)).SequenceEqual(source.Bytes[..100]), "guest reads the original media before interruption");
            var interrupted = cached.ReadAsync(MediaStreaming.ChunkBytes, 100, default);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoomSnapshot? retained = null;
            guest.Message += message =>
            {
                if (message.Type == "peer-recovering") { retained = guest.Snapshot; recovering.TrySetResult(); }
                if (message.Type == "peer-recovered") recovered.TrySetResult();
            };
            var before = Stopwatch.StartNew();
            Peers(host)[guest.Identity!.Peer].Dispose();
            await recovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(retained?.Media?.Id == source.Media.Id && retained.Playback == playback && guest.IsConnected,
                "temporary peer loss retains the authoritative movie and timeline in the same room");
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(before.ElapsedMilliseconds < 5000 && !guest.IsPeerRecovering,
                "closed peer reconnects immediately without waiting for the ten-second discovery heartbeat");
            source.Release.TrySetResult();
            check((await interrupted.WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(source.Bytes.AsSpan(MediaStreaming.ChunkBytes, 100).ToArray()),
                "in-flight media range retries on the replacement peer using the existing source");
            var reads = source.Reads;
            await cached.ReadAsync(0, 100, default);
            check(source.Reads == reads, "transport recovery preserves media blocks already in the guest cache");

            // Exercise the stale-reply path without waiting on nondeterministic
            // native retransmission timers. The real peer is still open here.
            var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            guest.Message += message => { if (message.Type == "peer-recovered") fresh.TrySetResult(); };
            SetField(guest, "lastHostMessageMs", Environment.TickCount64 - 7000);
            PostHealth(guest);
            await fresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(!guest.IsPeerRecovering && guest.Snapshot!.Media!.Id == source.Media.Id,
                "silent control replies replace an open peer while retaining the selected movie");

            var changed = source.Media with { Id = "replacement-movie" };
            host.Send(new("media", Data: Wire.Serialize(changed)));
            await Wait(() => guest.Snapshot?.Media?.Id == changed.Id);
            try { await remote.ReadAsync(0, 100, default); check(false, "superseded media read must fail"); }
            catch (IOException) { check(true, "a retained source cannot read a movie the host is no longer sharing"); }

            host.Send(new("media", Data: Wire.Serialize(source.Media)));
            await Wait(() => guest.Snapshot?.Media?.Id == source.Media.Id);
            // Prevent replies from starting a replacement handshake; a canceled
            // reader must not wait for the 45-second recovery budget.
            var controlConnection = (IRoomConnection)GetField(guest, "connection")!;
            controlConnection.Abort();
            await Wait(() => !guest.DiscoveryOnline);
            Peers(guest).Values.Single().Dispose();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try { await remote.ReadAsync(2 * MediaStreaming.ChunkBytes, 100, cancel.Token); check(false, "canceled recovery read must stop"); }
            catch (OperationCanceledException) { check(true, "canceling an HTTP reader promptly stops its recovery wait"); }
            var waiting = remote.ReadAsync(2 * MediaStreaming.ChunkBytes, 100, default);
            await guest.DisposeAsync();
            try { await waiting.WaitAsync(TimeSpan.FromSeconds(2)); check(false, "leaving room must cancel recovery reads"); }
            catch (OperationCanceledException) { check(true, "leaving a room cancels pending recovery reads"); }
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, "discovery-errors.log"), await errors);
        }
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? GetField(RoomClient client, string name) => typeof(RoomClient).GetField(name, Flags)!.GetValue(client);
    private static void SetField(RoomClient client, string name, object value) => typeof(RoomClient).GetField(name, Flags)!.SetValue(client, value);
    private static ConcurrentDictionary<string, PeerTransport> Peers(RoomClient client) => (ConcurrentDictionary<string, PeerTransport>)GetField(client, "peers")!;
    private static void PostHealth(RoomClient client)
    {
        var eventType = typeof(RoomClient).GetNestedType("RoomEvent", BindingFlags.NonPublic)!;
        var constructor = eventType.GetConstructors().Single();
        var values = constructor.GetParameters().Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null).ToArray();
        values[0] = "peer-health";
        typeof(RoomClient).GetMethod("Post", Flags)!.Invoke(client, [constructor.Invoke(values)]);
    }
    private static async Task Wait(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class InterruptedSource : IMediaSource
    {
        public readonly byte[] Bytes = Enumerable.Range(0, MediaStreaming.ChunkBytes * 3).Select(i => (byte)(i % 251)).ToArray();
        public SharedMedia Media => new("recovery-media", "Recovery fixture", Bytes.Length, ".bin");
        public int Reads;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            if (offset == MediaStreaming.ChunkBytes) { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return Bytes.AsSpan((int)offset, count).ToArray();
        }
    }
}
