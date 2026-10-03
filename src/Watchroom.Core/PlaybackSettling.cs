namespace Watchroom.Core;

// Allow a native asynchronous seek to complete before issuing another correction.
public sealed class PlaybackSettling
{
    private long revision = -1, until, started, requested, firstAt, firstPosition;
    private bool observed, advancing, playing;
    public void Seek(long revision, long position, long monotonicMs, bool playing = true)
    {
        this.playing = playing;
        this.revision = revision; requested = position; started = monotonicMs;
        until = monotonicMs + 2000; observed = advancing = false;
        if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("settle-start", new { revision, position });
    }
    // Called from the UI dispatch of native TimeChanged events. A setter echo
    // alone is insufficient while playing: wait for timeline advancement.
    // A paused seek instead waits for a matching native position and minimum hold.
    public void Observe(long position, long monotonicMs)
    {
        if (until == 0 || monotonicMs < started || position < requested - 600 || position > requested + 2500) return;
        if (!observed) { observed = true; firstAt = monotonicMs; firstPosition = position; }
        else if (monotonicMs - firstAt >= 200 && position - firstPosition >= 50) advancing = true;
    }
    public bool Waiting(long currentRevision, long actualPosition, long monotonicMs)
    {
        if (currentRevision != revision || monotonicMs >= until || (advancing || !playing && observed && Math.Abs(actualPosition - requested) <= 200) && monotonicMs - started >= 300)
        {
            if (until != 0 && PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("settle-end", new { revision, elapsedMs = monotonicMs - started, reason = currentRevision != revision ? "superseded" : monotonicMs >= until ? "timeout" : playing ? "advancing" : "position", actualPosition });
            until = 0; return false;
        }
        return true;
    }
}
