namespace Watchroom.Core;

public static class MediaStreaming
{
    public const int ChunkBytes = 32768;
    // Short binary frames avoid JSON/base64 expansion and long SCTP fragments
    // sharing congestion control with playback commands on a relay connection.
    public const int FrameBytes = 8192;
    // One MiB in flight sustains movie bitrates across ordinary WAN round trips.
    public const int Window = 32;
    public const int NetworkCacheMs = 1500;
    // Select once per movie; changing VLC's cache during playback reloads input.
    // Preserve the ordinary buffer on fast links and absorb measured WAN jitter.
    public static int NetworkCacheForRtt(long rttMs) => (int)Math.Clamp(Math.Max(NetworkCacheMs, Math.Min(rttMs, 10000) * 4), NetworkCacheMs, 5000);
}
