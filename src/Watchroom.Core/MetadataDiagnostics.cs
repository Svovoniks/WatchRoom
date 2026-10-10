using System.Text.RegularExpressions;

namespace Watchroom.Core;

internal static class MetadataDiagnostics
{
    private sealed record Context(string Stage, string MediaId, string Title, int? Season, int? Episode);
    private static readonly AsyncLocal<Context?> current = new();
    internal static IDisposable BeginItem(string stage, MediaItem item)
    {
        var previous = current.Value;
        current.Value = new(stage, item.Id, item.DisplayTitle, item.Season, item.Episode);
        return new Scope(() => current.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    internal static void Record(string kind, object data) => PlaybackDiagnostics.Record(kind, new { Context = current.Value, Data = data });

    // Keep URL queries, fragments and user information out of diagnostics (API keys may occur there).
    internal static string Request(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri.Scheme + "://" + uri.Authority + uri.AbsolutePath : "invalid-url";

    internal static string Redact(string text) => Regex.Replace(text, @"https?://[^\s'""<>]+", match => Request(match.Value), RegexOptions.IgnoreCase);

    internal static object Error(Exception ex) => new
    {
        Type = ex.GetType().FullName,
        Message = Redact(ex.Message),
        Stack = ex.StackTrace,
        Status = ex is HttpRequestException http ? (int?)http.StatusCode : null,
        Inner = ex.InnerException is null ? null : Error(ex.InnerException)
    };

    internal static void Failure(string stage, string? title, string? provider, Exception ex) =>
        Record("metadata-failure", new { Stage = stage, Title = title, Provider = provider, Error = Error(ex) });
}
