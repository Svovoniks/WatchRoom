using System.Diagnostics;
using System.Net.Http.Json;
using Watchroom.Core;

static class DirectControlFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("WATCHROOM_TEST_NODE") ?? "node")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("sites/tests/direct-control-server.mjs"));
        using var server = Process.Start(start)!;
        var errors = server.StandardError.ReadToEndAsync();
        var url = await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("Discovery fixture did not start");
        var output = server.StandardOutput.ReadToEndAsync();
        await using var host = new RoomClient();
        await using var a = new RoomClient();
        await using var b = new RoomClient();
        var statuses = new List<string>();
        host.Status += status => { lock (statuses) statuses.Add(status); };
        try
        {
            var file = Path.Combine(directory, "direct-media.bin");
            await File.WriteAllBytesAsync(file, Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray());
            var source = new BlockingSource(new FileMediaSource(file)); host.HostedMedia = source;
            await host.ConnectAsync(url, "Direct host");
            host.Send(new("media", Data: Wire.Serialize(source.Media)));
            await Wait(() => host.Snapshot?.Media?.Id == source.Media.Id);
            await a.ConnectAsync(url, "Guest A", host.Identity!.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == a.Identity!.Peer));
            check(a.Snapshot?.Media is null, "Sites discovery does not disclose media before admission");
            host.Send(new("admit", Target: a.Identity!.Peer));
            await a.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == a.Identity.Peer && p.Approved));
            check(true, "host participant snapshot confirms approval after admitting a waiting guest");
            await b.ConnectAsync(url, "Guest B", host.Identity.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == b.Identity!.Peer));
            host.Send(new("admit", Target: b.Identity!.Peer));
            await b.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            await Wait(() => a.Snapshot?.Media?.Id == source.Media.Id && b.Snapshot?.Media?.Id == source.Media.Id);
            check(true, "Sites admission establishes two independent direct guest control connections");
            host.Send(new("ready")); a.Send(new("ready")); b.Send(new("ready"));
            await Wait(() => host.Snapshot!.People.Where(p => p.Approved).All(p => p.Ready));
            var denied = new PlaybackState(0, source.Media.Id, true, 1000, 0, Guid.NewGuid().ToString("N"));
            a.Send(new("playback", Sender: host.Identity.Peer, Data: Wire.Serialize(denied)));
            await Task.Delay(150);
            check(host.Snapshot!.Playback!.CommandId != denied.CommandId, "direct guest cannot spoof host authority");
            await Control(host, true, 1000, host, a, b);
            host.Send(new("controls", Number: 1));
            await Wait(() => a.Snapshot!.SharedControls && b.Snapshot!.SharedControls);
            var remote = await a.GetSourceAsync(source.Media, CancellationToken.None);
            source.Block = true;
            var mediaRead = remote.ReadAsync(0, 32768, CancellationToken.None);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var timer = Stopwatch.StartNew();
            await Control(a, false, 0, host, a, b);
            var stopMs = timer.ElapsedMilliseconds;
            check(stopMs < 1000 && !mediaRead.IsCompleted, $"guest stop reaches host and second guest in {stopMs} ms while media read is blocked");
            source.Release.TrySetResult();
            check((await mediaRead.WaitAsync(TimeSpan.FromSeconds(5))).Length == 32768, "media channel continues independently after direct stop");
            await Control(host, true, 2000, host, a, b);
            b.Send(new("buffering"));
            await Wait(() => host.Snapshot!.Playback!.Playing == false && a.Snapshot!.Playback!.Playing == false);
            b.Send(new("ready"));
            await Wait(() => host.Snapshot!.Playback!.Playing && a.Snapshot!.Playback!.Playing && b.Snapshot!.Playback!.Playing);
            check(true, "host pauses and resumes all direct peers when a guest buffers");
            host.Send(new("queue", Data: Wire.Serialize(new[] { "Next", "Then" })));
            await Wait(() => b.Snapshot!.Queue?.Length == 2);
            check(a.Snapshot!.Queue!.SequenceEqual(new[] { "Next", "Then" }), "host distributes queue on the control channel");
            var chat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.Message += message => { if (message.Type == "chat" && message.Text == "Guest A: hello direct peers") chat.TrySetResult(); };
            a.Send(new("chat", Text: "hello direct peers", Sender: host.Identity.Peer));
            await chat.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(true, "host authenticates and relays guest chat to other peers");
            await Task.Delay(2200);
            check(Math.Abs(a.ServerNowMs - host.ServerNowMs) < 250 && Math.Abs(b.ServerNowMs - host.ServerNowMs) < 250,
                "guests synchronize to host clock despite discovery clock being ten minutes ahead");
            using var http = new HttpClient();
            var stats = await http.GetFromJsonAsync<Stats>(url + "/test/stats", Wire.Json);
            check(stats!.Commands.All(type => type is "ping" or "signal" or "admit" or "remove" or "settings"), "no playback, readiness, queue, or chat commands reach Sites");
            server.Kill(true); await server.WaitForExitAsync();
            await Control(b, false, 0, host, a, b);
            check(true, "direct stop still reaches all peers immediately after discovery goes offline");
            await Wait(() => !host.DiscoveryOnline && !a.DiscoveryOnline && !b.DiscoveryOnline, 40000);
            check(host.IsConnected && a.IsConnected && b.IsConnected, "existing direct room survives discovery retry exhaustion");
            await Control(host, true, 0, host, a, b);
            await Control(a, false, 0, host, a, b);
            check(true, "host play and guest stop work after discovery is fully disconnected");
            await a.DisposeAsync(); await b.DisposeAsync();
            await Wait(() => host.Snapshot!.People.Count(p => p.Approved) == 1);
            await Control(host, true, 0, host);
            await Control(host, false, 0, host);
            check(true, "host controls its own player state when no guests remain");
            await host.DisposeAsync();
        }
        finally
        {
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, "discovery-errors.log"), await errors);
        }
    }
    private static async Task Control(RoomClient sender, bool playing, long position, params RoomClient[] participants)
    {
        var command = new PlaybackState(0, sender.Snapshot!.Media!.Id, playing, position, 0, Guid.NewGuid().ToString("N"));
        sender.Send(new("playback", Data: Wire.Serialize(command)));
        await Wait(() => participants.All(p => p.Snapshot?.Playback is { } state && state.CommandId == command.CommandId && state.Playing == playing), 2000);
    }
    private static async Task Wait(Func<bool> condition, int timeoutMs = 20000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds >= timeoutMs) throw new TimeoutException("Direct control state did not converge"); await Task.Delay(10); }
    }
    private sealed record Stats(string[] Commands);
    private sealed class BlockingSource(IMediaSource source) : IMediaSource
    {
        public SharedMedia Media => source.Media;
        public volatile bool Block;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            if (Block) { Started.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return await source.ReadAsync(offset, count, ct);
        }
    }
}
