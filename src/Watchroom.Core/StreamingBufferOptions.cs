using System.Globalization;

namespace Watchroom.Core;

public static class StreamingBufferOptions
{
    public const string SettingKey = "streamingBufferSeconds";
    public const int DefaultSeconds = 30;
    public const int MinimumSeconds = 2;
    public const int MaximumSeconds = 60;
    public const long MaximumReadAheadBytes = 256L * 1024 * 1024;

    public static bool TryParse(string? value, out int seconds) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) &&
        seconds is >= MinimumSeconds and <= MaximumSeconds;

    public static int Load(LibraryStore library) =>
        TryParse(library.Setting(SettingKey), out var seconds) ? seconds : DefaultSeconds;

    public static int CacheMs(int seconds) => Math.Clamp(seconds, MinimumSeconds, MaximumSeconds) * 1000;

    // The setting is read-ahead, not a start threshold: VLC starts from its
    // short RTT-based network cache while the bridge keeps this much video
    // cached past the playback position. Average bitrate plus a quarter covers
    // ordinary variable-bitrate peaks.
    public static long ReadAheadBytes(int seconds, long mediaBytes, long durationMs) =>
        mediaBytes <= 0 || durationMs <= 0 ? 0 :
        (long)Math.Min(MaximumReadAheadBytes, mediaBytes * 1.25 * CacheMs(seconds) / durationMs);
}
