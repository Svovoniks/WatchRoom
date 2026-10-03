namespace Watchroom.Core;

// Clock observations are noisy. Require persistent drift before changing the
// native audio/video clock, and use hysteresis while converging.
public sealed class PlaybackRateControl
{
    private int candidate;
    private long since;
    private float rate = 1;

    public float Update(long driftMs, long monotonicMs)
    {
        var direction = Math.Sign(driftMs);
        var magnitude = Math.Abs(driftMs);
        if (magnitude < 80) { Reset(); return rate; }
        if (rate != 1 && Math.Sign(rate - 1) == direction) return rate;
        if (rate != 1) { rate = 1; candidate = 0; }
        if (magnitude < 200) { candidate = 0; return rate; }
        if (candidate != direction) { candidate = direction; since = monotonicMs; }
        if (monotonicMs - since >= 750)
        {
            rate = direction > 0 ? 1.03f : .97f;
            candidate = 0;
        }
        return rate;
    }

    public void Reset() { candidate = 0; rate = 1; }
}
