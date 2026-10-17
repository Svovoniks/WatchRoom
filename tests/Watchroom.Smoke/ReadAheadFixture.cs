using System.Collections.Concurrent;
using Watchroom.Core;

static class ReadAheadFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        const int block = MediaStreaming.ChunkBytes;
        check(StreamingBufferOptions.ReadAheadBytes(30, 3_600_000_000, 3_600_000) == 37_500_000,
            "thirty-second read-ahead follows the movie's average bitrate with variable-bitrate headroom");
        check(StreamingBufferOptions.ReadAheadBytes(60, long.MaxValue / 4, 1000) == StreamingBufferOptions.MaximumReadAheadBytes &&
            StreamingBufferOptions.ReadAheadBytes(30, 1000, 0) == 0, "read-ahead is bounded and waits for a known duration");

        var source = new Source(block * 64);
        var cached = new CachedMediaSource(source);
        cached.Played(0);
        await Task.Delay(200);
        check(source.Offsets.IsEmpty, "without a configured buffer, playback reads only what the player requests");
        cached.SetReadAhead(block * 10);
        await Until(() => cached.CachedAheadBytes >= block * 10);
        check(source.Offsets.All(offset => offset < block * 10) && source.Offsets.Count == 10,
            "read-ahead fills the configured window past the playback position without player requests");
        cached.Played(block * 5);
        await Until(() => source.Offsets.Contains(block * 14));
        check(!source.Offsets.Any(offset => offset >= block * 15), "read-ahead follows the playback position and stops at the window");
        var before = source.Offsets.Count;
        check((await cached.ReadAsync(block * 6, block * 4, default)).Length == block * 4 && source.Offsets.Count == before,
            "played video comes from the read-ahead cache");

        cached.Requested(block * 60);
        cached.Played(block * 40);
        await Until(() => source.Offsets.Contains(block * 49));
        check(!source.Offsets.Any(offset => offset is >= block * 15 and < block * 40), "a seek moves read-ahead to the new position");
        cached.Stop();

        var failing = new Source(block * 64) { Fail = true };
        cached = new CachedMediaSource(failing);
        cached.SetReadAhead(block * 10); cached.Played(0);
        await Task.Delay(1500);
        check(failing.Attempts is > 0 and <= 12, "an unavailable host is retried at a bounded rate instead of in a tight loop");
        cached.Stop();
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Source(long length) : IMediaSource
    {
        public readonly ConcurrentBag<long> Offsets = [];
        public bool Fail;
        public int Attempts;
        public SharedMedia Media { get; } = new("read-ahead", "Read-ahead", length, ".mkv");
        public Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            if (Fail) throw new IOException("Host unavailable");
            Offsets.Add(offset);
            return Task.FromResult(new byte[count]);
        }
    }
}
