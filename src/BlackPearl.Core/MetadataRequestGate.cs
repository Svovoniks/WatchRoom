namespace BlackPearl.Core;

// Bound active requests and space their starts independently for each provider.
internal sealed class MetadataRequestGate(int intervalMilliseconds, int concurrency = 2)
{
    private readonly SemaphoreSlim active = new(concurrency);
    private readonly SemaphoreSlim starts = new(1);
    private readonly object sync = new();
    private long next;
    private int interval = intervalMilliseconds;
    public async Task<IDisposable> Enter(CancellationToken ct)
    {
        await active.WaitAsync(ct);
        try
        {
            await starts.WaitAsync(ct);
            try
            {
                while (true)
                {
                    long delay;
                    lock (sync)
                    {
                        delay = next - Environment.TickCount64;
                        if (delay <= 0) { next = Environment.TickCount64 + interval; break; }
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                }
            }
            finally { starts.Release(); }
            return new Lease(active);
        }
        catch { active.Release(); throw; }
    }
    public void Backoff(TimeSpan delay, bool adapt = false)
    {
        lock (sync)
        {
            if (adapt) interval = Math.Min(1500, interval + 100);
            next = Math.Max(next, Environment.TickCount64 + (long)Math.Max(0, delay.TotalMilliseconds));
        }
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release(); }
    }
}
