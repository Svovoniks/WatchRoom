namespace Watchroom.Core;

// Button intent survives network acknowledgement and temporary buffering pauses.
public sealed class PlaybackIntent
{
    private PlaybackState? pending;
    private long revision, requestedAt;
    public bool Playing(PlaybackState? state, bool nativePlaying) => pending?.Playing ?? state?.WantsPlayback ?? nativePlaying;
    public long Position(PlaybackState? state, long nativePosition, long serverNow) =>
        pending?.PositionMs ?? (state is null ? Math.Max(0, nativePosition) : SyncMath.TargetPosition(state, serverNow));
    public void Request(PlaybackState command, long currentRevision)
    { pending = command; revision = currentRevision; requestedAt = Environment.TickCount64; }
    public void Observe(PlaybackState state)
    {
        if (pending is null) return;
        if (state.MediaId != pending.MediaId || state.Revision > revision &&
            (state.CommandId == pending.CommandId || state.CommandId is null))
        {
            PlaybackDiagnostics.Record("control-acknowledged", new { pending.CommandId, elapsedMs = Environment.TickCount64 - requestedAt,
                state.Revision, state.Generation, state.Playing, state.RequestedPlaying });
            pending = null;
        }
    }
    public void Clear() => pending = null;
}
