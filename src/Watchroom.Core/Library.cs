using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public sealed partial class LibraryStore
{
    public event Action<MediaItem>? MediaSaved;
    // Filename parsing and metadata matching cycle through more static Regex patterns than the default
    // cache of 15 holds, which would rebuild patterns for every scanned file.
    static LibraryStore() { if (Regex.CacheSize < 256) Regex.CacheSize = 256; }
    private readonly string connection;
    // Placeholders of wanted titles keep their paths under the data folder.
    internal string DataDirectory { get; }
    internal string MetadataCacheDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(new SqliteConnectionStringBuilder(connection).DataSource))!, "metadata-http-cache");
    // The desktop app defers the startup retention sweep until its library has rendered; see DeferRetention.
    public LibraryStore(string directory, bool deferRetention = false)
    {
        Directory.CreateDirectory(directory); DataDirectory = directory;
        connection = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "library.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        // WAL lets UI reads proceed during background writes and avoids a full sync per commit.
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, path TEXT UNIQUE NOT NULL, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS shows(id TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS seasons(id TEXT PRIMARY KEY, show_id TEXT NOT NULL, json TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE INDEX IF NOT EXISTS media_show_id ON media(json_extract(json, '$.showId'));";
        cmd.ExecuteNonQuery();
        // Additive migration: file IDs and saved queues remain unchanged.
        if (Setting("hierarchy-version") != "1")
        {
            LibraryFolder[] roots;
            try { roots = Wire.Read<LibraryFolder[]>(Setting("folders") ?? "[]"); }
            catch (System.Text.Json.JsonException) { roots = []; }
            foreach (var item in All())
            {
                var migrated = item;
                if (item.Series is not null && item.ShowId is null && !item.IsVirtual)
                {
                    var root = roots.Where(r => IsWithin(item.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
                    var parsed = FileNames.Parse(item.Path, item.Kind, root?.Path);
                    var parent = System.IO.Path.GetDirectoryName(item.Path) ?? "";
                    if (parsed.SeriesPath is not null && (root is not null || Regex.IsMatch(System.IO.Path.GetFileName(parent), @"(?i)Season\s*\d+|^Specials$|^\d+\s*сезон")))
                        migrated = item with { ShowId = parsed.ShowId, SeriesPath = parsed.SeriesPath };
                }
                Save(LibraryIdentity.Ensure(migrated));
            }
            Setting("hierarchy-version", "1");
        }
        if (Setting("release-folder-hierarchy-version") != "3")
        {
            RepairReleaseFolderHierarchy();
            Setting("release-folder-hierarchy-version", "3");
        }
        if (Setting("wikipedia-numbered-episode-repair") != "1")
        {
            foreach (var item in All().Where(x => x.MetadataProvider == "wikipedia" && x.MetadataType == "movie" && !x.Matched && !x.MetadataLocked))
            {
                var parsed = FileNames.Parse(item.Path);
                if (parsed.Episode is null || parsed.Series is null || LibraryIdentity.Locked(item, "Title")) continue;
                var ids = new Dictionary<string, string>(item.ProviderIds ?? []); ids.Remove("wikipedia");
                Save(item with { Series = parsed.Series, Title = parsed.Title, Kind = parsed.Kind, SeriesPath = parsed.SeriesPath,
                    Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd, ShowId = parsed.ShowId,
                    MetadataKind = null, MetadataType = null, MetadataProvider = null, MetadataId = null,
                    ProviderIds = ids, MetadataFetchedAt = 0, ArtworkFetchedAt = 0, PosterSource = null });
            }
            CleanHierarchy(); Setting("wikipedia-numbered-episode-repair", "1");
        }
        if (Setting("grouping-audit-repair") != "1") RepairGroupingAudit(directory);
        if (Setting("metadata-identity-repair") != "1") RepairMetadataIdentity(directory);
        if (Setting("adaptation-confidence-version") != "1")
        {
            foreach (var item in All().Where(x => !x.Matched && !x.MetadataLocked && MetadataTitles.IsAnimeSource(x) && x.MetadataKind == "Show"))
                Save(item with { ArtworkFetchedAt = 0, MetadataFetchedAt = 0 });
            Setting("adaptation-confidence-version", "1");
        }
        if (Setting("tmdb-retention-version") != "1")
        {
            foreach (var item in All()) Save(TmdbRetention.TrackLegacy(item));
            Setting("tmdb-retention-version", "1");
        }
        if (deferRetention) lastRetentionCheck = Wire.Now;
        else ExpireTmdbContent(force: true);
    }
    private void RepairMetadataIdentity(string directory)
    {
        var repairs = new List<object>();
        foreach (var item in All().Where(x => !x.IsVirtual && !x.IsExtra && x.Series is null && x.MetadataProvider == "wikipedia" && CanRepairGrouping(x) && !LibraryIdentity.Locked(x, "Year")))
        {
            var parsed = FileNames.Parse(item.Path, item.SourceLibraryKind ?? "Mixed");
            var wrongSourceYear = parsed.Year is { } sourceYear && item.Year is { } storedYear && Math.Abs(sourceYear - storedYear) > 1;
            var wrongFilmYear = item.Year is { } expected && WikipediaMetadata.LeadYear(item.Overview) is { } actual && Math.Abs(expected - actual) > 1;
            if (!wrongSourceYear && !wrongFilmYear) continue;
            if (repairs.Count == 0)
            {
                var backupPath = System.IO.Path.Combine(directory, "library-before-metadata-repair-v1.db");
                if (!File.Exists(backupPath))
                {
                    using var source = Open(); using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                    backup.Open(); source.BackupDatabase(backup);
                }
            }
            var updated = item with { Title = wrongSourceYear ? parsed.Title : item.Title, Year = parsed.Year ?? item.Year,
                MetadataProvider = null, MetadataId = null, MetadataKind = null, MetadataType = null, ProviderIds = [],
                Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : null,
                Poster = LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork" ? item.Poster : parsed.Poster,
                PosterSource = LibraryIdentity.Locked(item, "Poster") || item.PosterSource == "Local artwork" ? item.PosterSource : null,
                MetadataFetchedAt = 0, ArtworkFetchedAt = 0 };
            Save(updated);
            repairs.Add(new { item.Id, Reason = wrongSourceYear ? "Title number mistaken for release year" : "Automatic film metadata has contradictory release year",
                Before = new { item.Title, item.Year, item.MetadataId }, After = new { updated.Title, updated.Year } });
        }
        File.WriteAllText(System.IO.Path.Combine(directory, "metadata-identity-repair-v1.json"), Wire.Serialize(repairs));
        Setting("metadata-identity-repair", "1");
    }
    private static bool CanRepairGrouping(MediaItem item) => !item.Matched &&
        !new[] { "Title", "Kind", "Season", "Episode", "ShowId", "MetadataType", "ProviderIds" }.Any(field => LibraryIdentity.Locked(item, field));
    private void RepairGroupingAudit(string directory)
    {
        // Back up through SQLite so an active WAL is included. Keep file IDs and saved queues intact.
        var backupPath = System.IO.Path.Combine(directory, "library-before-grouping-repair-v1.db");
        if (!File.Exists(backupPath))
        {
            using var source = Open();
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
            backup.Open(); source.BackupDatabase(backup);
        }
        LibraryFolder[] roots;
        try { roots = Wire.Read<LibraryFolder[]>(Setting("folders") ?? "[]"); }
        catch (System.Text.Json.JsonException) { roots = []; }
        var repairs = new List<object>();
        foreach (var item in All().Where(x => !x.IsVirtual))
        {
            var root = roots.Where(r => IsWithin(item.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
            var parsed = FileNames.Parse(item.Path, root?.Kind ?? "Mixed", root?.Path);
            var updated = item; string? reason = null;
            if (parsed.IsExtra)
            { updated = item with { IsExtra = true, Available = File.Exists(item.Path) }; reason = "Excluded extra retained outside the main catalog"; }
            else if (CanRepairGrouping(item))
            {
                // Only repair conflicts backed by source evidence, not arbitrary provider/manual merges.
                if (parsed.Series is null && parsed.Episode is null && item.Series is not null &&
                    (MetadataMatching.HasMovieEvidence(parsed) || item.MetadataType is null &&
                        (item.Kind == "Movie" || parsed.Year is >= 1900 && item.Episode == parsed.Year)))
                {
                    updated = MetadataClassification.Apply(item, parsed.Kind, "movie") with
                    {
                        Title = parsed.Title, Year = parsed.Year ?? item.Year, SeriesPath = null,
                        MetadataKind = null, MetadataType = null, MetadataProvider = null, MetadataId = null, ProviderIds = [],
                        Poster = item.PosterSource == "Local artwork" || LibraryIdentity.Locked(item, "Poster") ? item.Poster : parsed.Poster,
                        PosterSource = item.PosterSource == "Local artwork" || LibraryIdentity.Locked(item, "Poster") ? item.PosterSource : null,
                        Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : null,
                        MetadataFetchedAt = 0, ArtworkFetchedAt = 0, SourcePart = null, SourcePartEpisode = null, SourcePartEpisodeEnd = null
                    };
                    reason = "Removed automatically inferred TV identity from a standalone film";
                }
                else if (parsed.NumberingSource == "season-folder" && parsed.ShowId is not null &&
                    (item.AbsoluteEpisode is not null || item.ShowId != parsed.ShowId))
                {
                    updated = item with { ShowId = parsed.ShowId, SeriesPath = parsed.SeriesPath,
                        Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd,
                        AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, NumberingOrder = "aired",
                        SeasonTitle = null, SeasonOverview = null, SeasonPoster = parsed.SeasonPoster,
                        EpisodeTitle = null, EpisodeOverview = null, EpisodePoster = null, EpisodeSource = null,
                        SeasonSource = null, AirDate = null, RuntimeMinutes = null, MetadataFetchedAt = 0 };
                    reason = "Restored explicit folder season and season-relative episode numbering";
                }
                updated = updated with { NumberingSource = parsed.NumberingSource, SourceLibraryKind = parsed.SourceLibraryKind };
            }
            if (updated != item) Save(updated);
            if (reason is not null) repairs.Add(new { item.Id, Reason = reason, Before = new { item.ShowId, item.Season, item.Episode, item.MetadataType },
                After = new { updated.ShowId, updated.Season, updated.Episode, updated.MetadataType } });
        }
        AttachLooseReleases(); GroupShowsByProvider(); CleanHierarchy();
        File.WriteAllText(System.IO.Path.Combine(directory, "grouping-repair-v1.json"), Wire.Serialize(repairs));
        Setting("grouping-audit-repair", "1");
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connection); db.Open();
        using var pragma = db.CreateCommand(); pragma.CommandText = "PRAGMA synchronous=NORMAL;"; pragma.ExecuteNonQuery();
        return db;
    }
    private void RepairReleaseFolderHierarchy()
    {
        LibraryFolder[] roots;
        try { roots = Wire.Read<LibraryFolder[]>(Setting("folders") ?? "[]"); }
        catch (System.Text.Json.JsonException) { roots = []; }
        var items = All();
        var remapped = new Dictionary<string, string>();
        foreach (var item in items.Where(x => x.Series is not null && !x.IsVirtual && x.SeriesPath is not null))
        {
            var oldFolderId = "show-" + LibraryIdentity.Hash(OperatingSystem.IsWindows() ? item.SeriesPath!.ToUpperInvariant() : item.SeriesPath!);
            if (item.ShowId != oldFolderId) continue; // Keep explicitly merged identities.
            var root = roots.Where(r => IsWithin(item.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
            var parsed = FileNames.Parse(item.Path, item.Kind, root?.Path);
            if (parsed.ShowId == item.ShowId) continue;
            var repaired = item with { ShowId = parsed.SeriesPath is null ? null : parsed.ShowId, SeriesPath = parsed.SeriesPath };
            if (parsed.SourcePart is not null)
                repaired = repaired with { SourcePart = parsed.SourcePart, SourcePartEpisode = parsed.SourcePartEpisode,
                    SourcePartEpisodeEnd = parsed.SourcePartEpisodeEnd, MetadataFetchedAt = 0,
                    Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd,
                    AbsoluteEpisode = parsed.AbsoluteEpisode, AbsoluteEpisodeEnd = parsed.AbsoluteEpisodeEnd };
            if (!LibraryIdentity.Locked(item, "Title") && LibraryIdentity.Normalize(item.Series!) ==
                LibraryIdentity.Normalize(System.IO.Path.GetFileName(item.SeriesPath!)) && parsed.Series is not null)
                repaired = repaired with { Series = parsed.Series, Title = parsed.Series };
            repaired = LibraryIdentity.Ensure(repaired);
            remapped[item.ShowId!] = repaired.ShowId!;
            Save(repaired);
        }
        foreach (var item in items.Where(x => x.IsVirtual && x.ShowId is not null && remapped.ContainsKey(x.ShowId)))
            Save(item with { ShowId = remapped[item.ShowId!] });
        AttachLooseReleases();
        CleanHierarchy();
    }
    // A file that went missing while another file of the same episode is still here was a replaced release, not a
    // lost episode: its record goes, and its watched flag and resume position pass to the file that remains.
    private int DropReplacedReleases()
    {
        var all = All();
        var available = all.Where(x => x.Available && !x.IsVirtual && !x.IsExtra && x.Series is not null && x.Season is not null && x.Episode is not null && x.NumberingConflict is null)
            .ToLookup(x => (LibraryIdentity.ShowKey(x), x.Season));
        var dropped = 0;
        foreach (var gone in all.Where(x => !x.Available && !x.IsVirtual && !x.IsExtra && x.Series is not null && x.Season is not null && x.Episode is not null && x.NumberingConflict is null))
        {
            var numbers = LibraryIdentity.EpisodeNumbers(gone).ToHashSet();
            var replacements = available[(LibraryIdentity.ShowKey(gone), gone.Season)].Where(x => LibraryIdentity.EpisodeNumbers(x).Any(numbers.Contains)).ToArray();
            if (replacements.Length == 0 || File.Exists(gone.Path)) continue;
            lock (writeGate)
            {
                if (Setting("watched:" + gone.Id) == "true" && !replacements.Any(x => Setting("watched:" + x.Id) == "true"))
                    Setting("watched:" + replacements[0].Id, "true");
                if (Setting("position:" + gone.Id) is { } position && replacements.All(x => Setting("position:" + x.Id) is null))
                    Setting("position:" + replacements[0].Id, position);
                using var db = Open(); using var command = db.CreateCommand();
                command.CommandText = "DELETE FROM media WHERE id=$id AND json_extract(json,'$.available')=0"; command.Parameters.AddWithValue("$id", gone.Id);
                dropped += command.ExecuteNonQuery();
            }
        }
        if (dropped > 0) CleanHierarchy();
        return dropped;
    }
    // Two folders holding the same show, such as a batch download beside a per-episode one, are one show. They
    // join when their titles match, one copy carries a provider identity, and nothing contradicts it: no differing
    // years and no differing provider identities. The identified copy lends its identity and kind to the rest.
    private void MergeDuplicateShows()
    {
        var items = All().Where(x => x.Series is not null && x.SeriesPath is not null && !x.IsExtra && !x.IsVirtual && !x.IsMusic).ToArray();
        var providers = new[] { "tmdb", "tvdb", "imdb", "tvmaze" }; var wanted = Wanted().Select(x => x.Id).ToHashSet();
        foreach (var name in items.GroupBy(x => LibraryIdentity.Normalize(x.Series!)).Where(g => g.Select(LibraryIdentity.ShowKey).Distinct().Count() > 1))
        {
            var shows = name.GroupBy(LibraryIdentity.ShowKey).Select(g => g.ToArray())
                .OrderByDescending(show => wanted.Contains(LibraryIdentity.ShowKey(show[0]))).ThenByDescending(show => show.Any(x => providers.Any(p => LibraryIdentity.ProviderId(x, p) is not null)))
                .ThenByDescending(show => show.Any(x => x.Kind == "Anime")).ThenByDescending(show => show.Length).ThenBy(show => LibraryIdentity.ShowKey(show[0]), StringComparer.Ordinal).ToArray();
            var target = shows[0];
            string? Id(MediaItem[] show, string provider) => show.Select(x => LibraryIdentity.ProviderId(x, provider)).FirstOrDefault(id => id is not null);
            // Two unidentified folders of one name may be different shows; a provider identity on one copy settles which show it is.
            if (!providers.Any(p => Id(target, p) is not null)) continue;
            int? Year(MediaItem[] show) => show.Select(x => x.Year).FirstOrDefault(year => year is not null);
            var kind = target.Select(x => x.MetadataKind ?? x.Kind).First();
            foreach (var show in shows.Skip(1))
            {
                if (providers.Any(p => Id(show, p) is { } left && Id(target, p) is { } right && left != right)) continue;
                if (Year(show) is { } year && Year(target) is { } other && year != other) continue;
                if (show.Any(x => LibraryIdentity.Locked(x, "ShowId"))) continue;
                foreach (var entry in show)
                    Save(LibraryIdentity.Ensure(entry with { ShowId = LibraryIdentity.ShowKey(target[0]), Kind = LibraryIdentity.Locked(entry, "Kind") ? entry.Kind : kind }));
            }
        }
        CleanHierarchy();
    }
    private void AttachLooseReleases()
    {
        var items = All().Where(x => x.Series is not null && !x.IsExtra && !x.IsMusic).ToArray();
        var structured = items.Where(x => x.SeriesPath is not null).GroupBy(LibraryIdentity.ShowKey).ToArray();
        foreach (var loose in items.Where(x => x.SeriesPath is null && !x.IsVirtual))
        {
            var candidates = structured.Where(group => group.Any(other =>
            {
                if (new[] { "tmdb", "tvdb", "imdb", "tvmaze" }.Any(provider =>
                    LibraryIdentity.ProviderId(loose, provider) is { } left && LibraryIdentity.ProviderId(other, provider) is { } right && left != right)) return false;
                var hasIdentity = new[] { "tmdb", "tvdb", "imdb", "tvmaze" }.Any(provider => LibraryIdentity.ProviderId(loose, provider) is { } id && id == LibraryIdentity.ProviderId(other, provider));
                return hasIdentity || LibraryIdentity.Normalize(loose.Series!) == LibraryIdentity.Normalize(other.Series!) && loose.Year == other.Year;
            })).ToArray();
            if (candidates.Length == 1 && loose.ShowId != candidates[0].Key) Save(loose with { ShowId = candidates[0].Key });
        }
        CleanHierarchy();
    }
    public string? Setting(string key)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "SELECT value FROM settings WHERE key=$k"; c.Parameters.AddWithValue("$k", key);
        return c.ExecuteScalar() as string;
    }
    public Dictionary<string, string> PlaybackSettings()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE key LIKE 'watched:%' OR key LIKE 'position:%' OR key LIKE 'played:%'";
        using var rows = command.ExecuteReader(); var result = new Dictionary<string, string>();
        while (rows.Read()) result[rows.GetString(0)] = rows.GetString(1);
        return result;
    }
    public void Setting(string key, string value)
    {
        using var db = Open(); using var c = db.CreateCommand();
        c.CommandText = "INSERT INTO settings VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        c.Parameters.AddWithValue("$k", key); c.Parameters.AddWithValue("$v", value); c.ExecuteNonQuery();
    }
    public Dictionary<string, string> SettingsWithPrefix(string prefix)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE substr(key, 1, length($p))=$p"; command.Parameters.AddWithValue("$p", prefix);
        using var rows = command.ExecuteReader(); var result = new Dictionary<string, string>();
        while (rows.Read()) result[rows.GetString(0)] = rows.GetString(1);
        return result;
    }
    // Writes several settings in one transaction instead of one commit each.
    public void SetSettings(IEnumerable<KeyValuePair<string, string>> values)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var c = db.CreateCommand(); c.Transaction = transaction;
        c.CommandText = "INSERT INTO settings VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        var key = c.Parameters.Add("$k", SqliteType.Text); var value = c.Parameters.Add("$v", SqliteType.Text);
        foreach (var entry in values) { key.Value = entry.Key; value.Value = entry.Value; c.ExecuteNonQuery(); }
        transaction.Commit();
    }
    public List<MediaItem> All()
    {
        ExpireTmdbContent();
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT json FROM media";
        using var rows = c.ExecuteReader(); var items = new List<MediaItem>();
        while (rows.Read()) items.Add(Wire.Read<MediaItem>(rows.GetString(0)));
        var shows = Shows().ToDictionary(x => x.Id); var seasons = Seasons().ToDictionary(x => x.Id);
        return SortItems(items.Select(item => ApplyHierarchy(item,
            item.ShowId is { } showId ? shows.GetValueOrDefault(showId) : null,
            item.SeasonId is { } seasonId ? seasons.GetValueOrDefault(seasonId) : null)));
    }
    // Fetch stages re-read only their affected records after awaiting providers.
    // Join current hierarchy data too: a sibling edit can change the shared show.
    private const string MediaWithHierarchy = "SELECT m.json, s.json, se.json FROM media m " +
        "LEFT JOIN shows s ON s.id=json_extract(m.json, '$.showId') " +
        "LEFT JOIN seasons se ON se.id=json_extract(m.json, '$.seasonId') ";
    internal List<MediaItem> ByIds(IEnumerable<string> ids)
    {
        ExpireTmdbContent();
        using var db = Open(); var result = new List<MediaItem>();
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            using var command = db.CreateCommand();
            var parameters = batch.Select((id, index) => "$id" + index).ToArray();
            command.CommandText = MediaWithHierarchy + "WHERE m.id IN (" + string.Join(",", parameters) + ")";
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(parameters[index], batch[index]);
            result.AddRange(ReadMedia(command));
        }
        return SortItems(result);
    }
    internal List<MediaItem> InShow(MediaItem show)
    {
        ExpireTmdbContent();
        if (show.Series is null) return ByIds([show.Id]);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = MediaWithHierarchy + "WHERE json_extract(m.json, '$.showId')=$show";
        command.Parameters.AddWithValue("$show", LibraryIdentity.ShowKey(show));
        return SortItems(ReadMedia(command));
    }
    // A moved file keeps its original ID, so a record can live at a path whose hash is not its ID.
    public MediaItem? ByPath(string path)
    {
        ExpireTmdbContent();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = MediaWithHierarchy + "WHERE m.path=$path" + (OperatingSystem.IsWindows() ? " COLLATE NOCASE" : "");
        command.Parameters.AddWithValue("$path", path);
        return ReadMedia(command).FirstOrDefault();
    }
    // Every record OpenedFiles.Resolve could match to a video opened from outside the app: the same file name, or a title
    // equal to the file's own. Reading these instead of the whole catalog lets a video opened at launch start sooner.
    public List<MediaItem> OpenedFileCandidates(string path)
    {
        ExpireTmdbContent();
        var name = System.IO.Path.GetFileName(path);
        var title = LibraryIdentity.Normalize(FileNames.Parse(path).DisplayTitle);
        var names = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Titled(string? value) => !string.IsNullOrWhiteSpace(value) && LibraryIdentity.Normalize(value) == title;
        using var db = Open();
        // A show's title replaces its episodes' series unless the series is locked, so both are compared.
        db.CreateFunction("opened_file_candidate", (string itemPath, string? series, string? itemTitle, string? original, string? seriesPath, string? showTitle) =>
            string.Equals(System.IO.Path.GetFileName(itemPath), name, names) || title.Length > 0 &&
            (Titled(series) || Titled(itemTitle) || Titled(original) || Titled(seriesPath is null ? null : System.IO.Path.GetFileName(seriesPath)) || Titled(showTitle)),
            isDeterministic: true);
        using var command = db.CreateCommand();
        command.CommandText = MediaWithHierarchy + "WHERE opened_file_candidate(m.path, json_extract(m.json, '$.series'), json_extract(m.json, '$.title'), " +
            "json_extract(m.json, '$.originalTitle'), json_extract(m.json, '$.seriesPath'), json_extract(s.json, '$.title'))";
        return SortItems(ReadMedia(command));
    }
    private static List<MediaItem> ReadMedia(SqliteCommand command)
    {
        using var rows = command.ExecuteReader(); var result = new List<MediaItem>();
        var shows = new Dictionary<string, LibraryShow>(); var seasons = new Dictionary<string, LibrarySeason>();
        while (rows.Read())
        {
            var item = Wire.Read<MediaItem>(rows.GetString(0));
            LibraryShow? show = null; LibrarySeason? season = null;
            if (item.ShowId is { } showId && !rows.IsDBNull(1) && !shows.TryGetValue(showId, out show))
                shows[showId] = show = Wire.Read<LibraryShow>(rows.GetString(1));
            if (item.SeasonId is { } seasonId && !rows.IsDBNull(2) && !seasons.TryGetValue(seasonId, out season))
                seasons[seasonId] = season = Wire.Read<LibrarySeason>(rows.GetString(2));
            result.Add(ApplyHierarchy(item, show, season));
        }
        return result;
    }
    private static List<MediaItem> SortItems(IEnumerable<MediaItem> items) =>
        items.OrderBy(x => x.DisplayTitle).ThenBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    private static MediaItem ApplyHierarchy(MediaItem item, LibraryShow? show, LibrarySeason? season)
    {
        if (show is not null)
        {
            var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
            foreach (var id in show.ProviderIds) ids.TryAdd(id.Key, id.Value);
            item = item with { Series = LibraryIdentity.Locked(item, "Title") ? item.Series : show.Title, Kind = show.Kind,
                Year = LibraryIdentity.Locked(item, "Year") ? item.Year : show.Year, SeriesPoster = show.Poster ?? item.SeriesPoster,
                Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : show.Overview ?? item.Overview,
                ProviderIds = ids, NumberingOrder = show.NumberingOrder, TmdbFields = TmdbRetention.Merge(item.TmdbFields,
                    TmdbRetention.Transfer(show.TmdbFields, ("title", "series"), ("kind", "kind"), ("year", "year"), ("poster", "seriesPoster"), ("overview", "overview"))) };
        }
        if (season is not null) item = item with
        {
            SeasonTitle = LibraryIdentity.Locked(item, "SeasonTitle") ? item.SeasonTitle : season.Title ?? item.SeasonTitle,
            SeasonOverview = LibraryIdentity.Locked(item, "SeasonOverview") ? item.SeasonOverview : season.Overview ?? item.SeasonOverview,
            SeasonPoster = season.Poster ?? item.SeasonPoster, TmdbFields = TmdbRetention.Merge(item.TmdbFields,
                TmdbRetention.Transfer(season.TmdbFields, ("title", "seasonTitle"), ("overview", "seasonOverview"), ("poster", "seasonPoster")))
        };
        return item;
    }
    // Scans merge each file into its current record under this gate, so a concurrent edit is never replaced by a stale copy.
    private readonly object writeGate = new();
    public void Save(MediaItem item)
    {
        item = LibraryIdentity.Ensure(TmdbRetention.TrackLegacy(item));
        lock (writeGate) Write(item);
        MediaSaved?.Invoke(item);
    }
    // Adds a file-derived length to a movie that has none. The entry is re-read under the write
    // lock, so a metadata save that lands first is kept rather than replaced with an older copy.
    public MediaItem? FillMovieRuntime(string id, int minutes)
    {
        MediaItem updated;
        lock (writeGate)
        {
            using (var db = Open())
            using (var read = db.CreateCommand())
            {
                read.CommandText = "SELECT json FROM media WHERE id=$id"; read.Parameters.AddWithValue("$id", id);
                if (read.ExecuteScalar() is not string json) return null;
                var current = Wire.Read<MediaItem>(json);
                if (!LibraryRuntimes.NeedsRuntime(current) || minutes <= 0) return null;
                updated = current with { RuntimeMinutes = minutes };
            }
            Write(updated);
        }
        MediaSaved?.Invoke(updated);
        return updated;
    }
    private void Write(MediaItem item)
    {
        using var db = Open(); using var c = db.CreateCommand();
        using var transaction = db.BeginTransaction(); c.Transaction = transaction;
        using var prior = db.CreateCommand(); prior.Transaction = transaction;
        prior.CommandText = "SELECT json FROM media WHERE id=$id"; prior.Parameters.AddWithValue("$id", item.Id);
        var previous = prior.ExecuteScalar() is string old ? Wire.Read<MediaItem>(old) : null;
        var identityChanged = previous is not null && (previous.MetadataType != item.MetadataType && item.MetadataType is not null ||
            previous.MetadataKind != item.MetadataKind && item.MetadataKind is not null ||
            previous.MetadataProvider == item.MetadataProvider && previous.MetadataId is not null && item.MetadataId is not null && previous.MetadataId != item.MetadataId);
        c.CommandText = "INSERT INTO media VALUES($id,$path,$json) ON CONFLICT(id) DO UPDATE SET path=$path, json=$json";
        c.Parameters.AddWithValue("$id", item.Id); c.Parameters.AddWithValue("$path", item.Path); c.Parameters.AddWithValue("$json", Wire.Serialize(item)); c.ExecuteNonQuery();
        if (item.ShowId is { } showId && item.SeasonId is { } seasonId)
        {
            var show = new LibraryShow(showId, item.Series!, item.Kind, item.Year, item.SeriesPoster ?? item.Poster, item.Overview,
                new Dictionary<string, string>(item.ProviderIds ?? []), item.NumberingOrder,
                TmdbRetention.Transfer(item.TmdbFields, ("series", "title"), ("kind", "kind"), ("year", "year"), ("seriesPoster", "poster"), ("overview", "overview")));
            var season = new LibrarySeason(seasonId, showId, item.Season, item.SeasonTitle, item.SeasonOverview, item.SeasonPoster,
                TmdbRetention.Transfer(item.TmdbFields, ("seasonTitle", "title"), ("seasonOverview", "overview"), ("seasonPoster", "poster")));
            using var read = db.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT json FROM shows WHERE id=$id"; read.Parameters.AddWithValue("$id", showId);
            if (read.ExecuteScalar() is string showJson && !identityChanged)
            {
                var current = Wire.Read<LibraryShow>(showJson);
                var ids = new Dictionary<string, string>(identityChanged ? [] : current.ProviderIds, StringComparer.OrdinalIgnoreCase);
                foreach (var id in show.ProviderIds) ids[id.Key] = id.Value;
                if (item.MetadataProvider is { } provider && item.MetadataId is > 0) ids[provider] = item.MetadataId.Value.ToString();
                show = show with { Title = previous is null || item.Series == previous.Series ? current.Title : show.Title,
                    Kind = previous is null || item.Kind == previous.Kind ? current.Kind : show.Kind,
                    Year = previous is null || item.Year == previous.Year ? current.Year ?? show.Year : show.Year,
                    Overview = item.Overview is null || previous is not null && item.Overview == previous.Overview ? current.Overview ?? show.Overview : show.Overview,
                    Poster = show.Poster is null || previous is not null && show.Poster == (previous.SeriesPoster ?? previous.Poster) ? current.Poster ?? show.Poster : show.Poster,
                    NumberingOrder = previous is null || item.NumberingOrder == previous.NumberingOrder ? current.NumberingOrder : show.NumberingOrder, ProviderIds = ids,
                    TmdbFields = TmdbRetention.Merge(current.TmdbFields, show.TmdbFields) };
            }
            read.CommandText = "SELECT json FROM seasons WHERE id=$id"; read.Parameters["$id"].Value = seasonId;
            if (read.ExecuteScalar() is string seasonJson && !identityChanged)
            {
                var current = Wire.Read<LibrarySeason>(seasonJson);
                season = season with { Title = item.SeasonTitle is null || previous is not null && item.SeasonTitle == previous.SeasonTitle ? current.Title ?? season.Title : season.Title,
                    Overview = item.SeasonOverview is null || previous is not null && item.SeasonOverview == previous.SeasonOverview ? current.Overview ?? season.Overview : season.Overview,
                    Poster = item.SeasonPoster is null || previous is not null && item.SeasonPoster == previous.SeasonPoster ? current.Poster ?? season.Poster : season.Poster, TmdbFields = TmdbRetention.Merge(current.TmdbFields, season.TmdbFields) };
            }
            using var hierarchy = db.CreateCommand(); hierarchy.Transaction = transaction;
            hierarchy.CommandText = "INSERT INTO shows VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json; INSERT INTO seasons VALUES($season,$id,$seasonJson) ON CONFLICT(id) DO UPDATE SET json=$seasonJson;";
            hierarchy.Parameters.AddWithValue("$id", showId); hierarchy.Parameters.AddWithValue("$json", Wire.Serialize(show));
            hierarchy.Parameters.AddWithValue("$season", seasonId); hierarchy.Parameters.AddWithValue("$seasonJson", Wire.Serialize(season)); hierarchy.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public List<LibraryShow> Shows() { ExpireTmdbContent(); return ReadHierarchy<LibraryShow>("shows"); }
    public List<LibrarySeason> Seasons() { ExpireTmdbContent(); return ReadHierarchy<LibrarySeason>("seasons"); }
    public void RemoveVirtual(IEnumerable<string> ids)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM media WHERE id=$id AND json_extract(json,'$.isVirtual')=1";
            command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
        transaction.Commit(); CleanHierarchy();
    }
    public int RemoveUnavailable(IEnumerable<string> ids)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        var removed = 0;
        foreach (var id in ids.Distinct())
        {
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM media WHERE id=$id AND json_extract(json,'$.available')=0";
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) continue;
            removed++;
            command.CommandText = "DELETE FROM settings WHERE key=$position OR key=$watched OR key=$played";
            command.Parameters.AddWithValue("$position", "position:" + id);
            command.Parameters.AddWithValue("$watched", "watched:" + id);
            command.Parameters.AddWithValue("$played", "played:" + id);
            command.ExecuteNonQuery();
        }
        transaction.Commit(); CleanHierarchy();
        return removed;
    }
    public void GroupShowsByProvider()
    {
        if (!MetadataOptions.Load(this).GroupShowsByProvider) return;
        MediaItem[]? items = null; var wanted = Wanted().Select(x => x.Id).ToHashSet();
        foreach (var provider in new[] { "tmdb", "tvdb", "imdb" })
        {
            // Re-read only after a provider's grouping changed records.
            items ??= All().Where(x => x.Series is not null && !x.IsExtra && !x.IsMusic).ToArray();
            var merged = false;
            foreach (var group in items.Where(x => LibraryIdentity.ProviderId(x, provider) is not null).GroupBy(x => LibraryIdentity.ProviderId(x, provider)))
            {
                if (group.Select(x => x.MetadataKind ?? x.Kind).Distinct().Count() > 1) continue;
                if (provider != "tmdb" && group.Select(x => LibraryIdentity.ProviderId(x, "tmdb")).OfType<string>().Distinct().Count() > 1) continue;
                // A wanted title's show stays the show the others join.
                var ids = group.Select(LibraryIdentity.ShowKey).Distinct().OrderByDescending(wanted.Contains).ThenBy(x => x, StringComparer.Ordinal).ToArray();
                if (ids.Length < 2) continue;
                foreach (var entry in items.Where(x => ids.Contains(LibraryIdentity.ShowKey(x)))) { Save(entry with { ShowId = ids[0] }); merged = true; }
            }
            if (merged) items = null;
        }
        CleanHierarchy();
    }
    private List<T> ReadHierarchy<T>(string table)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM " + table;
        using var rows = command.ExecuteReader(); var records = new List<T>();
        while (rows.Read()) records.Add(Wire.Read<T>(rows.GetString(0))); return records;
    }
    public static bool IsWithin(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        var full = System.IO.Path.GetFullPath(path);
        return full.Equals(root, comparison) || full.StartsWith(System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar, comparison);
    }
    public int Prune(IEnumerable<LibraryFolder> folders, IEnumerable<string>? exclusions = null) => Prune(folders, exclusions, All()).Removed;
    // Prunes and returns the remaining catalog from a single read. Only orphaned shows and seasons are
    // deleted alongside removed files, so the remaining items are unchanged.
    public List<MediaItem> PruneAndLoad(IEnumerable<LibraryFolder> folders, IEnumerable<string>? exclusions = null) => Prune(folders, exclusions, All()).Remaining;
    public MediaItem? Get(string id) => ByIds([id]).FirstOrDefault();
    private (int Removed, List<MediaItem> Remaining) Prune(IEnumerable<LibraryFolder> folders, IEnumerable<string>? exclusions, List<MediaItem> all)
    {
        var roots = folders.ToArray(); var excluded = exclusions?.ToArray() ?? []; var mixedMusic = MixedFoldersListMusic;
        // Changing a folder's category, or whether Mixed folders list music, drops what it no longer lists.
        bool Retained(MediaItem item) => ListedBy(roots, item.Path, mixedMusic) is not null && !excluded.Any(f => IsWithin(item.Path, f));
        // A wanted title keeps its placeholders while none of its files are here.
        var retainedShows = all.Where(x => !x.IsVirtual && Retained(x)).Select(LibraryIdentity.ShowKey).Concat(Wanted().Select(x => x.Id)).ToHashSet();
        var removed = all.Where(x => x.IsVirtual ? !retainedShows.Contains(LibraryIdentity.ShowKey(x)) : !Retained(x)).ToArray();
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var item in removed)
        {
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM media WHERE id=$id"; command.Parameters.AddWithValue("$id", item.Id); command.ExecuteNonQuery();
        }
        transaction.Commit();
        CleanHierarchy();
        if (removed.Length == 0) return (0, all);
        var gone = removed.Select(x => x.Id).ToHashSet();
        return (removed.Length, all.Where(x => !gone.Contains(x.Id)).ToList());
    }
    private void CleanHierarchy()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM seasons WHERE id NOT IN (SELECT json_extract(json, '$.seasonId') FROM media WHERE json_extract(json, '$.seasonId') IS NOT NULL); DELETE FROM shows WHERE id NOT IN (SELECT json_extract(json, '$.showId') FROM media WHERE json_extract(json, '$.showId') IS NOT NULL);";
        command.ExecuteNonQuery();
    }
    // Files added, changed or removed by the most recent completed scan.
    public int LastScanChanges { get; private set; }
    // Off unless chosen: video folders often hold audio that isn't music to browse, such as soundtracks and commentaries.
    public const string MixedMusicSetting = "mixedMusic";
    public bool MixedFoldersListMusic
    {
        get => Setting(MixedMusicSetting) == "true";
        set => Setting(MixedMusicSetting, value ? "true" : "false");
    }
    // Music folders list audio files, Mixed folders list videos and, when chosen, audio files, and other folders list videos.
    private static bool Lists(LibraryFolder folder, string path, bool mixedMusic) =>
        MusicFiles.IsAudio(path) ? folder.Kind == MusicFiles.Kind || mixedMusic && folder.Kind == "Mixed" : folder.Kind != MusicFiles.Kind;
    // The innermost folder holding a file decides whether it is listed, so a folder inside another can list other files.
    private static LibraryFolder? ListedBy(LibraryFolder[] roots, string path, bool mixedMusic) =>
        roots.Where(f => IsWithin(path, f.Path)).MaxBy(f => f.Path.Length) is { } root && Lists(root, path, mixedMusic) ? root : null;
    public async Task<List<MediaItem>> ScanAsync(IEnumerable<LibraryFolder> folders, IProgress<string>? progress = null, CancellationToken cancellation = default, IEnumerable<string>? exclusions = null)
    {
        var roots = folders.ToArray(); var excluded = exclusions?.ToArray() ?? []; var mixedMusic = MixedFoldersListMusic;
        return await Task.Run(() =>
        {
            var changes = Prune(roots, excluded);
            var seen = new HashSet<string>();
            // Files new to the library this scan; one may be a known video that moved.
            var found = new List<MediaItem>();
            foreach (var folder in roots)
            {
                if (!Directory.Exists(folder.Path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };
                foreach (var file in new DirectoryInfo(folder.Path).EnumerateFiles("*", options))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var path = file.FullName;
                    if (excluded.Any(f => IsWithin(path, f))) continue;
                    var music = MusicFiles.IsAudio(path);
                    if (!music && !FileNames.Extensions.Contains(System.IO.Path.GetExtension(path)) || !Lists(folder, path, mixedMusic)) continue;
                    // A folder inside this one that doesn't list this kind of file keeps it out.
                    if (roots.Any(r => r.Path.Length > folder.Path.Length && IsWithin(path, r.Path)) && ListedBy(roots, path, mixedMusic) is null) continue;
                    MediaItem parsed;
                    if (music)
                    {
                        // Tags are read again only when a track's file changed, or a cover appeared beside a track without one.
                        var writeTime = file.LastWriteTimeUtc.Ticks;
                        if (ByPath(path) is { IsMusic: true, Available: true } unchanged && unchanged.FileSize == file.Length && unchanged.FileWriteTime == writeTime &&
                            !MusicFiles.CoverAdded(unchanged))
                        {
                            seen.Add(unchanged.Id);
                            progress?.Report($"Found {seen.Count} files · {unchanged.Title}");
                            continue;
                        }
                        parsed = MusicFiles.Parse(path, folder.Path, DataDirectory) with { FileSize = file.Length, FileWriteTime = writeTime };
                    }
                    // Samples and film trailers stay out; a show's extras folder joins the show as bonus clips.
                    else parsed = FileNames.Parse(path, folder.Kind, folder.Path) with { FileSize = file.Length };
                    if (parsed.IsExtra) continue;
                    // Merge into the record as stored now: edits and fetches can land while a scan runs.
                    MediaItem item;
                    lock (writeGate)
                    {
                        var previous = ByIds([parsed.Id]).FirstOrDefault() ?? ByPath(parsed.Path);
                        item = music ? previous is null ? parsed : MusicFiles.Merge(parsed, previous)
                            : previous is null ? MergeScanned(parsed, null) : MergeScanned(parsed with { Id = previous.Id }, previous) with { FileSize = parsed.FileSize };
                        // Records compare collections by reference, so compare the stored form to skip unchanged files.
                        if (previous is null || Wire.Serialize(LibraryIdentity.Ensure(TmdbRetention.TrackLegacy(item))) != Wire.Serialize(previous)) { Save(item); changes++; }
                        if (previous is null) found.Add(item);
                    }
                    seen.Add(item.Id);
                    progress?.Report($"Found {seen.Count} files · {item.Title}");
                }
            }
            changes += RecoverMoved(found, seen, progress);
            foreach (var id in All().Where(x => !seen.Contains(x.Id) && !x.IsVirtual).Select(x => x.Id).ToArray())
                lock (writeGate)
                {
                    if (ByIds([id]).FirstOrDefault() is not { IsVirtual: false } stored) continue;
                    // A bonus clip from a show's extras folder is a catalog entry, so losing its file makes it unavailable rather than
                    // hidden; a clip the parser no longer counts as a show's becomes a hidden extra again.
                    var root = roots.Where(r => IsWithin(stored.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
                    var extra = stored.IsExtra || FileNames.IsExtra(stored.Path) && (!stored.IsBonus || FileNames.Parse(stored.Path, root?.Kind ?? "Mixed", root?.Path).IsExtra);
                    var missing = stored with { IsExtra = extra, Available = extra && File.Exists(stored.Path) };
                    if (missing != stored) { Save(missing); changes++; }
                }
            changes += DropReplacedReleases();
            AttachLooseReleases();
            MergeDuplicateShows();
            GroupShowsByProvider();
            changes += WantedTitles.Attach(this, DataDirectory);
            LastScanChanges = changes;
            return All();
        }, cancellation);
    }
    // A file missing from its stored path and found at a new one keeps its original record, so its ID,
    // watch progress, queue entries and metadata carry over. Matches must be unambiguous both ways.
    private int RecoverMoved(List<MediaItem> found, HashSet<string> seen, IProgress<string>? progress)
    {
        if (found.Count == 0) return 0;
        var all = All();
        var gone = all.Where(x => !x.IsVirtual && !x.IsExtra && !seen.Contains(x.Id) && !File.Exists(x.Path)).ToArray();
        if (gone.Length == 0) return 0;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        static string Name(MediaItem x) => System.IO.Path.GetFileName(x.Path);
        static string Folder(MediaItem x) => System.IO.Path.GetDirectoryName(x.Path) ?? "";
        // A matching size confirms a move under the same name or a rename within one folder. Records
        // stored before sizes were kept need the same name.
        bool Moved(MediaItem old, MediaItem now) => old.FileSize is { } size
            ? size == now.FileSize && (comparer.Equals(Name(old), Name(now)) || comparer.Equals(Folder(old), Folder(now)))
            : comparer.Equals(Name(old), Name(now));
        var goneByName = gone.ToLookup(Name, comparer); var goneByFolder = gone.Where(x => x.FileSize is not null).ToLookup(Folder, comparer);
        var foundByName = found.ToLookup(Name, comparer); var foundByFolder = found.ToLookup(Folder, comparer);
        var changes = 0;
        foreach (var now in found)
        {
            var olds = goneByName[Name(now)].Concat(goneByFolder[Folder(now)]).Distinct().Where(old => Moved(old, now)).Take(2).ToArray();
            if (olds.Length != 1 || foundByName[Name(olds[0])].Concat(foundByFolder[Folder(olds[0])]).Distinct().Count(other => Moved(olds[0], other)) != 1) continue;
            lock (writeGate)
            {
                if (ByIds([olds[0].Id]).FirstOrDefault() is not { IsVirtual: false } stored || File.Exists(stored.Path) || ByIds([now.Id]).Count == 0) continue;
                var parsed = now with { Id = stored.Id };
                var moved = parsed.IsMusic ? MusicFiles.Merge(parsed, stored) : MergeScanned(parsed, stored) with { FileSize = now.FileSize };
                // Joining a folder that already holds the show groups the file with its new siblings.
                if (CanRepairGrouping(stored) && parsed.SeriesPath is not null && !comparer.Equals(stored.SeriesPath, parsed.SeriesPath) &&
                    all.FirstOrDefault(x => x.Id != now.Id && x.Available && !x.IsVirtual && seen.Contains(x.Id) && comparer.Equals(x.SeriesPath, parsed.SeriesPath)) is { ShowId: { } showId })
                    moved = moved with { ShowId = showId };
                // The record created for the new path this scan gives way, freeing the path for the original.
                using (var db = Open()) using (var command = db.CreateCommand())
                {
                    command.CommandText = "DELETE FROM media WHERE id=$id"; command.Parameters.AddWithValue("$id", now.Id); command.ExecuteNonQuery();
                }
                Save(moved); seen.Remove(now.Id); seen.Add(stored.Id); changes++;
                progress?.Report("Moved video found · " + moved.Title);
            }
        }
        if (changes > 0) CleanHierarchy();
        return changes;
    }
    private static MediaItem MergeScanned(MediaItem parsed, MediaItem? previous)
    {
        var item = previous is not null ? previous with { Path = parsed.Path, Available = true, Year = previous.Matched || previous.MetadataType is not null ? previous.Year : parsed.Year, Kind = previous.MetadataKind ?? (previous.ShowId is not null && previous.ShowId != parsed.ShowId ? previous.Kind : parsed.Kind), Series = previous.MetadataType == "movie" ? null : previous.MetadataType == "tv" || previous.Matched ? previous.Series : parsed.Series ?? previous.Series, Season = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Season : parsed.Season ?? previous.Season, Episode = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Episode : parsed.Episode ?? previous.Episode, SeriesPoster = parsed.SeriesPoster ?? previous.SeriesPoster, SeasonPoster = parsed.SeasonPoster ?? previous.SeasonPoster, Poster = previous.Matched && File.Exists(previous.Poster) ? previous.Poster : parsed.Poster ?? (File.Exists(previous.Poster) ? previous.Poster : null) } : parsed;
        item = item with { ShowId = previous?.ShowId ?? parsed.ShowId, SeriesPath = parsed.SeriesPath ?? previous?.SeriesPath,
            // A clip's title follows its file name; an episode keeps a title metadata gave it.
            EpisodeTitle = LibraryIdentity.Locked(item, "EpisodeTitle") ? item.EpisodeTitle : parsed.IsBonus && !item.Matched ? parsed.EpisodeTitle ?? item.EpisodeTitle : item.EpisodeTitle ?? parsed.EpisodeTitle,
            AirDate = LibraryIdentity.Locked(item, "AirDate") ? item.AirDate : parsed.AirDate ?? item.AirDate,
            EpisodeEnd = previous?.AbsoluteEpisode is not null ? previous.EpisodeEnd : parsed.EpisodeEnd ?? previous?.EpisodeEnd,
            AbsoluteEpisode = parsed.AbsoluteEpisode ?? previous?.AbsoluteEpisode, AbsoluteEpisodeEnd = parsed.AbsoluteEpisodeEnd ?? previous?.AbsoluteEpisodeEnd,
            SourcePart = parsed.SourcePart ?? previous?.SourcePart, SourcePartEpisode = parsed.SourcePartEpisode ?? previous?.SourcePartEpisode,
            SourcePartEpisodeEnd = parsed.SourcePartEpisodeEnd ?? previous?.SourcePartEpisodeEnd,
            NumberingConflict = parsed.NumberingConflict, NumberingOrder = previous?.NumberingOrder ?? parsed.NumberingOrder };
        var sourceNumbering = parsed.NumberingSource is "filename" or "season-folder";
        if (previous is not null && CanRepairGrouping(previous) && previous.MetadataType is null && parsed.Series is not null && !LibraryIdentity.Locked(previous, "Title"))
            item = item with { Title = parsed.Title };
        if (sourceNumbering && previous is not null && CanRepairGrouping(previous) && previous.NumberingOrder != "dvd")
            item = item with { Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd,
                AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, NumberingOrder = "aired" };
        if (parsed.AbsoluteEpisodeEnd is not null && previous is not null && CanRepairGrouping(previous) && previous.AbsoluteEpisode is null && previous.NumberingOrder != "dvd")
            item = item with { NumberingOrder = parsed.NumberingOrder };
        if (parsed.Series is null && parsed.Episode is null && previous is not null && CanRepairGrouping(previous) && previous.MetadataType is null &&
            (previous.Kind == "Movie" || parsed.Year is >= 1900 && previous.Episode == parsed.Year))
            item = item with { Title = parsed.Title, Series = null, ShowId = null, SeasonId = null, SeriesPath = null,
                Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null };
        if (previous is not null)
        {
            if (previous.Matched || LibraryIdentity.Locked(previous, "Season")) item = item with { Season = previous.Season };
            if (previous.Matched || LibraryIdentity.Locked(previous, "Episode")) item = item with
            { Episode = previous.Episode, EpisodeEnd = previous.EpisodeEnd, AbsoluteEpisode = previous.AbsoluteEpisode, AbsoluteEpisodeEnd = previous.AbsoluteEpisodeEnd };
            if (LibraryIdentity.Locked(previous, "Title")) item = item with { Title = previous.Title, Series = previous.Series };
            if (LibraryIdentity.Locked(previous, "Kind")) item = item with { Kind = previous.Kind };
            // Older parsers read "S03E07 - 42" as a range; drop a stored range the filename no longer supports.
            if (previous.Matched && !LibraryIdentity.Locked(previous, "Episode") && previous.AbsoluteEpisode is null && previous.EpisodeEnd is not null &&
                parsed.NumberingSource == "filename" && parsed.EpisodeEnd is null && parsed.Season == previous.Season && parsed.Episode == previous.Episode)
                item = item with { EpisodeEnd = null };
        }
        var bonus = previous?.Matched == true || previous is not null && LibraryIdentity.Locked(previous, "Episode") ? previous.IsBonus : parsed.IsBonus;
        // A film's clip stays with its film, including one an earlier scan had filed under a show of its own.
        if (parsed.ExtraOf is not null && (previous is null || CanRepairGrouping(previous)))
        { item = item with { ExtraOf = parsed.ExtraOf, Title = parsed.Title, Kind = parsed.Kind, Series = null, ShowId = null, SeasonId = null, SeriesPath = null, Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, Year = null }; bonus = true; }
        // A clip's home season comes from its folder, so a rescan places clips indexed before seasons were recorded.
        if (bonus && parsed.IsBonus && parsed.AirsAfterSeason is not null) item = item with { AirsAfterSeason = parsed.AirsAfterSeason };
        return LocalMetadata.Apply(item with { IsBonus = bonus, NumberingSource = parsed.NumberingSource,
            SourceLibraryKind = parsed.SourceLibraryKind, IsExtra = false });
    }
}

public static partial class FileNames
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".mpeg", ".mpg", ".ts", ".m2ts", ".wmv", ".flv", ".ogv" };
    // Sidecar, artwork and partial-download files never change the catalog, so watcher events for them are ignored.
    private static readonly HashSet<string> IgnoredChanges = new(StringComparer.OrdinalIgnoreCase)
    { ".nfo", ".tmp", ".part", ".crdownload", ".download", ".partial", ".!qb", ".!ut", ".aria2", ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup",
      ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tbn", ".txt", ".url", ".ini", ".db", ".json", ".xml", ".log", ".torrent", ".parts" };
    // Audio files count only in folders that list music, so a soundtrack saved beside a film doesn't start a scan.
    public static bool AffectsLibrary(string path, bool deleted, bool music = false)
    {
        var extension = System.IO.Path.GetExtension(path);
        if (MusicFiles.Extensions.Contains(extension)) return music;
        if (Extensions.Contains(extension)) return true;
        // A deleted folder can no longer be checked, so only skip names that look like a known non-video file.
        if (deleted) return !IgnoredChanges.Contains(extension);
        return Directory.Exists(path);
    }
    private const string ExtrasFolders = @"(?i)^(?:extras|trailers|featurettes|behind the scenes)$";
    private const string SeasonFolder = @"(?i)(?:^|\s)(?:Season\s*|S)(\d{1,2})(?!\d|E\d)\b|^(\d+)\s*(?:сезон|sezon)\b|^Specials$";
    private static string[] Parts(string path) => path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    private static bool IsSeasonFolder(string name) => Regex.IsMatch(Regex.Replace(name, @"[._]+", " "), SeasonFolder);
    private static int? SeasonNumber(string name) => Regex.Match(Regex.Replace(name, @"[._]+", " "), SeasonFolder) is { Success: true } match
        ? match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0 : null;
    // Raw path policy: a file in an extras folder, or named as a trailer or sample. Parse keeps a show's extras as bonus clips.
    public static bool IsExtra(string path) => (Parts(path).SkipLast(1).Any(part => Regex.IsMatch(part, ExtrasFolders) || Regex.IsMatch(part, @"(?i)^samples?$")) ||
        Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:^|[-._ ])(?:trailer|sample|featurette)\d*$")) &&
        FilenameEpisodes.Read(path, false) is not { ExplicitSeason: true, Season: 0 };
    private static bool IsSample(string path) => Parts(path).SkipLast(1).Any(part => Regex.IsMatch(part, @"(?i)^samples?$")) ||
        Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:^|[-._ ])sample\d*$");
    private static string Identity(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)))[..32];
    // An extras folder belongs to a show when it sits under a season folder, or beside season folders or numbered
    // episodes. A folder with an unnumbered video beside it is a film's own extras folder, even inside an anime library.
    private static (bool Show, string? Film) ExtrasHome(string path, bool seriesContext)
    {
        if (IsSample(path)) return (false, null);
        var parts = Parts(path);
        var extras = Array.FindLastIndex(parts, parts.Length - 2, part => Regex.IsMatch(part, ExtrasFolders));
        if (extras < 1) return (false, null);
        if (parts[..extras].Any(IsSeasonFolder)) return (true, null);
        var parent = string.Join(System.IO.Path.DirectorySeparatorChar, parts[..extras]);
        try
        {
            if (!Directory.Exists(parent)) return (seriesContext, null);
            if (File.Exists(System.IO.Path.Combine(parent, "tvshow.nfo")) || Directory.EnumerateDirectories(parent).Any(folder => IsSeasonFolder(System.IO.Path.GetFileName(folder)))) return (true, null);
            var videos = Directory.EnumerateFiles(parent).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file)) && !IsExtra(file)).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
            if (videos.Length == 0) return (false, null);
            var film = videos.FirstOrDefault(file => FilenameEpisodes.Read(file, true) is null);
            return film is null ? (true, null) : (false, film);
        }
        catch (IOException) { return (false, null); }
        catch (UnauthorizedAccessException) { return (false, null); }
    }
    public static MediaItem Parse(string path, string kind = "Mixed", string? libraryRoot = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var sourceKind = kind;
        var id = Identity(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var seriesContext = kind is "Show" or "Anime" || kind == "Mixed" && path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => Regex.IsMatch(part, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$")) || File.Exists(System.IO.Path.Combine(dir, "tvshow.nfo"));
        var (showExtras, filmExtra) = ExtrasHome(path, seriesContext);
        var bonus = seriesContext && Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path),
            @"(?i)(?:^|[ ._-])(?:NC(?:OP|ED)\d*|(?:BD[ -]?Box|DVD(?:[ -](?:Memorial|Remaster))?|LD)?[ -]*CM(?:[ -]Collection)?|Promo|Preview|Pilot[ -]Film)(?=$|[ ._(-])");
        var seasonFolderName = System.IO.Path.GetFileName(dir);
        var seasonFolder = Regex.Match(Regex.Replace(seasonFolderName, @"[._]+", " "), @"(?i)(?:^|\s)(?:Season\s*|S)(\d{1,2})(?!\d|E\d)\b|^(\d+)\s*(?:сезон|sezon)\b|^Specials$");
        var folderSeason = seasonFolder.Success ? seasonFolder.Groups[1].Success ? int.Parse(seasonFolder.Groups[1].Value) : seasonFolder.Groups[2].Success ? int.Parse(seasonFolder.Groups[2].Value) : 0 : (int?)null;
        var name = FilenameEpisodes.Clean(path, seriesContext || seasonFolder.Success);
        var episodeName = name;
        var numbering = FilenameEpisodes.Read(path, seriesContext || seasonFolder.Success, folderSeason, name, specials: sourceKind != "Movie");
        var season = numbering?.Season; var number = numbering?.Episode;
        string? series = numbering?.Series;
        // "Azumanga Daioh - XX - Original Opening Sequence" beside the show's numbered episodes is one of its clips, not a film.
        // "One Piece - 492A" is half of an episode, so it stays where its folder put it.
        var besideEpisodes = numbering is null && !bonus && sourceKind != "Movie" && filmExtra is null && !showExtras && !IsExtra(path) &&
            !Regex.IsMatch(name, @"(?i)(?:^|\s-\s)\d{1,4}[a-z]\b")
            ? EpisodeFolderSeries(dir, name, libraryRoot, seriesContext || seasonFolder.Success) : null;
        if (besideEpisodes is not null) { bonus = true; series = besideEpisodes; }
        // "One Piece - Egghead SP4" beside "One Piece - 1100" is a One Piece special, not a show called "One Piece - Egghead".
        else if (numbering is { ExplicitSeason: true, Season: 0 } && EpisodeFolderSeries(dir, name, libraryRoot, seriesContext || seasonFolder.Success) is { } owner) series = owner;
        if (numbering is not null && !numbering.ExplicitSeason && number is not null && kind == "Mixed" && Regex.IsMatch(System.IO.Path.GetFileName(path), @"^\[[^\]]+\]")) kind = "Anime";
        var filenameSeason = numbering?.ExplicitSeason == true ? season : null;
        if (seasonFolder.Success)
        {
            season = filenameSeason ?? (seasonFolder.Groups[1].Success ? int.Parse(seasonFolder.Groups[1].Value) : seasonFolder.Groups[2].Success ? int.Parse(seasonFolder.Groups[2].Value) : 0);
            if (string.IsNullOrWhiteSpace(series) || series == seasonFolderName)
                series = seasonFolder.Index > 0 ? seasonFolderName[..seasonFolder.Index].Trim() : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir)!);
        }
        if (numbering is not null && string.IsNullOrWhiteSpace(series)) series = System.IO.Path.GetFileName(dir);
        var seriesDir = dir;
        int? sourcePart = null;
        while (IsReleaseDirectory(seriesDir) && System.IO.Path.GetDirectoryName(seriesDir) is { } parent &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            var part = Regex.Match(System.IO.Path.GetFileName(seriesDir), @"(?i)^Part\s*(\d+)\s*$");
            if (part.Success) sourcePart = int.Parse(part.Groups[1].Value);
            seriesDir = parent;
        }
        // A show's extras folder joins the show as bonus clips, whatever the file names say; an explicit S00 file there is a numbered special.
        var extraClip = showExtras && seriesDir != dir && numbering is not { ExplicitSeason: true, Season: 0 };
        if (extraClip) { bonus = true; series = System.IO.Path.GetFileName(seriesDir); }
        // Dates in an episode title are not the premiere year of the entire series.
        var years = Regex.Matches(series ?? episodeName, @"\b(19\d{2}|20\d{2})\b");
        var year = series is null && years.Count > 1 ? years[^1] : years.Count > 0 ? years[0] : Match.Empty;
        if (series is not null) series = Regex.Replace(Regex.Replace(Regex.Replace(StripReleaseTags(series), @"[._]+", " "), @"\s*\(?(?:19|20)\d{2}\)?\s*$", ""), @"\s+", " ").Trim();        var title = series ?? (year.Success ? name[..year.Index] : name);
        title = Regex.Split(title, @"(?i)\b(?:2160p|1080p|720p|480p|WEB-DL|WEBRip|BluRay|x264|x265|HEVC)\b")[0].Trim(' ', '-', '(');
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);
        string? Artwork(string folder, params string[] stems) => stems.SelectMany(stem => new[] { ".jpg", ".jpeg", ".png" }.Select(ext => System.IO.Path.Combine(folder, stem + ext))).FirstOrDefault(File.Exists);
        var localPoster = Artwork(dir, System.IO.Path.GetFileNameWithoutExtension(path), "poster", "folder", "cover");
        var seriesPoster = series is null ? null : Artwork(seriesDir, "poster", "folder", "cover");
        var seasonPoster = series is null ? null : Artwork(dir, "poster", "folder", "cover") ?? Artwork(seriesDir, $"season{season ?? 1:00}-poster", $"season{season ?? 1}-poster");
        var result = new MediaItem(id, path, title, kind == "Mixed" ? (series is null ? "Movie" : "Show") : kind,
            year.Success ? int.Parse(year.Value) : null, series, season, number, localPoster ?? seriesPoster,
            SeriesPoster: seriesPoster, SeasonPoster: seasonPoster, EpisodeTitle: numbering?.Subtitle, AirDate: numbering?.AirDate,
            EpisodeEnd: numbering?.End, NumberingConflict: numbering?.Conflict);
        // Structured show folders own membership, regardless of aliases in individual filenames.
        var parentName = System.IO.Path.GetFileName(seriesDir);
        var structured = File.Exists(System.IO.Path.Combine(seriesDir, "tvshow.nfo")) || (numbering is not null || bonus) && (seasonFolder.Success || seriesContext || seriesDir != dir) &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) &&
            !Regex.IsMatch(parentName, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$");
        if (structured)
        {
            var folderTitle = StripReleaseTags(parentName);
            var folderYear = Regex.Match(folderTitle, @"\s*\((19\d{2}|20\d{2})\)\s*$");
            folderTitle = Regex.Replace(folderTitle, @"\s*\((?:19|20)\d{2}\)\s*$", "");
            folderTitle = Regex.Replace(Regex.Replace(folderTitle, @"[._]+", " "), @"\s+", " ").Trim();
            if (folderTitle.Length == 0) folderTitle = parentName;
            result = result with { Series = folderTitle, Title = folderTitle, SeriesPath = seriesDir,
                Year = folderYear.Success ? int.Parse(folderYear.Groups[1].Value) : result.Year,
                ShowId = "show-" + LibraryIdentity.Hash(OperatingSystem.IsWindows() ? seriesDir.ToUpperInvariant() : seriesDir) };
        }
        // A special filed inside a season folder is that season's special, not a numbering conflict.
        if (filenameSeason is not null and not 0 && folderSeason is not null && filenameSeason != folderSeason)
            result = result with { NumberingConflict = $"Filename season {filenameSeason} differs from folder season {folderSeason}; using filename." };
        var absoluteFolder = Regex.IsMatch(seasonFolderName, @"(?i)\bEP\s*\d{3,4}\b");
        if (number is not null && filenameSeason is null && result.Kind == "Anime" && (!seasonFolder.Success || absoluteFolder))
            result = result with { AbsoluteEpisode = number, AbsoluteEpisodeEnd = numbering?.End, NumberingOrder = "absolute" };
        // Unlabelled ranges retain absolute ordering unless a season folder confirms relative numbering.
        if (numbering?.End is not null && filenameSeason is null && (!seasonFolder.Success || absoluteFolder))
            result = result with { AbsoluteEpisode = number, AbsoluteEpisodeEnd = numbering.End, NumberingOrder = "absolute" };
        if (sourcePart is not null && filenameSeason is null && result.Episode is not null)
            result = result with { SourcePart = sourcePart, SourcePartEpisode = result.Episode, SourcePartEpisodeEnd = result.EpisodeEnd,
                Season = sourcePart > 1 ? null : result.Season, Episode = sourcePart > 1 ? null : result.Episode,
                EpisodeEnd = sourcePart > 1 ? null : result.EpisodeEnd, NumberingOrder = "absolute",
                AbsoluteEpisode = sourcePart > 1 ? null : result.AbsoluteEpisode };
        // Bonus clips sit in season 0 without numbering; a clip from an extras folder drops any numbers its file name carried.
        if (bonus && (structured || extraClip || besideEpisodes is not null) && (numbering is null || extraClip))
            result = result with { IsBonus = true, Kind = sourceKind == "Anime" || path.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.Equals("Anime", StringComparison.OrdinalIgnoreCase)) ? "Anime" : "Show",
                Season = 0, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, SourcePart = null, SourcePartEpisode = null, SourcePartEpisodeEnd = null,
                EpisodeTitle = BonusTitle(name, result.Series), NumberingOrder = "aired", NumberingConflict = null,
                // An extras folder inside "Season 2" holds that season's clips, so they also list at the end of season 2.
                AirsAfterSeason = extraClip ? Parts(dir).Reverse().Select(SeasonNumber).FirstOrDefault(n => n is > 0) : result.AirsAfterSeason };
        // A film's extras are clips of that film: no show, no numbering, listed on the film's page.
        if (filmExtra is not null)
            result = result with { IsBonus = true, ExtraOf = Identity(filmExtra), Title = name, EpisodeTitle = name, Year = null,
                Kind = sourceKind == "Anime" || path.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.Equals("Anime", StringComparison.OrdinalIgnoreCase)) ? "Anime" : "Movie",
                Series = null, SeriesPath = null, Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null,
                SourcePart = null, SourcePartEpisode = null, SourcePartEpisodeEnd = null, NumberingOrder = "aired", NumberingConflict = null, AirsAfterSeason = null };
        return LocalMetadata.Apply(LibraryIdentity.Ensure(result with { SourceLibraryKind = sourceKind, IsExtra = IsExtra(path) && !showExtras && filmExtra is null,
            NumberingSource = result.IsBonus ? null : sourcePart is not null ? "part" : numbering?.AirDate is not null ? "date" : filenameSeason is not null ? "filename" :
                seasonFolder.Success && number is not null && !absoluteFolder ? "season-folder" : result.AbsoluteEpisode is not null ? "absolute" : null }));
    }
    // "Komi Can't Communicate [Season 1 + 2 + Extras] [JPBD 1080p HEVC] (Batch)" is the show "Komi Can't Communicate":
    // bracketed tags and release notes in parentheses describe the download, not the title.
    internal static string StripReleaseTags(string name)
    {
        var cleaned = Regex.Replace(name, @"\s*\[[^\]]*\]", " ");
        cleaned = Regex.Replace(cleaned, @"\s*\((?=[^)]*(?i:\b(?:batch|complete|dual[ -]?audio|multi[ -]?sub|subs?|dub|bd|bluray|blu-ray|web-?dl|webrip|hdtv|\d{3,4}p|x26[45]|hevc|aac|flac|opus|remux)\b))[^)]*\)", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '-', '_', '.');
        return cleaned.Length > 0 ? cleaned : name;
    }
    // "Outlaw Star - NCOP1" is the clip "NCOP1" once the show's own name is dropped, and "Azumanga Daioh - XX - Gallery"
    // is "Gallery" once its placeholder episode number goes too.
    private static string BonusTitle(string name, string? series)
    {
        var rest = string.IsNullOrWhiteSpace(series) ? name : Regex.Replace(name, @"^" + Regex.Escape(series) + @"(?:\s*[-–:]\s*|\s+)", "", RegexOptions.IgnoreCase);
        rest = Regex.Replace(Regex.Replace(rest, @"^XX\s*[-–:]\s*", "", RegexOptions.IgnoreCase), @"^\(([^()]+)\)$", "$1");
        return rest.Length > 0 ? rest : name;
    }
    // The show whose numbered episodes share this folder, when this file's name or the folder's names it. Two episodes
    // are needed, so a stray numbered file beside a film can't claim it. The answer is cached until the folder changes;
    // the cache starts over past a few thousand folders.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, (string Series, int Count)[] Shows)> EpisodeFolders =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    // "Arrow - Pilot" is named after Arrow, but "Arrowhead" isn't, and "Fargo (1996)" beside Fargo's episodes is the film.
    // "Pokemon" names "Pokémon" and "Love and Peace" names "Love & Peace".
    private static string Fold(string text) => new string(text.Replace("&", " and ").Normalize(NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
    private static bool NamedAfter(string text, string series, bool clip)
    {
        text = Fold(text);
        var words = Regex.Split(Regex.Replace(Fold(series), @"(?i)^the\s+", ""), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0).Select(Regex.Escape).ToArray();
        if (words.Length == 0) return false;
        var match = Regex.Match(text, @"(?i)^(?:the[^\p{L}\p{N}]+)?" + string.Join(@"[^\p{L}\p{N}]*", words) + @"(?![\p{L}\p{N}])");
        if (!match.Success) return false;
        var rest = text[match.Length..].Trim(' ', '-', '–', ':', '.', '_');
        return !clip || rest.Length > 0 && !Regex.IsMatch(rest, @"^\(?(?:19|20)\d{2}(?!\d)");
    }
    private static string? EpisodeFolderSeries(string dir, string name, string? libraryRoot, bool seriesContext)
    {
        (string Series, int Count)[] shows;
        try
        {
            var key = dir + "|" + seriesContext;
            var written = Directory.GetLastWriteTimeUtc(dir);
            if (EpisodeFolders.TryGetValue(key, out var cached) && cached.Written == written) shows = cached.Shows;
            else
            {
                shows = Directory.EnumerateFiles(dir).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file)) && !IsExtra(file))
                    .Select(file => FilenameEpisodes.Read(file, seriesContext)).Where(found => found is { Episode: not null } && found.Season != 0 && found.Conflict is null)
                    .Select(found => string.IsNullOrWhiteSpace(found!.Series) ? System.IO.Path.GetFileName(dir) : found.Series)
                    .GroupBy(series => MetadataTitles.Normalize(series)).Where(group => group.Count() >= 2).Select(group => (group.First(), group.Count())).ToArray();
                if (EpisodeFolders.Count > 4096) EpisodeFolders.Clear();
                EpisodeFolders[key] = (written, shows);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        var root = libraryRoot is not null && string.Equals(System.IO.Path.GetFullPath(libraryRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar), dir,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var folder = StripReleaseTags(System.IO.Path.GetFileName(dir));
        // A name that starts with the show's name is judged on that name alone: "Sherlock The Abominable Bride 2016" is a clip,
        // "Westworld 1973 REMASTERED" the film. Any other dated name is a film, so the folder can't claim it.
        var dated = Regex.IsMatch(name, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        return shows.OrderByDescending(show => show.Count).Select(show => show.Series).FirstOrDefault(series =>
            MetadataTitles.Normalize(series).Length >= 2 && (NamedAfter(name, series, clip: false)
                ? NamedAfter(name, series, clip: true) : !root && !dated && NamedAfter(folder, series, clip: false)));
    }
    private static bool IsReleaseDirectory(string directory) => Regex.IsMatch(
        Regex.Replace(System.IO.Path.GetFileName(directory), @"[._]+", " "),
        @"(?i)(?:^|\s)(?:S\d{1,2}(?:E\d{1,3})?|Season\s*\d{1,2})(?=\b|[ -])|^\d+\s*(?:сезон|sezon)\b|^Specials$|^Part\s*\d+\s*$") ||
        Regex.IsMatch(System.IO.Path.GetFileName(directory), ExtrasFolders);
}
