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
}
