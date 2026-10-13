using System.Globalization;

namespace Watchroom.Core;

public static class StreamingBufferOptions
{
    public const string SettingKey = "streamingBufferSeconds";
    public const int DefaultSeconds = 30;
    public const int MinimumSeconds = 2;
    public const int MaximumSeconds = 60;

    public static bool TryParse(string? value, out int seconds) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) &&
        seconds is >= MinimumSeconds and <= MaximumSeconds;

    public static int Load(LibraryStore library) =>
        TryParse(library.Setting(SettingKey), out var seconds) ? seconds : DefaultSeconds;

    // VLC owns the time-based forward buffer; the bridge's byte cache is for
    // repeated range reads and seeks, and does not limit VLC's retained data.
    public static int CacheMs(int seconds) => Math.Clamp(seconds, MinimumSeconds, MaximumSeconds) * 1000;
}
