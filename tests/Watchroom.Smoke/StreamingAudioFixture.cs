using System.Collections.Concurrent;
using System.Text;
using LibVLCSharp.Shared;
using Watchroom.Core;

static class StreamingAudioFixture
{
    public static async Task Run(string directory, Action<bool, string> check, int cacheMs = MediaStreaming.NetworkCacheMs, string[]? iceServers = null, bool relayOnly = false)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "generated-audio.wav");
        const int sampleRate = 48000, seconds = 8, channels = 2;
        using (var output = new BinaryWriter(File.Create(path)))
        {
            void Four(string value) => output.Write(Encoding.ASCII.GetBytes(value));
            var size = sampleRate * seconds * channels * sizeof(short);
            Four("RIFF"); output.Write(size + 36); Four("WAVE"); Four("fmt "); output.Write(16);
            output.Write((short)1); output.Write((short)channels); output.Write(sampleRate);
            output.Write(sampleRate * channels * sizeof(short)); output.Write((short)(channels * sizeof(short))); output.Write((short)16);
            Four("data"); output.Write(size);
            for (var frame = 0; frame < sampleRate * seconds; frame++)
            {
                var sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / sampleRate) * 8000);
                output.Write(sample); output.Write(sample);
            }
        }
        var delayed = new DelayedSource(new FileMediaSource(path));
        await using var bridge = new MediaBridge();
        var url = await bridge.StartAsync(delayed);
        using (var http = new HttpClient())
        {
            var bytes = await http.GetByteArrayAsync(url);
            var expected = await File.ReadAllBytesAsync(path);
            check(bytes.SequenceEqual(expected), "delayed streaming pipeline preserves every audio byte");
            check(delayed.Maximum >= 16 && delayed.Maximum <= MediaStreaming.Window,
                "WAN-latency pipeline keeps sufficient requests in flight within its memory bound");
        }
        using var host = new PeerTransport(iceServers ?? [], relayOnly);
        using var guest = new PeerTransport(iceServers ?? [], relayOnly);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        var signals = System.Threading.Channels.Channel.CreateUnbounded<(PeerTransport Peer, string Type, string Data)>();
        host.Signal += (type, data) => signals.Writer.TryWrite((guest, type, data));
        guest.Signal += (type, data) => signals.Writer.TryWrite((host, type, data));
        var pump = Task.Run(async () =>
        {
            try { await foreach (var signal in signals.Reader.ReadAllAsync(cancellation.Token)) signal.Peer.ReceiveSignal(signal.Type, signal.Data); }
            catch (OperationCanceledException) { }
        });
        host.ResolveMedia = id => id == delayed.Media.Id ? delayed : null;
        host.StartOffer();
        await Task.WhenAll(host.Ready, guest.Ready).WaitAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAll(host.ControlReady, guest.ControlReady).WaitAsync(TimeSpan.FromSeconds(5));
        var command = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.ControlMessage += message => { if (message.Type == "notice" && message.Text == "audio-test-control") command.TrySetResult(); };
        var fullWindow = Task.WhenAll(Enumerable.Range(0, MediaStreaming.Window).Select(index =>
            guest.ReadAsync(delayed.Media.Id, index * MediaStreaming.ChunkBytes, MediaStreaming.ChunkBytes, cancellation.Token)));
        await Task.Delay(50);
        host.SendControl(new("notice", Text: "audio-test-control"));
        await command.Task.WaitAsync(TimeSpan.FromSeconds(1));
        check(true, "room controls remain responsive while the enlarged media window is full");
        await fullWindow;
        await using var peerBridge = new MediaBridge();
        var peerUrl = await peerBridge.StartAsync(new RemoteMediaSource(guest, delayed.Media));
        Core.Initialize();
        using var vlc = new LibVLC("--vout=dummy", "--no-video-title-show", "--quiet");
        using var player = new MediaPlayer(vlc);
        var blocks = new ConcurrentQueue<(long Pts, uint Samples, long At)>();
        player.SetAudioFormat("S16N", sampleRate, channels);
        player.SetAudioCallbacks((_, _, count, pts) => blocks.Enqueue((pts, count, vlc.Clock)), null, null, null, null);
        using var media = new Media(vlc, peerUrl);
        media.AddOption($":network-caching={cacheMs}");
        check(player.Play(media), "guest audio input starts through the delayed HTTP bridge");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (player.State != VLCState.Ended && player.State != VLCState.Error && DateTime.UtcNow < deadline) await Task.Delay(50);
        check(player.State == VLCState.Ended, "guest audio decodes to completion despite network jitter");
        var decoded = blocks.ToArray();
        var duration = decoded.Sum(block => (long)block.Samples) * 1000 / sampleRate;
        long maximumGap = 0;
        for (var i = 1; i < decoded.Length; i++)
            maximumGap = Math.Max(maximumGap, decoded[i].Pts - decoded[i - 1].Pts - decoded[i - 1].Samples * 1000000L / sampleRate);
        check(duration >= seconds * 1000 - 100, "guest outputs the complete generated audio track");
        check(maximumGap < 100000, $"guest audio presentation has no gaps over 100 ms (maximum {maximumGap / 1000} ms)");
        var late = decoded.Count(block => block.At - block.Pts > 100000);
        check(late == 0, $"guest audio arrives before its playback deadline ({late} blocks over 100 ms late)");
        player.Stop();
        // Exercise the native paused-seek path through the same guest HTTP input.
        // Previously settling required advancement, which a paused input cannot do.
        var settling = new PlaybackSettling();
        var settleGate = new object();
        player.TimeChanged += (_, e) => { lock (settleGate) settling.Observe(e.Time, Environment.TickCount64); };
        check(player.Play(media), "cached guest stream restarts for seek recovery checks");
        async Task WaitFor(Func<bool> predicate, string name)
        {
            var until = DateTime.UtcNow.AddSeconds(8);
            while (!predicate() && DateTime.UtcNow < until) await Task.Delay(50);
            check(predicate(), name);
        }
        await WaitFor(() => player.IsPlaying && player.Time >= 500, "restarted guest stream advances before seeking");
        player.SetPause(true);
        await WaitFor(() => player.State == VLCState.Paused, "guest Pause takes effect before a seek");
        var seekStarted = Environment.TickCount64;
        lock (settleGate) settling.Seek(1, 4000, seekStarted, playing: false);
        player.Time = 4000;
        bool SeekSettled() { lock (settleGate) return !settling.Waiting(1, player.Time, Environment.TickCount64) && Math.Abs(player.Time - 4000) < 300; }
        await WaitFor(SeekSettled,
            "native guest seek reaches its position while remaining paused");
        check(!player.IsPlaying, "native seek does not override explicit Pause intent");
        player.SetPause(false);
        await WaitFor(() => player.IsPlaying && player.Time > 4200, "guest resumes from the newly sought position");
        player.Time = 6500; player.Time = 1000;
        await WaitFor(() => player.IsPlaying && player.Time >= 1000 && player.Time < 2500,
            "latest rapid guest seek supersedes an older native seek");
        player.Stop();
        cancellation.Cancel(); await pump;
    }

    private sealed class DelayedSource(FileMediaSource file) : IMediaSource
    {
        private int active, maximum, sequence;
        public int Maximum => maximum;
        public SharedMedia Media => file.Media;
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            var concurrent = Interlocked.Increment(ref active);
            int previous;
            do { previous = maximum; } while (concurrent > previous && Interlocked.CompareExchange(ref maximum, concurrent, previous) != previous);
            try
            {
                // A round trip for every range, with recurring bursts of jitter.
                await Task.Delay(Interlocked.Increment(ref sequence) % 17 == 0 ? 900 : 80, ct);
                return await file.ReadAsync(offset, count, ct);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
