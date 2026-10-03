namespace Watchroom.Core;

// A per-movie LRU cache reuses nearby seeks without retaining the entire movie.
// Shared reads are canceled only after their last HTTP consumer leaves.
internal sealed class CachedMediaSource(IMediaSource source, int capacityBytes = 16 * 1024 * 1024) : IMediaSource
{
    private readonly object gate = new();
    private readonly Dictionary<long, LinkedListNode<(long Offset, byte[] Bytes)>> cache = [];
    private readonly LinkedList<(long Offset, byte[] Bytes)> recent = [];
    private readonly Dictionary<long, Pending> pending = [];
    private int bytes;
    public SharedMedia Media => source.Media;
    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
    {
        if (offset < 0 || count < 0 || offset > Media.Length || count > Media.Length - offset) throw new ArgumentOutOfRangeException(nameof(offset));
        var result = new byte[count]; var written = 0;
        while (written < count)
        {
            ct.ThrowIfCancellationRequested();
            var position = offset + written; var blockOffset = position / MediaStreaming.ChunkBytes * MediaStreaming.ChunkBytes;
            var block = await Block(blockOffset, ct);
            var within = (int)(position - blockOffset); var copy = Math.Min(count - written, block.Length - within);
            if (copy <= 0) throw new EndOfStreamException("Incomplete media range");
            block.AsSpan(within, copy).CopyTo(result.AsSpan(written)); written += copy;
        }
        return result;
    }
    private async Task<byte[]> Block(long offset, CancellationToken ct)
    {
        Pending read;
        lock (gate)
        {
            if (cache.TryGetValue(offset, out var hit))
            {
                recent.Remove(hit); recent.AddLast(hit); return hit.Value.Bytes;
            }
            if (!pending.TryGetValue(offset, out read!))
            {
                read = new Pending(); pending.Add(offset, read);
                read.Task = Fetch(offset, read);
            }
            read.Users++;
        }
        try { return await read.Task.WaitAsync(ct); }
        finally
        {
            lock (gate)
            {
                if (--read.Users == 0)
                {
                    if (!read.Task.IsCompleted)
                    {
                        if (pending.GetValueOrDefault(offset) == read) pending.Remove(offset);
                        read.Cancellation.Cancel();
                    }
                    _ = read.Task.ContinueWith(t => { _ = t.Exception; read.Cancellation.Dispose(); }, TaskScheduler.Default);
                }
            }
        }
    }
    private async Task<byte[]> Fetch(long offset, Pending read)
    {
        // Yield so the read is registered and has a consumer before completion.
        await Task.Yield();
        try
        {
            var count = (int)Math.Min(MediaStreaming.ChunkBytes, Media.Length - offset);
            var data = await source.ReadAsync(offset, count, read.Cancellation.Token);
            if (data.Length != count) throw new EndOfStreamException("Incomplete media range");
            lock (gate)
            {
                if (!read.Cancellation.IsCancellationRequested && !cache.ContainsKey(offset) && data.Length <= capacityBytes)
                {
                    cache.Add(offset, recent.AddLast((offset, data))); bytes += data.Length;
                    while (bytes > capacityBytes && recent.First is { } oldest)
                    { bytes -= oldest.Value.Bytes.Length; cache.Remove(oldest.Value.Offset); recent.RemoveFirst(); }
                }
            }
            return data;
        }
        finally
        {
            lock (gate) { if (pending.GetValueOrDefault(offset) == read) pending.Remove(offset); }
        }
    }
    private sealed class Pending
    {
        public readonly CancellationTokenSource Cancellation = new();
        public Task<byte[]> Task = null!;
        public int Users;
    }
}
