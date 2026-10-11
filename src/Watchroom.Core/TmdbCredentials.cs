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
        return Validate(reader.ReadToEnd());
    });
    public const string Attribution = "This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.";
    public const string StreamingAttribution = "Streaming-provider data supplied by JustWatch via TMDB.";
    public static string DefaultStatus => HasBundledKey ? "Using the bundled WatchRoom key." : "This build needs a TMDB key.";
    public static bool HasBundledKey => Bundled.Value is not null;
    public static string? Resolve(string? userKey) => Select(userKey, Bundled.Value);
    internal static string? Select(string? userKey, string? bundledKey) =>
        string.IsNullOrWhiteSpace(userKey) ? Validate(bundledKey) : Validate(userKey);
    private static string? Validate(string? value)
    {
        value = value?.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 8192 || !Regex.IsMatch(value, @"\A[A-Za-z0-9._-]+\z"))
            throw new ArgumentException("Invalid TMDB application credential format.");
        return value;
    }
}
