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
    public static bool IsWithin(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        var full = System.IO.Path.GetFullPath(path);
        return full.Equals(root, comparison) || full.StartsWith(System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar, comparison);
    }
    public void Prune(IEnumerable<LibraryFolder> folders, IEnumerable<string>? exclusions = null)
    {
        var roots = folders.ToArray(); var excluded = exclusions?.ToArray() ?? [];
        var removed = All().Where(x => !roots.Any(f => IsWithin(x.Path, f.Path)) || excluded.Any(f => IsWithin(x.Path, f))).ToArray();
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var item in removed)
        {
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM media WHERE id=$id"; command.Parameters.AddWithValue("$id", item.Id); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public async Task<List<MediaItem>> ScanAsync(IEnumerable<LibraryFolder> folders, IProgress<string>? progress = null, CancellationToken cancellation = default, IEnumerable<string>? exclusions = null)
    {
        var roots = folders.ToArray(); var excluded = exclusions?.ToArray() ?? [];
        return await Task.Run(() =>
        {
            Prune(roots, excluded);
            var old = All().ToDictionary(x => x.Id); var seen = new HashSet<string>();
            foreach (var folder in roots)
            {
                if (!Directory.Exists(folder.Path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                foreach (var path in Directory.EnumerateFiles(folder.Path, "*", options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (excluded.Any(f => IsWithin(path, f))) continue;
                    if (!FileNames.Extensions.Contains(System.IO.Path.GetExtension(path))) continue;
                    var parsed = FileNames.Parse(path, folder.Kind); seen.Add(parsed.Id);
                    var item = old.TryGetValue(parsed.Id, out var previous) ? previous with { Available = true, Kind = previous.MetadataKind ?? parsed.Kind, Series = previous.MetadataType == "movie" ? null : previous.MetadataType == "tv" || previous.Matched ? previous.Series : parsed.Series ?? previous.Series, Season = previous.MetadataType == "movie" ? null : parsed.Season ?? previous.Season, Episode = previous.MetadataType == "movie" ? null : parsed.Episode ?? previous.Episode, SeriesPoster = parsed.SeriesPoster ?? previous.SeriesPoster, SeasonPoster = parsed.SeasonPoster ?? previous.SeasonPoster, Poster = previous.Matched && File.Exists(previous.Poster) ? previous.Poster : parsed.Poster ?? (File.Exists(previous.Poster) ? previous.Poster : null) } : parsed;
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
        else if (kind == "Anime" || kind == "Mixed" && Regex.IsMatch(System.IO.Path.GetFileName(path), @"^\[[^\]]+\]"))
        {
            var anime = Regex.Match(name, @"\s-\s(\d{1,3})(?:v\d)?\b");
            if (anime.Success) { series = name[..anime.Index].Trim(); season = 1; number = int.Parse(anime.Groups[1].Value); if (kind == "Mixed") kind = "Anime"; }
        }
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var seasonFolder = Regex.Match(System.IO.Path.GetFileName(dir), @"(?i)^Season\s*(\d+)\b|^Specials$");
        if (seasonFolder.Success)
        {
            season = seasonFolder.Groups[1].Success ? int.Parse(seasonFolder.Groups[1].Value) : 0;
            if (string.IsNullOrWhiteSpace(series)) series = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir)!);
        }
        if (episode.Success && string.IsNullOrWhiteSpace(series)) series = System.IO.Path.GetFileName(dir);
        if (series is not null) series = Regex.Replace(series, @"\s*\(?(?:19|20)\d{2}\)?\s*$", "").Trim();
        var year = Regex.Match(name, @"\b(19\d{2}|20\d{2})\b");
        var title = series ?? (year.Success ? name[..year.Index] : name);
        title = Regex.Split(title, @"(?i)\b(?:2160p|1080p|720p|480p|WEB-DL|WEBRip|BluRay|x264|x265|HEVC)\b")[0].Trim(' ', '-', '(');
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);
        string? Artwork(string folder, params string[] stems) => stems.SelectMany(stem => new[] { ".jpg", ".jpeg", ".png" }.Select(ext => System.IO.Path.Combine(folder, stem + ext))).FirstOrDefault(File.Exists);
        var localPoster = Artwork(dir, System.IO.Path.GetFileNameWithoutExtension(path), "poster", "folder", "cover");
        var seriesDir = seasonFolder.Success ? System.IO.Path.GetDirectoryName(dir)! : dir;
        var seriesPoster = series is null ? null : Artwork(seriesDir, "poster", "folder", "cover");
        var seasonPoster = series is null ? null : Artwork(dir, "poster", "folder", "cover") ?? Artwork(seriesDir, $"season{season ?? 1:00}-poster", $"season{season ?? 1}-poster");
        return new(id, path, title, kind == "Mixed" ? (series is null ? "Movie" : "Show") : kind,
            year.Success ? int.Parse(year.Value) : null, series, season, number, localPoster ?? seriesPoster,
            SeriesPoster: seriesPoster, SeasonPoster: seasonPoster);
    }
}
