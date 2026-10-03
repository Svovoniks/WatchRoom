namespace Watchroom.Core;

// VLC emits short 0%/100% bursts on seeks. Only sustained buffering should pause
// the room; otherwise a correction itself produces another playback revision.
public sealed class PlaybackBuffering
{
    private long? started, fullSince;
    private long lastNotice = long.MinValue / 2;
    private bool reported;
    public bool IsActive => started is not null;
    public bool Cache(float percent, long monotonicMs)
    {
        if (percent < 100) { started ??= monotonicMs; fullSince = null; }
        else if (started is not null) fullSince ??= monotonicMs;
        return Recover(monotonicMs);
    }
    public bool Recover(long monotonicMs)
    {
        // One 100% notification can be followed immediately by another stall.
        if (fullSince is not { } since || monotonicMs - since < 750) return false;
        started = fullSince = null; var notify = reported; reported = false; return notify;
    }
    public bool Poll(bool playing, long monotonicMs)
    {
        if (!playing || reported || started is not { } since || fullSince is not null || monotonicMs - since < 750 || monotonicMs - lastNotice < 5000) return false;
        reported = true; lastNotice = monotonicMs; return true;
    }
}
