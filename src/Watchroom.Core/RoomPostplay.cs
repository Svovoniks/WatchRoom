namespace Watchroom.Core;

// Whether the local user still has to see the postplay promo. The host keeps everyone's answer in the room
// snapshot, so it can start the promo for the whole room when anyone has not seen it.
public sealed partial class RoomClient
{
    private int postplayDesired;
    private (string Key, long At)? postplaySent;
    public void SetPostplayPending(bool pending)
    {
        var state = pending ? 1 : 0;
        if (Interlocked.Exchange(ref postplayDesired, state) == state) return;
        Post(new("postplay-local"));
    }
    private void ApplyLocalPostplay()
    {
        if (Identity?.Host == true) { if (coordinator is not null) ApplyHostCommand(Identity.Peer, new("postplay", Number: Volatile.Read(ref postplayDesired))); }
        else ReconcilePostplay();
    }
    // Guests repeat their answer until the host's snapshot reflects it, including after a reconnect.
    private void ReconcilePostplay()
    {
        if (Identity is not { Host: false } identity || hostPeer is not { } host || !negotiated.ContainsKey(host) || Snapshot?.Postplay is not { } postplay) return;
        var desired = Volatile.Read(ref postplayDesired);
        if (postplay.Pending.Contains(identity.Peer) == (desired == 1)) { postplaySent = null; return; }
        var key = peers.GetValueOrDefault(host)?.DiagnosticId + ":" + desired;
        if (postplaySent is { } sent && sent.Key == key && Environment.TickCount64 - sent.At < 2000) return;
        postplaySent = (key, Environment.TickCount64);
        SendPeer(host, new("postplay", Number: desired));
    }
}
