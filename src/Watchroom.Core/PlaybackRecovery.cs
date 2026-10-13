namespace Watchroom.Core;

// Continue only playback that was already running. Recovery never starts a
// paused video, and expiration stays latched until the peer reconnects.
public sealed class PlaybackRecovery
{
    private long? started;
    private int graceMs;
    private bool held;

    public bool Begin(bool playing, int bufferSeconds, long monotonicMs)
    {
        if (started is not null) return held;
        started = monotonicMs;
        graceMs = StreamingBufferOptions.CacheMs(bufferSeconds);
        held = !playing;
        return held;
    }

    public bool ShouldPause(bool playbackAllowed, bool starved, long monotonicMs)
    {
        if (started is not { } since) return false;
        held |= !playbackAllowed || starved || monotonicMs - since >= graceMs;
        return held;
    }

    public void Clear() { started = null; held = false; }
}
