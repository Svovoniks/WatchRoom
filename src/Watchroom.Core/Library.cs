using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public sealed class LibraryStore
{
    private readonly string connection;
    public LibraryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "library.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, path TEXT UNIQUE NOT NULL, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }
    public string? Setting(string key)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$k"; c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }
    public void Setting(string key, string value)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO settings VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value); c.ExecuteNonQuery();
    }
    public List<MediaItem> All()
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT json FROM media";
        using var rows = c.ExecuteReader(); var items = new List<MediaItem>();
        while (rows.Read()) items.Add(Wire.Read<MediaItem>(rows.GetString(0)));
        return items.OrderBy(x => x.DisplayTitle).ThenBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }
    public void Save(MediaItem item)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO media VALUES($id,$path,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        c.Parameters.AddWithValue("$id", item.Id); c.Parameters.AddWithValue("$path", item.Path); c.Parameters.AddWithValue("$json", Wire.Serialize(item)); c.ExecuteNonQuery();
    }
    public async Task<List<MediaItem>> ScanAsync(IEnumerable<LibraryFolder> folders, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        return await Task.Run(() =>
        {
            var old = All().ToDictionary(x => x.Id); var seen = new HashSet<string>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder.Path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                foreach (var path in Directory.EnumerateFiles(folder.Path, "*", options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!FileNames.Extensions.Contains(System.IO.Path.GetExtension(path))) continue;
                    var parsed = FileNames.Parse(path, folder.Kind); seen.Add(parsed.Id);
                    var item = old.TryGetValue(parsed.Id, out var previous) ? previous with { Available = true, Poster = previous.Matched ? previous.Poster : parsed.Poster ?? previous.Poster } : parsed;
                    Save(item); progress?.Report($"Found {seen.Count} videos · {item.Title}");
                }
            }
            foreach (var item in old.Values.Where(x => !seen.Contains(x.Id))) Save(item with { Available = false });
            return All();
        }, cancellation);
    }
}

public static partial class FileNames
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".mpeg", ".mpg", ".ts", ".m2ts", ".wmv", ".flv", ".ogv" };
    public static MediaItem Parse(string path, string kind = "Mixed")
    {
        path = System.IO.Path.GetFullPath(path);
        var identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)))[..32];
        var name = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(path), @"\[[^\]]*\]", " ");
        name = Regex.Replace(name, @"[._]+", " ").Trim();
        var episode = Regex.Match(name, @"(?i)\bS(\d{1,2})\s*E(\d{1,3})\b|\b(\d{1,2})x(\d{1,3})\b");
        int? season = null, number = null; string? series = null;
        if (episode.Success)
        {
            season = int.Parse(episode.Groups[1].Success ? episode.Groups[1].Value : episode.Groups[3].Value);
            number = int.Parse(episode.Groups[2].Success ? episode.Groups[2].Value : episode.Groups[4].Value);
            series = name[..episode.Index].Trim(' ', '-');
        }
        else if (kind == "Anime")
        {
            var anime = Regex.Match(name, @"\s-\s(\d{1,3})(?:v\d)?\b");
            if (anime.Success) { series = name[..anime.Index].Trim(); season = 1; number = int.Parse(anime.Groups[1].Value); }
        }
        var year = Regex.Match(name, @"\b(19\d{2}|20\d{2})\b");
        var title = series ?? (year.Success ? name[..year.Index] : name);
        title = Regex.Split(title, @"(?i)\b(?:2160p|1080p|720p|480p|WEB-DL|WEBRip|BluRay|x264|x265|HEVC)\b")[0].Trim(' ', '-', '(');
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var poster = new[] { System.IO.Path.ChangeExtension(path, ".jpg"), System.IO.Path.Combine(dir, "poster.jpg"), System.IO.Path.Combine(dir, "folder.jpg") }.FirstOrDefault(File.Exists);
        return new(id, path, title, kind == "Mixed" ? (series is null ? "Movie" : "Show") : kind,
            year.Success ? int.Parse(year.Value) : null, series, season, number, poster);
    }
}
