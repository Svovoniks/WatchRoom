namespace Watchroom.Core;

// Room timestamps are UTC, but elapsed playback time must not depend on changes
// to the operating system clock (VM clock synchronization, NTP, manual changes).
public sealed class ServerClock
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<long, long> pending = [];
    private readonly List<(long At, long Rtt, long Server)> samples = [];
    private long anchor, serverAnchor;
    public ServerClock(TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        anchor = this.time.GetTimestamp(); serverAnchor = this.time.GetUtcNow().ToUnixTimeMilliseconds();
    }
    public long Now { get { lock (gate) return serverAnchor + Elapsed(anchor, time.GetTimestamp()); } }
    public long OffsetMs => Now - time.GetUtcNow().ToUnixTimeMilliseconds();
    public long RecentRttMs { get { lock (gate) return samples.Where(s => Elapsed(s.At, time.GetTimestamp()) <= 10000).Select(s => s.Rtt).DefaultIfEmpty(0).Max(); } }
    internal void Initialize(long hostUnixMs)
    {
        lock (gate) { anchor = time.GetTimestamp(); serverAnchor = hostUnixMs; samples.Clear(); pending.Clear(); }
    }
    private long Elapsed(long from, long to) => (long)time.GetElapsedTime(from, to).TotalMilliseconds;
    public void Sent(long id)
    {
        lock (gate)
        {
            var now = time.GetTimestamp();
            foreach (var old in pending.Where(p => Elapsed(p.Value, now) > 10000).Select(p => p.Key).ToArray()) pending.Remove(old);
            pending[id] = now;
        }
    }
    public bool Receive(long id, long serverUnixMs)
    {
        lock (gate)
        {
            if (!pending.Remove(id, out var sent)) return false;
            var now = time.GetTimestamp(); var rtt = Elapsed(sent, now);
            if (rtt < 0 || rtt > 10000 || serverUnixMs <= 0) return false;
            samples.RemoveAll(s => Elapsed(s.At, now) > 10000);
            samples.Add((now, rtt, serverUnixMs + rtt / 2));
            var best = samples.OrderBy(s => s.Rtt).ThenByDescending(s => s.At).First();
            var before = serverAnchor + Elapsed(anchor, now);
            anchor = now; serverAnchor = best.Server + Elapsed(best.At, now);
            if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("clock", new { id, rtt, selectedRtt = best.Rtt, sampleAgeMs = Elapsed(best.At, now), correctionMs = serverAnchor - before, serverMs = serverAnchor });
            return true;
        }
    }
}
