using System.Net;
using System.Runtime.CompilerServices;

namespace Watchroom.Core;

internal static class MetadataHttp
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private static readonly MetadataRequestGate WikipediaGate = new(350, 1);
    private static readonly AsyncLocal<(string Directory, MetadataResponseCache Cache)?> Current = new();
    private static readonly ConditionalWeakTable<HttpClient, MetadataResponseCache> LocalCaches = new();
    private static readonly Dictionary<string, MetadataRequestGate> Gates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api.themoviedb.org"] = new(100), ["api.tvmaze.com"] = new(550)
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
    // Clients are created per fetch, but share one pooled handler so connections and TLS sessions are reused.
    private static readonly SocketsHttpHandler SharedHandler = new() { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    internal static HttpClient CreateClient()
    {
        var client = new HttpClient(SharedHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Watchroom/0.3 (+https://github.com/Svovoniks/WatchRoom; metadata)");
        return client;
    }
    internal static bool ArtworkHost(Uri uri) => uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.EndsWith(".tvmaze.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "upload.wikimedia.org" || uri.Host == "image.tmdb.org");
    public static async Task<byte[]> GetBytes(HttpClient http, string url, CancellationToken ct, bool image = false)
    {
        var originalUrl = url; var redirects = 0;
        for (var attempt = 0; ; attempt++)
        {
            var uri = new Uri(url);
            if (image && !ArtworkHost(uri)) throw new IOException("Unsupported artwork host or redirect.");
            Gates.TryGetValue(uri.Host, out var gate);
            if (uri.Host.EndsWith(".wikipedia.org", StringComparison.OrdinalIgnoreCase)) gate = WikipediaGate;
            TimeSpan delay;
            {
                using var lease = gate is null ? null : await gate.Enter(ct);
                var started = System.Diagnostics.Stopwatch.StartNew();
                HttpResponseMessage received;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    received = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                }
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
                    Location = response.Headers.Location is { } location ? MetadataDiagnostics.Request(new Uri(uri, location).AbsoluteUri) : null,
                    RetryDelayMs = delay.TotalMilliseconds, WillRetry = attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) });
                if (response.StatusCode == HttpStatusCode.TooManyRequests) gate?.Backoff(delay, adapt: true);
                if (image && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is null || ++redirects > 5) throw new IOException("Artwork redirect is missing or exceeds five hops.");
                    var target = new Uri(uri, response.Headers.Location);
                    if (!ArtworkHost(target)) throw new IOException("Unsupported artwork redirect host or scheme.");
                    MetadataDiagnostics.Record("metadata-artwork-redirect", new { From = MetadataDiagnostics.Request(url), To = MetadataDiagnostics.Request(target.AbsoluteUri), Hop = redirects });
                    url = target.AbsoluteUri; attempt--; continue;
                }
                if (attempt >= 2 || response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    try
                    {
                        response.EnsureSuccessStatusCode();
                        var maximum = image ? MaximumImageBytes : MaximumBytes;
                        if (image && response.RequestMessage?.RequestUri is { } finalUri && !ArtworkHost(finalUri)) throw new IOException("Unsupported artwork redirect host or scheme.");
                        if (response.Content.Headers.ContentLength > maximum) throw new IOException(image ? "Artwork response exceeds 20 MB." : "Metadata response exceeds 10 MB.");
                        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
                        var chunk = new byte[32768]; int read;
                        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                        { if (buffer.Length + read > maximum) throw new IOException(image ? "Artwork response exceeds 20 MB." : "Metadata response exceeds 10 MB."); buffer.Write(chunk, 0, read); }
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
            url = originalUrl; redirects = 0;
        }
    }
    public static Task<string> GetString(HttpClient http, string url, CancellationToken ct) =>
        (Current.Value?.Cache ?? LocalCaches.GetValue(http, _ => new())).Get(http, url, ct);
    public static async Task<byte[]> GetImage(HttpClient http, string url, CancellationToken ct)
    {
        try { return await GetImageCore(http, url, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        { MetadataDiagnostics.Record("metadata-image-failure", new { Request = MetadataDiagnostics.Request(url), Error = MetadataDiagnostics.Error(ex) }); throw; }
    }
    private static async Task<byte[]> GetImageCore(HttpClient http, string url, CancellationToken ct)
    {
        var bytes = await GetBytes(http, url, ct, image: true);
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            using var data = SkiaSharp.SKData.CreateCopy(bytes);
            using var codec = SkiaSharp.SKCodec.Create(data);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 32_000_000 ||
                codec.Info.Width > 8192 || codec.Info.Height > 8192) throw new IOException("Artwork has invalid or excessive dimensions.");
            using var bitmap = SkiaSharp.SKBitmap.Decode(codec);
            if (bitmap is null) throw new IOException("Provider returned an invalid WebP image.");
            using var png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            if (png is null || png.Size > MaximumImageBytes) throw new IOException("Converted artwork exceeds 20 MB.");
            bytes = png.ToArray();
            MetadataDiagnostics.Record("metadata-image-converted", new { Request = MetadataDiagnostics.Request(url), Format = "webp", codec.Info.Width, codec.Info.Height });
        }
        if (!(bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216) &&
            !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
        {
            var error = new IOException("Provider returned an unsupported image.");
            throw error;
        }
        using var encoded = SkiaSharp.SKData.CreateCopy(bytes);
        using var imageCodec = SkiaSharp.SKCodec.Create(encoded);
        if (imageCodec is null || imageCodec.Info.Width <= 0 || imageCodec.Info.Height <= 0 || imageCodec.Info.Width > 8192 || imageCodec.Info.Height > 8192 ||
            (long)imageCodec.Info.Width * imageCodec.Info.Height > 32_000_000) throw new IOException("Artwork has invalid or excessive dimensions.");
        return bytes;
    }
}
