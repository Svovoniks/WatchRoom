using System.Net;
using System.Text;

namespace Watchroom.Core;

internal static class MetadataHttp
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    public static async Task<byte[]> GetBytes(HttpClient http, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMilliseconds(250 * (attempt + 1));
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 5000)), ct); continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new IOException("Metadata response exceeds 10 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
            var chunk = new byte[32768]; int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            { if (buffer.Length + read > MaximumBytes) throw new IOException("Metadata response exceeds 10 MB."); buffer.Write(chunk, 0, read); }
            return buffer.ToArray();
        }
    }
    public static async Task<string> GetString(HttpClient http, string url, CancellationToken ct) => Encoding.UTF8.GetString(await GetBytes(http, url, ct));
    public static async Task<byte[]> GetImage(HttpClient http, string url, CancellationToken ct)
    {
        var bytes = await GetBytes(http, url, ct);
        if (!(bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216) &&
            !(bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new IOException("Provider returned an unsupported image.");
        return bytes;
    }
}
