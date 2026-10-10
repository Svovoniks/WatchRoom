using System.Net;
using System.Runtime.CompilerServices;

namespace Watchroom.Core;

internal static class MetadataHttp
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly AsyncLocal<(string Directory, MetadataResponseCache Cache)?> Current = new();
    private static readonly ConditionalWeakTable<HttpClient, MetadataResponseCache> LocalCaches = new();
    private static readonly Dictionary<string, MetadataRequestGate> Gates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api.themoviedb.org"] = new(100), ["api.tvmaze.com"] = new(550), ["en.wikipedia.org"] = new(200, 1)
    };
    internal static IDisposable BeginScope(LibraryStore library, bool fresh, MetadataOptions options)
    {
        var directory = library.MetadataCacheDirectory;
        var previous = Current.Value;
        if (previous?.Directory != directory) Current.Value = (directory, new MetadataResponseCache(directory, fresh,
            Wire.Serialize(new { options.Language, options.Country, options.ProviderOrder })));
        return new Scope(() => Current.Value = previous);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static async Task<byte[]> GetBytes(HttpClient http, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            Gates.TryGetValue(new Uri(url).Host, out var gate);
            TimeSpan delay;
            {
                using var lease = gate is null ? null : await gate.Enter(ct);
                var started = System.Diagnostics.Stopwatch.StartNew();
                HttpResponseMessage received;
                try { received = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    MetadataDiagnostics.Record("metadata-http-failure", new { Request = MetadataDiagnostics.Request(url), Attempt = attempt + 1,
                        ElapsedMs = started.ElapsedMilliseconds, Cancelled = ct.IsCancellationRequested, Error = MetadataDiagnostics.Error(ex) });
                    throw;
                }
                using var response = received;
                delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ??
                    TimeSpan.FromMilliseconds(response.StatusCode == HttpStatusCode.TooManyRequests ? 2000 * (attempt + 1) : 250 * (attempt + 1));
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                MetadataDiagnostics.Record("metadata-http-response", new { Request = MetadataDiagnostics.Request(url), Attempt = attempt + 1,
                    Status = (int)response.StatusCode, ElapsedMs = started.ElapsedMilliseconds,
                    ContentType = response.Content.Headers.ContentType?.MediaType, ContentLength = response.Content.Headers.ContentLength,
                    RetryDelayMs = delay.TotalMilliseconds, WillRetry = attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) });
                if (response.StatusCode == HttpStatusCode.TooManyRequests) gate?.Backoff(delay);
                if (attempt >= 2 || response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    try
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("Metadata response exceeds 10 MB.");
                        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
                        var chunk = new byte[32768]; int read;
                        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                        { if (buffer.Length + read > MaximumBytes) throw new IOException("Metadata response exceeds 10 MB."); buffer.Write(chunk, 0, read); }
                        return buffer.ToArray();
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                    {
                        MetadataDiagnostics.Record("metadata-http-failure", new { Request = MetadataDiagnostics.Request(url), Attempt = attempt + 1,
                            Status = (int)response.StatusCode, ElapsedMs = started.ElapsedMilliseconds, Cancelled = ct.IsCancellationRequested,
                            Error = MetadataDiagnostics.Error(ex) });
                        throw;
                    }
                }
            }
            // Dispose responses and release concurrency slots before sleeping.
            await Task.Delay(delay, ct);
        }
    }
    public static Task<string> GetString(HttpClient http, string url, CancellationToken ct) =>
        (Current.Value?.Cache ?? LocalCaches.GetValue(http, _ => new())).Get(http, url, ct);
    public static async Task<byte[]> GetImage(HttpClient http, string url, CancellationToken ct)
    {
        var bytes = await GetBytes(http, url, ct);
        if (!(bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216) &&
            !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
        {
            var error = new IOException("Provider returned an unsupported image.");
            MetadataDiagnostics.Record("metadata-image-failure", new { Request = MetadataDiagnostics.Request(url), Bytes = bytes.Length, Error = MetadataDiagnostics.Error(error) });
            throw error;
        }
        return bytes;
    }
}
