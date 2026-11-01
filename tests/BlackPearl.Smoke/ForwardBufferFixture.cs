using System.Collections.Concurrent;
using System.Text;
using LibVLCSharp.Shared;
using BlackPearl.Core;

static class ForwardBufferFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "buffered.wav");
        const int sampleRate = 48000, channels = 2, seconds = 90;
        using (var output = new BinaryWriter(File.Create(path)))
        {
            void Four(string value) => output.Write(Encoding.ASCII.GetBytes(value));
            var size = sampleRate * channels * sizeof(short) * seconds;
            Four("RIFF"); output.Write(size + 36); Four("WAVE"); Four("fmt "); output.Write(16);
            output.Write((short)1); output.Write((short)channels); output.Write(sampleRate);
            output.Write(sampleRate * channels * sizeof(short)); output.Write((short)(channels * sizeof(short))); output.Write((short)16);
            Four("data"); output.Write(size);
            var second = new byte[sampleRate * channels * sizeof(short)];
            for (var i = 0; i < seconds; i++) output.Write(second);
        }
        // Stop the source after twenty seconds of media, before the native
        // reader can consume the entire (small, highly compressible) fixture.
        var source = new InterruptibleSource(new FileMediaSource(path), sampleRate * channels * sizeof(short) * 20);
        await using var bridge = new MediaBridge();
        var url = await bridge.StartAsync(source);
        Core.Initialize();
        using var vlc = new LibVLC("--vout=dummy", "--no-video-title-show", "--quiet");
        using var player = new MediaPlayer(vlc);
        var decoded = new ConcurrentQueue<(long Pts, uint Samples)>();
        player.SetAudioFormat("S16N", sampleRate, channels);
        player.SetAudioCallbacks((_, _, count, pts) => decoded.Enqueue((pts, count)), null, null, null, null);
        using var media = new Media(vlc, url);
        // Start from the short RTT cache; the bridge holds the ten-second buffer.
        media.AddOption($":network-caching={MediaStreaming.NetworkCacheForRtt(50)}");
        bridge.SetReadAhead(StreamingBufferOptions.ReadAheadBytes(10, source.Media.Length, seconds * 1000L));
        async Task Wait(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!condition()) await Task.Delay(25, timeout.Token);
        }
        try
        {
            check(player.Play(media), "native player starts from the short network cache with ten seconds of bridge read-ahead");
            await Wait(() => player.IsPlaying && player.Time > 500);
            await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(100);
            var before = player.Time; var reads = Volatile.Read(ref source.Reads);
            await Task.Delay(3000);
            check(Volatile.Read(ref source.Reads) == reads,
                "simulated host outage delivers no fresh movie bytes for three seconds");
            check(player.IsPlaying && player.Time >= before + 2000,
                "bridge read-ahead sustains playback across three seconds of host unavailability");
            source.Resume();
            await Wait(() => Volatile.Read(ref source.Reads) > reads && player.Time > before + 4000);
            var blocks = decoded.ToArray();
            var maximumGap = blocks.Zip(blocks.Skip(1), (a, b) => b.Pts - a.Pts - a.Samples * 1000000L / sampleRate).DefaultIfEmpty().Max();
            check(maximumGap < 100000 && player.State != VLCState.Error,
                "restored media transfer continues through the same native input without audio timeline gaps");
        }
        finally { source.Resume(); player.Stop(); }
    }

    private sealed class InterruptibleSource(IMediaSource inner, long stopOffset) : IMediaSource
    {
        private TaskCompletionSource? outage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
        public SharedMedia Media => inner.Media;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resume() => Interlocked.Exchange(ref outage, null)?.TrySetResult();
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            if (offset >= stopOffset && Volatile.Read(ref outage) is { } stopped)
            { Blocked.TrySetResult(); await stopped.Task.WaitAsync(ct); }
            var bytes = await inner.ReadAsync(offset, count, ct);
            Interlocked.Increment(ref Reads);
            return bytes;
        }
    }
}
