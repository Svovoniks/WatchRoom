namespace BlackPearl.Core;

public sealed partial class LibraryStore
{
    private readonly object retentionGate = new();
    private long lastRetentionCheck;
    public bool ExpireTmdbContent(bool force = false)
    {
        lock (retentionGate)
        {
            var now = Wire.Now;
            if (!force && now - lastRetentionCheck < (long)TimeSpan.FromHours(1).TotalMilliseconds) return false;
            using var db = Open(); using var transaction = db.BeginTransaction();
            var changed = false;
            foreach (var table in new[] { "media", "shows", "seasons" })
            {
                var rows = new List<(string Id, string Json)>();
                using (var read = db.CreateCommand())
                {
                    read.Transaction = transaction; read.CommandText = "SELECT id,json FROM " + table;
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                }
                foreach (var row in rows)
                {
                    var json = TmdbRetention.Expire(row.Json, now, out var expired);
                    if (!expired) continue;
                    using var update = db.CreateCommand(); update.Transaction = transaction;
                    update.CommandText = "UPDATE " + table + " SET json=$json WHERE id=$id";
                    update.Parameters.AddWithValue("$json", json); update.Parameters.AddWithValue("$id", row.Id); update.ExecuteNonQuery();
                    changed = true;
                }
            }
            transaction.Commit();
            if (Setting("metadata-outstanding-v2") is { } issuesJson)
            {
                try
                {
                    var issues = Wire.Read<MetadataIssues.Issue[]>(issuesJson);
                    var recent = issues.Where(i => i.At > now - (long)TmdbRetention.MaximumAge.TotalMilliseconds).ToArray();
                    if (recent.Length != issues.Length) Setting("metadata-outstanding-v2", Wire.Serialize(recent));
                }
                catch (System.Text.Json.JsonException) { Setting("metadata-outstanding-v2", "[]"); }
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection).DataSource))!;
            ExpireRepairBackups(directory, now);
            PruneTmdbImages(directory, now);
            var responses = Path.Combine(directory, "metadata-http-cache");
            if (Directory.Exists(responses)) foreach (var file in new DirectoryInfo(responses).EnumerateFiles("*.json"))
                if (file.LastWriteTimeUtc <= DateTime.UtcNow.AddDays(-8)) DeleteCacheFile(file.FullName);
            foreach (var name in new[] { "metadata-fetch-report.json", "metadata-identity-repair-v1.json", "grouping-repair-v1.json" })
            {
                var report = Path.Combine(directory, name);
                if (File.Exists(report) && File.GetLastWriteTimeUtc(report) <= DateTime.UtcNow - TmdbRetention.MaximumAge) DeleteCacheFile(report);
            }
            lastRetentionCheck = now;
            return changed;
        }
    }
    private static void DeleteCacheFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MetadataDiagnostics.Record("metadata-retention-failure", new { Operation = "delete-cache", Error = MetadataDiagnostics.Error(ex) }); }
    }
    private static void ExpireRepairBackups(string directory, long now)
    {
        // Keep recovery files and local data; expire only provider snapshots in
        // the two SQLite backups created by our own repair migrations.
        foreach (var name in new[] { "library-before-metadata-repair-v1.db", "library-before-grouping-repair-v1.db" })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var backup = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite }.ToString());
                backup.Open(); using var transaction = backup.BeginTransaction();
                var items = new List<MediaItem>();
                using (var read = backup.CreateCommand())
                {
                    read.Transaction = transaction; read.CommandText = "SELECT json FROM media";
                    using var reader = read.ExecuteReader(); while (reader.Read()) items.Add(TmdbRetention.TrackLegacy(Wire.Read<MediaItem>(reader.GetString(0))));
                }
                foreach (var item in items)
                {
                    var json = TmdbRetention.Expire(Wire.Serialize(item), now, out var changed);
                    if (!changed) continue;
                    using var update = backup.CreateCommand(); update.Transaction = transaction;
                    update.CommandText = "UPDATE media SET json=$json WHERE id=$id";
                    update.Parameters.AddWithValue("$json", json); update.Parameters.AddWithValue("$id", item.Id); update.ExecuteNonQuery();
                }
                foreach (var table in new[] { "shows", "seasons" })
                {
                    using var schema = backup.CreateCommand(); schema.Transaction = transaction;
                    schema.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name"; schema.Parameters.AddWithValue("$name", table);
                    if ((long)schema.ExecuteScalar()! == 0) continue;
                    var rows = new List<(string Id, string Json)>();
                    using (var read = backup.CreateCommand())
                    {
                        read.Transaction = transaction; read.CommandText = "SELECT id,json FROM " + table;
                        using var reader = read.ExecuteReader(); while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                    }
                    foreach (var row in rows)
                    {
                        var node = System.Text.Json.Nodes.JsonNode.Parse(row.Json)!.AsObject();
                        var fields = new Dictionary<string, TmdbField>();
                        foreach (var item in items.Where(x => (table == "shows" ? x.ShowId : x.SeasonId) == row.Id))
                        {
                            var candidates = table == "shows" ? TmdbRetention.Transfer(item.TmdbFields, ("series", "title"), ("kind", "kind"), ("year", "year"), ("overview", "overview"), ("seriesPoster", "poster"))
                                : TmdbRetention.Transfer(item.TmdbFields, ("seasonTitle", "title"), ("seasonOverview", "overview"), ("seasonPoster", "poster"));
                            foreach (var field in candidates ?? []) fields.TryAdd(field.Key, field.Value);
                        }
                        if (node["tmdbFields"] is null) node["tmdbFields"] = System.Text.Json.JsonSerializer.SerializeToNode(fields, Wire.Json);
                        var json = TmdbRetention.Expire(node.ToJsonString(Wire.Json), now, out var changed);
                        if (!changed) continue;
                        using var update = backup.CreateCommand(); update.Transaction = transaction;
                        update.CommandText = "UPDATE " + table + " SET json=$json WHERE id=$id"; update.Parameters.AddWithValue("$json", json); update.Parameters.AddWithValue("$id", row.Id); update.ExecuteNonQuery();
                    }
                }
                transaction.Commit();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { MetadataDiagnostics.Record("metadata-retention-failure", new { Operation = "repair-backup", Error = MetadataDiagnostics.Error(ex) }); }
        }
    }
    private static void PruneTmdbImages(string directory, long now)
    {
        // Only the app-owned download directory is eligible for cleanup.
        var posters = Path.Combine(directory, "posters");
        if (!Directory.Exists(posters)) return;
        foreach (var file in new DirectoryInfo(posters).EnumerateFiles())
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^(?:(?:tv|movie)-\d+(?:-s\d+(?:-e\d+)?)?-w(?:500|780)-[A-Fa-f0-9]{12}(?:-[A-Fa-f0-9]{32})?\.jpg|tmdb-url-[A-Fa-f0-9]{64}(?:-[A-Fa-f0-9]{32})?\.(?:jpg|png))$")) continue;
            if (new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() <= now - (long)TmdbRetention.MaximumAge.TotalMilliseconds)
                DeleteCacheFile(file.FullName);
        }
    }
}
