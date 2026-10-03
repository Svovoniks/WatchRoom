namespace Watchroom.Core;

// VLC emits short 0%/100% bursts on seeks. Only sustained buffering should pause
// the room; otherwise a correction itself produces another playback revision.
public sealed class PlaybackBuffering
{
    private long? started;
    private long lastNotice = long.MinValue / 2;
    private bool reported;
    public bool IsActive => started is not null;
    public bool Cache(float percent, long monotonicMs)
    {
        if (percent < 100) started ??= monotonicMs;
        if (percent < 100) return false;
        started = null; var ready = reported; reported = false; return ready;
    }
    public bool Poll(bool playing, long monotonicMs)
    {
        if (!playing || reported || started is not { } since || monotonicMs - since < 500 || monotonicMs - lastNotice < 5000) return false;
        reported = true; lastNotice = monotonicMs; return true;
    }
}
