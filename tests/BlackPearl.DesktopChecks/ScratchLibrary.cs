using System.IO;
using Microsoft.Data.Sqlite;

// Copies a library folder so work can be tried on real data without touching it: --copy-library <source> <destination>.
// scripts/scratch-library.ps1 runs this; see AGENTS.md. The database is copied with SQLite's online backup, so a library
// the app has open still copies consistently. Rows store artwork as absolute paths into the source folder, so the
// artwork, plugins and metadata cache are copied too and every path is rewritten to the copy: an app running on the copy
// then can never replace or delete the original's posters. Logs, update downloads and old backups are left behind.
static class ScratchLibrary
{
    static readonly string[] Folders = ["posters", "episode-thumbnails", "plugins", "metadata-http-cache"];
    static readonly string[] Files = ["anidb-titles.xml.gz"];
    // Every text column that can hold a path, by table.
    static readonly (string Table, string Column)[] Columns = [("media", "json"), ("media", "path"), ("shows", "json"), ("seasons", "json"), ("settings", "value")];

    public static int Run(string source, string destination)
    {
        source = Path.GetFullPath(source).TrimEnd('\\', '/'); destination = Path.GetFullPath(destination).TrimEnd('\\', '/');
        var database = Path.Combine(source, "library.db");
        if (!File.Exists(database)) { Console.Error.WriteLine($"No library.db in {source}."); return 1; }
        if (destination.Equals(source, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { Console.Error.WriteLine("The copy must be outside the library folder."); return 1; }
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        { Console.Error.WriteLine($"{destination} is not empty. Choose a new folder."); return 1; }
        Directory.CreateDirectory(destination);

        var copy = Path.Combine(destination, "library.db");
        using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        { from.Open(); to.Open(); from.BackupDatabase(to); }

        long bytes = 0;
        foreach (var folder in Folders.Select(name => Path.Combine(source, name)).Where(Directory.Exists))
            bytes += CopyFolder(folder, Path.Combine(destination, Path.GetFileName(folder)));
        foreach (var file in Files.Select(name => Path.Combine(source, name)).Where(File.Exists))
        { File.Copy(file, Path.Combine(destination, Path.GetFileName(file))); bytes += new FileInfo(file).Length; }

        // Paths appear plain in the path and settings columns and JSON-escaped (doubled backslashes) inside json columns.
        // Only paths inside the source folder change: media files elsewhere (M:\Shows) keep their real paths.
        string Escaped(string path) => path.Replace("\\", "\\\\");
        var rewritten = 0; var left = 0;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
        {
            db.Open();
            using var transaction = db.BeginTransaction();
            foreach (var (table, column) in Columns)
            {
                using var update = db.CreateCommand(); update.Transaction = transaction;
                update.CommandText = $"UPDATE {table} SET {column} = replace(replace({column}, $escapedOld, $escapedNew), $plainOld, $plainNew) WHERE instr({column}, $plainOld) > 0 OR instr({column}, $escapedOld) > 0";
                update.Parameters.AddWithValue("$escapedOld", Escaped(source + "\\")); update.Parameters.AddWithValue("$escapedNew", Escaped(destination + "\\"));
                update.Parameters.AddWithValue("$plainOld", source + "\\"); update.Parameters.AddWithValue("$plainNew", destination + "\\");
                rewritten += update.ExecuteNonQuery();
                // A path saved with different letter case escapes replace(), which matches case exactly.
                using var check = db.CreateCommand(); check.Transaction = transaction;
                check.CommandText = $"SELECT count(*) FROM {table} WHERE instr(lower({column}), lower($plainOld)) > 0 OR instr(lower({column}), lower($escapedOld)) > 0";
                check.Parameters.AddWithValue("$plainOld", source + "\\"); check.Parameters.AddWithValue("$escapedOld", Escaped(source + "\\"));
                left += Convert.ToInt32(check.ExecuteScalar());
            }
            transaction.Commit();
        }
        Console.WriteLine($"Copied {source} to {destination}: the database, {bytes / 1048576.0:F0} MB of artwork, plugins and cache; {rewritten} rows now point at the copy.");
        if (left > 0) { Console.Error.WriteLine($"{left} rows still name the original folder (a different letter case?). Fix this before running anything on the copy."); return 2; }
        return 0;
    }

    static long CopyFolder(string from, string to)
    {
        long bytes = 0;
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
            bytes += new FileInfo(file).Length;
        }
        return bytes;
    }
}
