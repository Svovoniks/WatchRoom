using System.Collections.Concurrent;
using System.Text.Json;

namespace Watchroom.Core;

// One cache serves all provider clients in a fetch. Only successful JSON is
// persisted; filenames are hashes and never contain queries or credentials.
internal sealed class MetadataResponseCache(string? directory = null, bool fresh = false, string variant = "", TimeProvider? clock = null)
{
    private sealed record Entry(DateTimeOffset Expires, string Body);
    private readonly ConcurrentDictionary<string, Entry> memory = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> pending = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private int writes;
    public async Task<string> Get(HttpClient http, string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var aliasResponse = url.Contains("/alternative_titles", StringComparison.Ordinal) || new Uri(url).AbsolutePath.EndsWith("/akas");
        var key = LibraryIdentity.Hash(url + "\n" + http.DefaultRequestHeaders.Authorization + "\n" + variant);
        var gate = pending.GetOrAdd(key, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            if (memory.TryGetValue(key, out var cached) && cached.Expires > time.GetUtcNow()) return cached.Body;
            var path = directory is null ? null : Path.Combine(directory, key + ".json");
            if (!fresh && path is not null)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length <= 24 * 1024 * 1024)
                    {
                        var entry = JsonSerializer.Deserialize<Entry>(await File.ReadAllTextAsync(path, ct));
                        if (entry?.Body is not null && entry.Expires > time.GetUtcNow() && Cacheable(entry.Body, aliasResponse))
                        { Remember(key, entry); return entry.Body; }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            var body = System.Text.Encoding.UTF8.GetString(await MetadataHttp.GetBytes(http, url, ct));
            // Parse before storing: malformed and provider-error responses must
            // never poison retries. Empty searches are deliberately not cached.
            if (!Cacheable(body, aliasResponse)) return body;
            var ttl = aliasResponse ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1);
            var saved = new Entry(time.GetUtcNow() + ttl, body); Remember(key, saved);
            if (path is not null)
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(directory!);
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(saved), ct);
                    File.Move(temp, path, true);
                    if (Interlocked.Increment(ref writes) % 64 == 1) Prune();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            }
            return body;
        }
        finally { gate.Release(); }
    }
    private static bool Cacheable(string body, bool allowEmpty)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Array) return allowEmpty || root.GetArrayLength() > 0;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) || root.TryGetProperty("errors", out _) ||
            root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return false;
        return allowEmpty || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 0;
    }
    private void Remember(string key, Entry entry)
    {
        // Bound per-run memory even for very large libraries.
        if (memory.Count >= 256 || memory.Values.Sum(x => (long)x.Body.Length * 2) + entry.Body.Length * 2L > 32 * 1024 * 1024) memory.Clear();
        memory[key] = entry;
    }
    private void Prune()
    {
        var files = new DirectoryInfo(directory!).EnumerateFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        for (var i = 0; i < files.Length; i++)
        {
            bytes += files[i].Length;
            if (i >= 512 || bytes > 64 * 1024 * 1024 || time.GetUtcNow().UtcDateTime - files[i].LastWriteTimeUtc > TimeSpan.FromDays(8)) files[i].Delete();
        }
    }
}
