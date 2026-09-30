using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Watchroom.Core;

// AniDB's title dump resolves aliases locally; no library titles are sent to AniDB.
public static class AnimeTitles
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static string Key(string text) => Regex.Replace(text, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    public static string[] FindAliases(Stream xml, string title)
    {
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 100_000_000 });
        var doc = XDocument.Load(reader);
        var matches = doc.Descendants("anime").Where(a => a.Elements("title").Any(t => Key(t.Value) == Key(title))).ToArray();
        if (matches.Length != 1) return [];
        return matches[0].Elements("title").Where(t => (string?)t.Attribute(XNamespace.Xml + "lang") == "en").Select(t => t.Value).Distinct().Take(4).ToArray();
    }
    public static async Task<string[]> AliasesAsync(HttpClient http, string directory, string title, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "anidb-titles.xml.gz");
            var attempted = Path.Combine(directory, "anidb-titles-attempt.txt");
            // Even failed downloads are attempted at most once each day.
            if (!File.Exists(attempted) || DateTime.UtcNow - File.GetLastWriteTimeUtc(attempted) > TimeSpan.FromDays(1))
            {
                await File.WriteAllTextAsync(attempted, "", ct);
                using var response = await http.GetAsync("https://anidb.net/api/anime-titles.xml.gz", HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var data = new MemoryStream(); var chunk = new byte[32768]; int count;
                while ((count = await input.ReadAsync(chunk, ct)) > 0) { if (data.Length + count > 16 * 1024 * 1024) throw new IOException("Anime title index is too large."); data.Write(chunk, 0, count); }
                data.Position = 0; using (var validation = new GZipStream(data, CompressionMode.Decompress, true)) FindAliases(validation, title);
                var temp = path + ".tmp";
                try { await File.WriteAllBytesAsync(temp, data.ToArray(), ct); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            if (!File.Exists(path)) return [];
            using var file = File.OpenRead(path); using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return FindAliases(gzip, title);
        }
        finally { Gate.Release(); }
    }
}
