using System.Collections.Concurrent;

namespace BlackPearl.Core;

// Voice chat over the room's direct connections. Guests send their microphone frames to the host; the host
// relays each speaker's frames to everyone else in voice. Frames bypass the control loop for latency, so the
// relay reads an immutable routing table that the control loop republishes whenever voice state changes.
public sealed partial class RoomClient
{
    private IVoiceSink? voiceOutput;
    // Receives the room's voice while the local user is in voice.
    public IVoiceSink? VoiceOutput { get => Volatile.Read(ref voiceOutput); set => Volatile.Write(ref voiceOutput, value); }
    // Bit 1: in voice; bit 2: microphone muted.
    private int voiceDesired;
    private sealed record VoiceRoutes(IReadOnlyDictionary<string, byte> Talkers, string[] Listeners, byte HostSlot, bool HostListening, bool HostTalking);
    private VoiceRoutes voiceRoutes = new(new Dictionary<string, byte>(), [], 0, false, false);
    private sealed class VoiceBudget { public double Tokens = 50; public long Refilled = Environment.TickCount64; }
    private readonly ConcurrentDictionary<string, VoiceBudget> voiceBudgets = new();
    private (string Key, long At)? voiceSent;
    public bool InVoice => (Volatile.Read(ref voiceDesired) & 1) != 0;
    // The host supports voice and, for guests, its direct connection is up.
    public bool VoiceAvailable => IsConnected && Snapshot?.Voice is not null && (Identity?.Host == true || directConnected.Task.IsCompletedSuccessfully);
    public VoiceMember? VoiceState(string peer) => Snapshot?.Voice?.FirstOrDefault(v => v.Peer == peer);
    public void SetVoice(bool joined, bool muted)
    {
        var state = (joined ? 1 : 0) | (muted ? 2 : 0);
        if (Interlocked.Exchange(ref voiceDesired, state) == state) return;
        PlaybackDiagnostics.Record("voice-state", new { room = diagnosticId, joined, muted });
        Post(new("voice-local"));
    }
    private void ApplyLocalVoice()
    {
        if (Identity?.Host == true) { if (coordinator is not null) ApplyHostCommand(Identity.Peer, new("voice", Number: Volatile.Read(ref voiceDesired))); }
        else ReconcileVoice();
    }
    // Guests repeat their state until the host's snapshot reflects it, including after a reconnect replaced the
    // transport: quickly at first so joining voice is not held up by one lost message, then every few seconds.
    private int voiceRetries;
    private void ReconcileVoice()
    {
        if (Identity is not { Host: false } identity || hostPeer is not { } host || !negotiated.ContainsKey(host) || Snapshot?.Voice is not { } members) return;
        var desired = Volatile.Read(ref voiceDesired);
        var mine = members.FirstOrDefault(v => v.Peer == identity.Peer);
        if (mine is not null && (mine.Joined ? 1 : 0) + (mine.Muted ? 2 : 0) == desired) { voiceSent = null; voiceRetries = 0; return; }
        var key = peers.GetValueOrDefault(host)?.DiagnosticId + ":" + desired;
        var interval = voiceRetries < 6 ? 400 : 3000;
        if (voiceSent is { } sent && sent.Key == key && Environment.TickCount64 - sent.At < interval) return;
        if (voiceSent is { } previous && previous.Key == key) voiceRetries++; else voiceRetries = 0;
        voiceSent = (key, Environment.TickCount64);
        SendPeer(host, new("voice", Number: desired));
        // Check again shortly rather than waiting for the next health tick.
        _ = Task.Delay(interval + 50, lifetime.Token).ContinueWith(_ => Post(new("voice-local")), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
    private void UpdateVoiceRoutes()
    {
        if (coordinator is null || Identity is not { } identity) return;
        var members = coordinator.Voice.ToArray();
        var host = members.FirstOrDefault(v => v.Peer == identity.Peer);
        Volatile.Write(ref voiceRoutes, new(
            members.Where(v => v.Joined && !v.Muted && v.Peer != identity.Peer).ToDictionary(v => v.Peer, v => (byte)v.Slot),
            members.Where(v => v.Joined && v.Peer != identity.Peer).Select(v => v.Peer).ToArray(),
            (byte)(host?.Slot ?? 0), host?.Joined == true, host is { Joined: true, Muted: false }));
    }
    // Called on the capture thread with a frame whose slot is not yet set.
    public void TransmitVoice(ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref voiceDesired) != 1 || !IsConnected || frame.Length > VoicePacket.HeaderBytes + VoicePacket.MaxPayload) return;
        if (Identity?.Host == true)
        {
            var routes = Volatile.Read(ref voiceRoutes);
            if (!routes.HostTalking || routes.HostSlot == 0) return;
            Span<byte> stamped = stackalloc byte[frame.Length];
            frame.CopyTo(stamped); stamped[1] = routes.HostSlot;
            foreach (var listener in routes.Listeners)
                if (negotiated.ContainsKey(listener) && peers.TryGetValue(listener, out var peer)) peer.SendVoice(stamped);
        }
        else if (hostPeer is { } host && negotiated.ContainsKey(host) && peers.TryGetValue(host, out var peer)) peer.SendVoice(frame);
    }
    // Called on a native transport thread.
    private void ReceiveVoice(string sender, PeerTransport transport, ReadOnlySpan<byte> frame)
    {
        if (!VoicePacket.TryRead(frame, out var slot, out var sequence, out var flags, out var payload)) return;
        if (!peers.TryGetValue(sender, out var current) || current != transport || !negotiated.ContainsKey(sender)) return;
        if (Identity?.Host == true)
        {
            var routes = Volatile.Read(ref voiceRoutes);
            // Only people in voice with their microphone on may speak, and none faster than real time allows.
            if (!routes.Talkers.TryGetValue(sender, out var assigned) || !AdmitVoice(sender)) return;
            Span<byte> stamped = stackalloc byte[frame.Length];
            frame.CopyTo(stamped); stamped[1] = assigned;
            if (routes.HostListening) VoiceOutput?.Receive(assigned, sequence, flags, payload);
            foreach (var listener in routes.Listeners)
                if (listener != sender && negotiated.ContainsKey(listener) && peers.TryGetValue(listener, out var peer)) peer.SendVoice(stamped);
        }
        else if (sender == hostPeer && slot != 0 && InVoice && slot != VoiceState(Identity?.Peer ?? "")?.Slot)
            VoiceOutput?.Receive(slot, sequence, flags, payload);
    }
    private bool AdmitVoice(string sender)
    {
        var budget = voiceBudgets.GetOrAdd(sender, _ => new());
        lock (budget)
        {
            var now = Environment.TickCount64;
            // 50 frames per second of speech, plus room for pre-roll and network bunching.
            budget.Tokens = Math.Min(60, budget.Tokens + (now - budget.Refilled) * 0.06); budget.Refilled = now;
            if (budget.Tokens < 1) return false;
            budget.Tokens--; return true;
        }
    }
}
