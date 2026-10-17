namespace Watchroom.Core;

// A per-movie LRU cache reuses nearby seeks without retaining the entire movie.
// Shared reads are canceled only after their last HTTP consumer leaves.
// Optional read-ahead fills the cache past the playback position in the
// background, so starting playback never waits for the configured buffer.
internal sealed class CachedMediaSource(IMediaSource source, int capacityBytes = 16 * 1024 * 1024) : IMediaSource
{
    private const int PrefetchReads = 4;
    private const long JumpSlackBytes = 2L * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<long, LinkedListNode<(long Offset, byte[] Bytes)>> cache = [];
    private readonly LinkedList<(long Offset, byte[] Bytes)> recent = [];
    private readonly Dictionary<long, Pending> pending = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private CancellationTokenSource epoch = new();
    private Task? prefetch;
    private long readAhead, anchor = -1, cursor = -1, retryAt;
    private readonly int baseCapacity = capacityBytes;
    private int capacity = capacityBytes;
    private int bytes;
    public SharedMedia Media => source.Media;
    public long ReadAheadBytes { get { lock (gate) return readAhead; } }
    public long CachedAheadBytes
    {
        get
        {
            lock (gate)
            {
                if (anchor < 0) return 0;
                var block = anchor / MediaStreaming.ChunkBytes * MediaStreaming.ChunkBytes;
                while (block < Media.Length && cache.ContainsKey(block)) block += MediaStreaming.ChunkBytes;
                return Math.Max(0, Math.Min(block, Media.Length) - anchor);
            }
        }
    }
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
    public void SetReadAhead(long aheadBytes)
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) return;
            readAhead = Math.Clamp(aheadBytes, 0, StreamingBufferOptions.MaximumReadAheadBytes);
            capacity = (int)Math.Max(baseCapacity, readAhead + baseCapacity);
            if (readAhead > 0) prefetch ??= Task.Run(Prefetch);
        }
        Wake();
    }
    // A new HTTP range far from the playback position (a seek or an index
    // probe) pauses read-ahead until a playback stream advances again.
    public void Requested(long offset)
    {
        lock (gate)
        {
            if (anchor < 0 || offset >= anchor - JumpSlackBytes && offset <= anchor + readAhead + JumpSlackBytes) return;
            Reposition(-1);
        }
    }
    public void Played(long position)
    {
        lock (gate)
        {
            if (anchor < 0 || position < anchor || position > anchor + readAhead + JumpSlackBytes) Reposition(position);
            else anchor = position;
        }
        Wake();
    }
    public void Stop()
    {
        lock (gate) { if (lifetime.IsCancellationRequested) return; lifetime.Cancel(); epoch.Cancel(); }
        _ = prefetch?.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
    private void Reposition(long position)
    {
        epoch.Cancel(); epoch = new();
        anchor = position; cursor = position < 0 ? -1 : position / MediaStreaming.ChunkBytes * MediaStreaming.ChunkBytes;
    }
    private void Wake()
    {
        try { if (wake.CurrentCount == 0) wake.Release(); }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException) { }
    }
    private async Task Prefetch()
    {
        var reads = new List<Task>(); Task? signal = null;
        while (!lifetime.IsCancellationRequested)
        {
            reads.RemoveAll(read => read.IsCompleted);
            if (reads.Count < PrefetchReads && NextBlock() is { } next) { reads.Add(Warm(next.Offset, next.Token)); continue; }
            signal ??= wake.WaitAsync(lifetime.Token);
            // Retry after a failed read, even when nothing else wakes the loop.
            var wait = Interlocked.Read(ref retryAt) - Environment.TickCount64;
            await Task.WhenAny(wait > 0 ? [.. reads, signal, Task.Delay((int)Math.Min(wait, 1000), lifetime.Token)] : [.. reads, signal]);
            if (signal.IsCompleted) { if (signal.IsCanceled) return; signal = null; }
        }
    }
    private (long Offset, CancellationToken Token)? NextBlock()
    {
        lock (gate)
        {
            if (anchor < 0 || cursor < 0 || readAhead <= 0 || Environment.TickCount64 < retryAt) return null;
            var limit = Math.Min(Media.Length, anchor + readAhead);
            cursor = Math.Max(cursor, anchor / MediaStreaming.ChunkBytes * MediaStreaming.ChunkBytes);
            while (cursor < limit && (cache.ContainsKey(cursor) || pending.ContainsKey(cursor))) cursor += MediaStreaming.ChunkBytes;
            if (cursor >= limit) return null;
            var offset = cursor; cursor += MediaStreaming.ChunkBytes;
            return (offset, epoch.Token);
        }
    }
    private async Task Warm(long offset, CancellationToken ct)
    {
        try { await Block(offset, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception)
        {
            // An unavailable host must not turn read-ahead into a retry storm.
            lock (gate) { retryAt = Environment.TickCount64 + 1000; if (cursor > offset) cursor = offset; }
        }
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
                if (!read.Cancellation.IsCancellationRequested && !cache.ContainsKey(offset) && data.Length <= capacity)
                {
                    cache.Add(offset, recent.AddLast((offset, data))); bytes += data.Length;
                    while (bytes > capacity && recent.First is { } oldest)
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
