namespace Watchroom.Core;

// VLC publishes time only a few times per second. Extrapolate briefly between
// native observations, but never extrapolate indefinitely through a stalled read.
public sealed class PlaybackPosition
{
    private readonly object gate = new();
    private long position = -1, at;
    public void Observe(long milliseconds, long monotonicMs)
    { lock (gate) { position = milliseconds; at = monotonicMs; } }
    public long Estimate(long actual, bool playing, float rate, long monotonicMs)
    {
        lock (gate)
        {
            if (!playing || actual < 0 || actual != position) return Math.Max(0, actual);
            return Math.Max(0, position + (long)(Math.Clamp(monotonicMs - at, 0, 500) * rate));
        }
    }
}
