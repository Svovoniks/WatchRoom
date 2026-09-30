using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Watchroom.Core;

public sealed class MediaBridge : IAsyncDisposable
{
    private WebApplication? app;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private IMediaSource? source;
    public Uri? Url { get; private set; }
    public async Task<Uri> StartAsync(IMediaSource media)
    {
        source = media;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        app = builder.Build();
        app.MapMethods("/{secret}/media", ["GET", "HEAD"], Serve);
        await app.StartAsync();
        Url = new Uri(app.Urls.Single() + $"/{token}/media");
        return Url;
    }
    private async Task Serve(HttpContext context)
    {
        if (!string.Equals(context.Request.RouteValues["secret"]?.ToString(), token, StringComparison.Ordinal) || source is null)
        { context.Response.StatusCode = 404; return; }
        var length = source.Media.Length;
        if (!TryRange(context.Request.Headers.Range.ToString(), length, out var start, out var end))
        { context.Response.StatusCode = 416; context.Response.Headers.ContentRange = $"bytes */{length}"; return; }
        var partial = context.Request.Headers.ContainsKey("Range");
        context.Response.StatusCode = partial ? 206 : 200;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = end - start + 1;
        if (partial) context.Response.Headers.ContentRange = $"bytes {start}-{end}/{length}";
        if (context.Request.Method == "HEAD") return;
        try
        {
            // A bounded pipeline keeps several requests in flight without caching the movie.
            var inflight = new Queue<Task<byte[]>>(); var offset = start;
            while (offset <= end || inflight.Count > 0)
            {
                while (offset <= end && inflight.Count < 8)
                {
                    int count = (int)Math.Min(32768, end - offset + 1);
                    inflight.Enqueue(source.ReadAsync(offset, count, context.RequestAborted)); offset += count;
                }
                var bytes = await inflight.Dequeue();
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            }
        }
        catch (Exception) { context.Abort(); }
    }
    public static bool TryRange(string? value, long length, out long start, out long end)
    {
        start = 0; end = length - 1;
        if (length <= 0) return false;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) return false;
        var parts = value[6..].Split('-');
        if (parts.Length != 2) return false;
        if (parts[0] == "")
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix); return true;
        }
        if (!long.TryParse(parts[0], out start) || start < 0 || start >= length) return false;
        if (parts[1] != "" && (!long.TryParse(parts[1], out end) || end < start)) return false;
        end = Math.Min(end, length - 1); return true;
    }
    public async ValueTask DisposeAsync() { if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); } }
}
