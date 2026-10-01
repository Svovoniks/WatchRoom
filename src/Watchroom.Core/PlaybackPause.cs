namespace Watchroom.Core;

// A local pause must survive old room updates while its command is in flight.
// New services echo CommandId; older services acknowledge by revision and state.
public sealed class PlaybackPause
{
    private PlaybackState? pending;
    private long revision;
    public bool Pending => pending is not null;
    public void Request(PlaybackState command, long currentRevision)
    {
        pending = command.Playing ? null : command;
        revision = currentRevision;
    }
    public void Observe(PlaybackState state)
    {
        if (pending is null) return;
        if (state.MediaId != pending.MediaId) { Clear(); return; }
        if (state.Revision <= revision) return;
        if (state.CommandId == pending.CommandId && state.CommandId is not null ||
            state.CommandId is null && !state.Playing && state.PositionMs == pending.PositionMs)
            Clear();
    }
    public void Clear() => pending = null;
}
