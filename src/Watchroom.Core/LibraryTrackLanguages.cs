using System.Security.Cryptography;
using System.Text;

namespace Watchroom.Core;

// Detected track languages are cached per version of a file: the key hashes its path, size and modification time.
// Each cached file also records its path, so entries for files that were deleted or changed can be removed.
public sealed partial class LibraryStore
{
    private const string TrackLanguagePrefix = "trackLanguage:", TrackLanguageFilePrefix = "trackLanguageFile:";

    public static string? TrackLanguageFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks)))[..24];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static string TrackLanguageKey(string fileKey, bool audio, int id) => TrackLanguagePrefix + fileKey + ":" + (audio ? "a" : "s") + id;

    public void CacheTrackLanguage(string path, string fileKey, bool audio, int id, string? language) => SetSettings([
        new(TrackLanguageFilePrefix + fileKey, path), new(TrackLanguageKey(fileKey, audio, id), language ?? "")]);

    /// <summary>Removes cached languages of files that are gone or have changed since. Files on a drive or network
    /// share that isn't reachable right now keep theirs, so unplugging a drive doesn't throw its results away.</summary>
    public int RemoveStaleTrackLanguages()
    {
        var files = SettingsWithPrefix(TrackLanguageFilePrefix).ToDictionary(entry => entry.Key[TrackLanguageFilePrefix.Length..], entry => entry.Value);
        var keep = files.Where(entry => TrackLanguageFile(entry.Value) == entry.Key || !Reachable(entry.Value)).Select(entry => entry.Key).ToHashSet();
        var stale = SettingsWithPrefix(TrackLanguagePrefix).Keys.Where(key => !keep.Contains(key[TrackLanguagePrefix.Length..].Split(':')[0]))
            .Concat(files.Keys.Where(key => !keep.Contains(key)).Select(key => TrackLanguageFilePrefix + key)).ToArray();
        if (stale.Length == 0) return 0;
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM settings WHERE key=$k";
        var parameter = command.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var key in stale) { parameter.Value = key; command.ExecuteNonQuery(); }
        transaction.Commit();
        return stale.Length;
    }

    private static bool Reachable(string path)
    {
        try { return Path.GetPathRoot(path) is { Length: > 0 } root && Directory.Exists(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
