using System.Collections.Concurrent;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Watchroom.Core;

// AniDB's title dump resolves aliases locally; no library titles are sent to AniDB.
public static class AnimeTitles
{
    private sealed class CachedTitles
    {
        public SemaphoreSlim Gate { get; } = new(1);
        public TitleIndex? Index;
        public long Length;
        public long Modified;
        public void Remember(string path, TitleIndex index)
        {
            var file = new FileInfo(path);
            Length = file.Length; Modified = file.LastWriteTimeUtc.Ticks; Index = index;
        }
    }
    private sealed class TitleIndex(Dictionary<string, string[]?> titles)
    {
        // Return a copy so callers cannot mutate the shared index.
        public string[] Find(string title) => titles.TryGetValue(Key(title), out var aliases) && aliases is not null ? [.. aliases] : [];
    }
    private static readonly ConcurrentDictionary<string, CachedTitles> Caches = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static string Key(string text) => MetadataTitles.Normalize(text);
    private static TitleIndex ReadIndex(Stream xml)
    {
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 100_000_000 });
        var doc = XDocument.Load(reader);
        var titles = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        foreach (var anime in doc.Descendants("anime"))
        {
            var aliases = anime.Elements("title").Where(t => (string?)t.Attribute(XNamespace.Xml + "lang") == "en")
                .Select(t => t.Value).Distinct().Take(4).ToArray();
            foreach (var key in anime.Elements("title").Select(t => Key(t.Value)).Distinct())
                // Multiple anime sharing an alias must remain ambiguous. Repeated
                // spellings inside one anime are just one piece of evidence.
                if (!titles.TryAdd(key, aliases)) titles[key] = null;
        }
        return new(titles);
    }
    public static string[] FindAliases(Stream xml, string title) => ReadIndex(xml).Find(title);
    public static async Task<string[]> AliasesAsync(HttpClient http, string directory, string title, CancellationToken ct)
    {
        var path = Path.GetFullPath(Path.Combine(directory, "anidb-titles.xml.gz"));
        var cache = Caches.GetOrAdd(path, _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var attempted = Path.Combine(directory, "anidb-titles-attempt.txt");
            // Even failed downloads are attempted at most once each day.
            if (!File.Exists(attempted) || DateTime.UtcNow - File.GetLastWriteTimeUtc(attempted) > TimeSpan.FromDays(1))
            {
                await File.WriteAllTextAsync(attempted, "", ct);
                try
                {
                    using var response = await http.GetAsync("https://anidb.net/api/anime-titles.xml.gz", HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    using var data = new MemoryStream(); var chunk = new byte[32768]; int count;
                    while ((count = await input.ReadAsync(chunk, ct)) > 0) { if (data.Length + count > 16 * 1024 * 1024) throw new IOException("Anime title index is too large."); data.Write(chunk, 0, count); }
                    data.Position = 0; TitleIndex index;
                    using (var validation = new GZipStream(data, CompressionMode.Decompress, true)) index = ReadIndex(validation);
                    ct.ThrowIfCancellationRequested();
                    var temp = path + ".tmp";
                    try { await File.WriteAllBytesAsync(temp, data.ToArray(), ct); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
                    cache.Remember(path, index);
                }
                catch (Exception ex) when (File.Exists(path) && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException or XmlException)
                { /* A stale, validated title dump is better than losing all local aliases. */ }
            }
            var info = new FileInfo(path);
            if (!info.Exists) { cache.Index = null; return []; }
            if (cache.Index is null || cache.Length != info.Length || cache.Modified != info.LastWriteTimeUtc.Ticks)
            {
                using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress);
                var index = ReadIndex(gzip);
                ct.ThrowIfCancellationRequested();
                // Use the version observed before reading. If replaced during the
                // read, the next lookup will notice and rebuild the index.
                cache.Length = info.Length; cache.Modified = info.LastWriteTimeUtc.Ticks; cache.Index = index;
            }
            return cache.Index.Find(title);
        }
        finally { cache.Gate.Release(); }
    }
}
