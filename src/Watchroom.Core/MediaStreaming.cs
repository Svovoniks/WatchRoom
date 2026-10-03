namespace Watchroom.Core;

public static class MediaStreaming
{
    public const int ChunkBytes = 32768;
    // One MiB in flight sustains movie bitrates across ordinary WAN round trips.
    public const int Window = 32;
    public const int NetworkCacheMs = 1500;
}
