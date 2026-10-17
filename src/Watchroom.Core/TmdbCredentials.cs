using System.Text.RegularExpressions;

namespace Watchroom.Core;

// Distributed application credentials are extractable from the binary. Keep
// the input file outside the checkout and never include the value in diagnostics.
public static class TmdbCredentials
{
    private static readonly Lazy<string?> Bundled = new(() =>
    {
        using var stream = typeof(TmdbCredentials).Assembly.GetManifestResourceStream("Watchroom.TmdbKey");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return TryNormalize(reader.ReadToEnd(), out var key) && key.Length > 0 ? key : null;
    });
    public const string Attribution = "This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.";
    public const string StreamingAttribution = "Streaming-provider data supplied by JustWatch via TMDB.";
    public static string DefaultStatus => HasBundledKey ? "Using the bundled WatchRoom key." : "This build needs a TMDB key.";
    public static bool HasBundledKey => Bundled.Value is not null;
    public static string? Resolve(string? userKey) => Select(userKey, Bundled.Value);
    // A malformed personal key is ignored rather than thrown, so TVmaze/Wikipedia and the bundled key keep working.
    internal static string? Select(string? userKey, string? bundledKey) =>
        TryNormalize(userKey, out var user) && user.Length > 0 ? user : TryNormalize(bundledKey, out var bundled) && bundled.Length > 0 ? bundled : null;
    /// <summary>Trims whitespace/BOM and a leading "Bearer " prefix. True with "" for a blank value (use the bundled key),
    /// true with the cleaned key when well formed, false with "" when the value cannot be a TMDB credential.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        value = value?.Trim().Trim('\uFEFF').Trim();
        if (string.IsNullOrEmpty(value)) return true;
        value = Regex.Replace(value, @"\ABearer(?:\s+|\z)", "", RegexOptions.IgnoreCase).Trim();
        if (value.Length == 0 || value.Length > 8192 || !Regex.IsMatch(value, @"\A[A-Za-z0-9._-]+\z")) return false;
        normalized = value; return true;
    }
}
