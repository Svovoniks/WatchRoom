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
    private long diagnosticReads;
    private readonly CancellationTokenSource shutdown = new();
    private int stopped;
    public Uri? Url { get; private set; }
    public async Task<Uri> StartAsync(IMediaSource media)
    {
        source = new CachedMediaSource(media);
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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, shutdown.Token);
        using var abort = shutdown.Token.Register(context.Abort);
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
            // A bounded pipeline plus a per-movie cache amortizes nearby seeks.
            var inflight = new Queue<Task<byte[]>>(); var offset = start;
            while (offset <= end || inflight.Count > 0)
            {
                while (offset <= end && inflight.Count < MediaStreaming.Window)
                {
                    int count = (int)Math.Min(MediaStreaming.ChunkBytes, end - offset + 1);
                    inflight.Enqueue(ReadRange(offset, count, cancellation.Token)); offset += count;
                }
                var bytes = await inflight.Dequeue();
                await context.Response.Body.WriteAsync(bytes, cancellation.Token);
            }
        }
        catch (Exception) { context.Abort(); }
    }
    private async Task<byte[]> ReadRange(long offset, int count, CancellationToken ct)
    {
        if (!PlaybackDiagnostics.Enabled) return await source!.ReadAsync(offset, count, ct);
        var start = Environment.TickCount64;
        try
        {
            var bytes = await source!.ReadAsync(offset, count, ct);
            var elapsedMs = Environment.TickCount64 - start;
            if (elapsedMs >= 250 || Interlocked.Increment(ref diagnosticReads) % 64 == 1)
                PlaybackDiagnostics.Record("range-read", new { offset, count, bytes = bytes.Length, elapsedMs });
            return bytes;
        }
        catch (Exception ex)
        {
            PlaybackDiagnostics.Record("range-error", new { offset, count, elapsedMs = Environment.TickCount64 - start, error = ex.GetType().Name });
            throw;
        }
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
    public void CancelPendingReads()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) shutdown.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        CancelPendingReads();
        var server = Interlocked.Exchange(ref app, null);
        if (server is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await server.StopAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { await server.DisposeAsync(); }
    }
}
