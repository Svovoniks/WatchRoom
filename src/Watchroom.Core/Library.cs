using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public sealed class LibraryStore
{
    public event Action<MediaItem>? MediaSaved;
    private readonly string connection;
    public LibraryStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connection = new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "library.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS media(id TEXT PRIMARY KEY, path TEXT UNIQUE NOT NULL, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE IF NOT EXISTS shows(id TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS seasons(id TEXT PRIMARY KEY, show_id TEXT NOT NULL, json TEXT NOT NULL);";
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
            if (FileNames.IsExtra(item.Path))
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
    private SqliteConnection Open() { var db = new SqliteConnection(connection); db.Open(); return db; }
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
    private void AttachLooseReleases()
    {
        var items = All().Where(x => x.Series is not null && !x.IsExtra).ToArray();
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
        var shows = Shows().ToDictionary(x => x.Id); var seasons = Seasons().ToDictionary(x => x.Id);
        return items.Select(item =>
        {
            if (item.ShowId is { } showId && shows.TryGetValue(showId, out var show))
            {
                var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var id in show.ProviderIds) ids.TryAdd(id.Key, id.Value);
                item = item with { Series = LibraryIdentity.Locked(item, "Title") ? item.Series : show.Title, Kind = show.Kind,
                    Year = LibraryIdentity.Locked(item, "Year") ? item.Year : show.Year, SeriesPoster = show.Poster ?? item.SeriesPoster,
                    Overview = LibraryIdentity.Locked(item, "Overview") ? item.Overview : show.Overview ?? item.Overview,
                    ProviderIds = ids, NumberingOrder = show.NumberingOrder };
            }
            if (item.SeasonId is { } seasonId && seasons.TryGetValue(seasonId, out var season)) item = item with
            {
                SeasonTitle = LibraryIdentity.Locked(item, "SeasonTitle") ? item.SeasonTitle : season.Title ?? item.SeasonTitle,
                SeasonOverview = LibraryIdentity.Locked(item, "SeasonOverview") ? item.SeasonOverview : season.Overview ?? item.SeasonOverview,
                SeasonPoster = season.Poster ?? item.SeasonPoster
            };
            return item;
        }).OrderBy(x => x.DisplayTitle).ThenBy(x => x.Season).ThenBy(x => x.Episode).ToList();
    }
    public void Save(MediaItem item)
    {
        item = LibraryIdentity.Ensure(item);
        using var db = Open(); using var c = db.CreateCommand();
        using var transaction = db.BeginTransaction(); c.Transaction = transaction;
        using var prior = db.CreateCommand(); prior.Transaction = transaction;
        prior.CommandText = "SELECT json FROM media WHERE id=$id"; prior.Parameters.AddWithValue("$id", item.Id);
        var previous = prior.ExecuteScalar() is string old ? Wire.Read<MediaItem>(old) : null;
        var identityChanged = previous is not null && (previous.MetadataType != item.MetadataType && item.MetadataType is not null ||
            previous.MetadataKind != item.MetadataKind && item.MetadataKind is not null ||
            previous.MetadataProvider == item.MetadataProvider && previous.MetadataId is not null && item.MetadataId is not null && previous.MetadataId != item.MetadataId);
        c.CommandText = "INSERT INTO media VALUES($id,$path,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
        c.Parameters.AddWithValue("$id", item.Id); c.Parameters.AddWithValue("$path", item.Path); c.Parameters.AddWithValue("$json", Wire.Serialize(item)); c.ExecuteNonQuery();
        if (item.ShowId is { } showId && item.SeasonId is { } seasonId)
        {
            var show = new LibraryShow(showId, item.Series!, item.Kind, item.Year, item.SeriesPoster ?? item.Poster, item.Overview,
                new Dictionary<string, string>(item.ProviderIds ?? []), item.NumberingOrder);
            var season = new LibrarySeason(seasonId, showId, item.Season, item.SeasonTitle, item.SeasonOverview, item.SeasonPoster);
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
                    NumberingOrder = previous is null || item.NumberingOrder == previous.NumberingOrder ? current.NumberingOrder : show.NumberingOrder, ProviderIds = ids };
            }
            read.CommandText = "SELECT json FROM seasons WHERE id=$id"; read.Parameters["$id"].Value = seasonId;
            if (read.ExecuteScalar() is string seasonJson && !identityChanged)
            {
                var current = Wire.Read<LibrarySeason>(seasonJson);
                season = season with { Title = item.SeasonTitle is null || previous is not null && item.SeasonTitle == previous.SeasonTitle ? current.Title ?? season.Title : season.Title,
                    Overview = item.SeasonOverview is null || previous is not null && item.SeasonOverview == previous.SeasonOverview ? current.Overview ?? season.Overview : season.Overview,
                    Poster = item.SeasonPoster is null || previous is not null && item.SeasonPoster == previous.SeasonPoster ? current.Poster ?? season.Poster : season.Poster };
            }
            using var hierarchy = db.CreateCommand(); hierarchy.Transaction = transaction;
            hierarchy.CommandText = "INSERT INTO shows VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json; INSERT INTO seasons VALUES($season,$id,$seasonJson) ON CONFLICT(id) DO UPDATE SET json=$seasonJson;";
            hierarchy.Parameters.AddWithValue("$id", showId); hierarchy.Parameters.AddWithValue("$json", Wire.Serialize(show));
            hierarchy.Parameters.AddWithValue("$season", seasonId); hierarchy.Parameters.AddWithValue("$seasonJson", Wire.Serialize(season)); hierarchy.ExecuteNonQuery();
        }
        transaction.Commit();
        MediaSaved?.Invoke(item);
    }
    public List<LibraryShow> Shows() => ReadHierarchy<LibraryShow>("shows");
    public List<LibrarySeason> Seasons() => ReadHierarchy<LibrarySeason>("seasons");
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
    public void GroupShowsByProvider()
    {
        if (!MetadataOptions.Load(this).GroupShowsByProvider) return;
        foreach (var provider in new[] { "tmdb", "tvdb", "imdb" })
        {
            var items = All().Where(x => x.Series is not null && !x.IsExtra).ToArray();
            foreach (var group in items.Where(x => LibraryIdentity.ProviderId(x, provider) is not null).GroupBy(x => LibraryIdentity.ProviderId(x, provider)))
            {
                if (group.Select(x => x.MetadataKind ?? x.Kind).Distinct().Count() > 1) continue;
                if (provider != "tmdb" && group.Select(x => LibraryIdentity.ProviderId(x, "tmdb")).OfType<string>().Distinct().Count() > 1) continue;
                var ids = group.Select(LibraryIdentity.ShowKey).Distinct().Order(StringComparer.Ordinal).ToArray();
                if (ids.Length < 2) continue;
                foreach (var entry in items.Where(x => ids.Contains(LibraryIdentity.ShowKey(x)))) Save(entry with { ShowId = ids[0] });
            }
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
    public void Prune(IEnumerable<LibraryFolder> folders, IEnumerable<string>? exclusions = null)
    {
        var roots = folders.ToArray(); var excluded = exclusions?.ToArray() ?? [];
        var all = All();
        bool Retained(MediaItem item) => roots.Any(f => IsWithin(item.Path, f.Path)) && !excluded.Any(f => IsWithin(item.Path, f));
        var retainedShows = all.Where(x => !x.IsVirtual && Retained(x)).Select(LibraryIdentity.ShowKey).ToHashSet();
        var removed = all.Where(x => x.IsVirtual ? !retainedShows.Contains(LibraryIdentity.ShowKey(x)) : !Retained(x)).ToArray();
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var item in removed)
        {
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM media WHERE id=$id"; command.Parameters.AddWithValue("$id", item.Id); command.ExecuteNonQuery();
        }
        transaction.Commit();
        CleanHierarchy();
    }
    private void CleanHierarchy()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM seasons WHERE id NOT IN (SELECT json_extract(json, '$.seasonId') FROM media WHERE json_extract(json, '$.seasonId') IS NOT NULL); DELETE FROM shows WHERE id NOT IN (SELECT json_extract(json, '$.showId') FROM media WHERE json_extract(json, '$.showId') IS NOT NULL);";
        command.ExecuteNonQuery();
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
                    if (FileNames.IsExtra(path)) continue;
                    var parsed = FileNames.Parse(path, folder.Kind, folder.Path); seen.Add(parsed.Id);
                    var item = old.TryGetValue(parsed.Id, out var previous) ? previous with { Available = true, Year = previous.Matched || previous.MetadataType is not null ? previous.Year : parsed.Year, Kind = previous.MetadataKind ?? parsed.Kind, Series = previous.MetadataType == "movie" ? null : previous.MetadataType == "tv" || previous.Matched ? previous.Series : parsed.Series ?? previous.Series, Season = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Season : parsed.Season ?? previous.Season, Episode = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Episode : parsed.Episode ?? previous.Episode, SeriesPoster = parsed.SeriesPoster ?? previous.SeriesPoster, SeasonPoster = parsed.SeasonPoster ?? previous.SeasonPoster, Poster = previous.Matched && File.Exists(previous.Poster) ? previous.Poster : parsed.Poster ?? (File.Exists(previous.Poster) ? previous.Poster : null) } : parsed;
                    item = item with { ShowId = previous?.ShowId ?? parsed.ShowId, SeriesPath = parsed.SeriesPath ?? previous?.SeriesPath,
                        EpisodeEnd = previous?.AbsoluteEpisode is not null ? previous.EpisodeEnd : parsed.EpisodeEnd ?? previous?.EpisodeEnd,
                        AbsoluteEpisode = parsed.AbsoluteEpisode ?? previous?.AbsoluteEpisode, AbsoluteEpisodeEnd = parsed.AbsoluteEpisodeEnd ?? previous?.AbsoluteEpisodeEnd,
                        SourcePart = parsed.SourcePart ?? previous?.SourcePart, SourcePartEpisode = parsed.SourcePartEpisode ?? previous?.SourcePartEpisode,
                        SourcePartEpisodeEnd = parsed.SourcePartEpisodeEnd ?? previous?.SourcePartEpisodeEnd,
                        NumberingConflict = parsed.NumberingConflict, NumberingOrder = previous?.NumberingOrder ?? parsed.NumberingOrder };
                    var sourceNumbering = parsed.NumberingSource is "filename" or "season-folder";
                    if (sourceNumbering && previous is not null && CanRepairGrouping(previous) && previous.NumberingOrder != "dvd")
                        item = item with { Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd,
                            AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, NumberingOrder = "aired" };
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
                    }
                    item = LocalMetadata.Apply(item with { NumberingSource = parsed.NumberingSource,
                        SourceLibraryKind = parsed.SourceLibraryKind, IsExtra = false });
                    Save(item); progress?.Report($"Found {seen.Count} videos · {item.Title}");
                }
            }
            foreach (var item in old.Values.Where(x => !seen.Contains(x.Id) && !x.IsVirtual))
                Save(item with { IsExtra = FileNames.IsExtra(item.Path), Available = FileNames.IsExtra(item.Path) && File.Exists(item.Path) });
            AttachLooseReleases();
            GroupShowsByProvider();
            return All();
        }, cancellation);
    }
}

public static partial class FileNames
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".mpeg", ".mpg", ".ts", ".m2ts", ".wmv", ".flv", ".ogv" };
    public static bool IsExtra(string path) => !Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)\bS00[._ ]*E\d+\b|\b0x\d+\b") && (path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
        .SkipLast(1).Any(part => Regex.IsMatch(part, @"(?i)^(?:extras|trailers|featurettes|behind the scenes|samples)$")) ||
        Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:^|[-._ ])(?:trailer|sample|featurette)\d*$"));
    public static MediaItem Parse(string path, string kind = "Mixed", string? libraryRoot = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var sourceKind = kind;
        var identityPath = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityPath)))[..32];
        var name = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(path), @"\[[^\]]*\]", " ");
        name = Regex.Replace(name, @"[._]+", " ").Trim();
        // Release details can contain audio layouts such as 2.0x3. They are not episodes.
        var episodeName = Regex.Split(name, @"(?i)\b(?:BDRip|BRRip|2160p|1080p|720p|480p|\d{3,4}x\d{3,4}p?|WEB-DL|WEBRip|BluRay|x264|x265|HEVC)\b")[0].TrimEnd(' ', '(');
        var episode = Regex.Match(episodeName, @"(?i)\bS(\d{1,2})\s*E(\d{1,3})(?:v\d+)?\b|\b(\d{1,2})x(\d{1,3})(?:v\d+)?\b");
        var localized = Regex.Match(episodeName, @"(?i)\b(\d{1,2})\s*сезон\s*(\d{1,4})\s*сери(?:я|и|й)\b");
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var seriesContext = kind is "Show" or "Anime" || kind == "Mixed" && path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => Regex.IsMatch(part, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$"));
        int? season = null, number = null; string? series = null;
        if (localized.Success)
        {
            season = int.Parse(localized.Groups[1].Value); number = int.Parse(localized.Groups[2].Value);
            series = episodeName[..localized.Index].Trim(' ', '-');
        }
        else if (episode.Success)
        {
            season = int.Parse(episode.Groups[1].Success ? episode.Groups[1].Value : episode.Groups[3].Value);
            number = int.Parse(episode.Groups[2].Success ? episode.Groups[2].Value : episode.Groups[4].Value);
            series = episodeName[..episode.Index].Trim(' ', '-');
        }
        else
        {
            var numbered = Regex.Match(episodeName, @"(?i)(?:(?:^|\s)-\s(?:E(?:P(?:ISODE)?)?\s*)?|\sE(?:P(?:ISODE)?)?\s*)(\d{1,4})(?:\s*v\d+)?(?:\s+END)?\s*$");
            // A dotted acronym ending in E immediately before a film year is not an episode token.
            var acronymYear = Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:[a-z]\.){2,}E[. _-]*(?:19|20)\d{2}\b");
            if (numbered.Success && !acronymYear)
            {
                series = episodeName[..numbered.Index].Trim(' ', '-'); season = 1; number = int.Parse(numbered.Groups[1].Value);
                if (kind == "Mixed" && Regex.IsMatch(System.IO.Path.GetFileName(path), @"^\[[^\]]+\]")) kind = "Anime";
            }
            else
            {
                var single = Regex.Match(episodeName, @"(?i)\b(\d{1,4})\s*(?:серия|эпизод)\b");
                var prefix = Regex.Match(episodeName, @"^\s*(\d{2,4})\s+\S");
                var suffix = Regex.Match(episodeName, @"\s+(\d{2,4})\s*$");
                if (single.Success) { series = episodeName[..single.Index].Trim(' ', '-'); season = 1; number = int.Parse(single.Groups[1].Value); }
                else if (seriesContext && prefix.Success && int.Parse(prefix.Groups[1].Value) < 1900)
                { series = System.IO.Path.GetFileName(dir); season = 1; number = int.Parse(prefix.Groups[1].Value); }
                else if (seriesContext && suffix.Success && int.Parse(suffix.Groups[1].Value) < 1900)
                { series = episodeName[..suffix.Index].Trim(); season = 1; number = int.Parse(suffix.Groups[1].Value); }
            }
        }
        var filenameSeason = episode.Success || localized.Success ? season : null;
        var seasonFolderName = System.IO.Path.GetFileName(dir);
        var seasonFolder = Regex.Match(Regex.Replace(seasonFolderName, @"[._]+", " "), @"(?i)(?:^|\s)(?:Season\s*|S)(\d{1,2})(?!\d|E\d)\b|^(\d+)\s*(?:сезон|sezon)\b|^Specials$");
        if (seasonFolder.Success)
        {
            season = filenameSeason ?? (seasonFolder.Groups[1].Success ? int.Parse(seasonFolder.Groups[1].Value) : seasonFolder.Groups[2].Success ? int.Parse(seasonFolder.Groups[2].Value) : 0);
            if (string.IsNullOrWhiteSpace(series) || series == seasonFolderName)
                series = seasonFolder.Index > 0 ? seasonFolderName[..seasonFolder.Index].Trim() : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir)!);
        }
        if (number is not null && string.IsNullOrWhiteSpace(series)) series = System.IO.Path.GetFileName(dir);
        // Dates in an episode title are not the premiere year of the entire series.
        var years = Regex.Matches(series ?? episodeName, @"\b(19\d{2}|20\d{2})\b");
        var year = series is null && years.Count > 1 ? years[^1] : years.Count > 0 ? years[0] : Match.Empty;
        if (series is not null) series = Regex.Replace(Regex.Replace(Regex.Replace(series, @"[._]+", " "), @"\s*\(?(?:19|20)\d{2}\)?\s*$", ""), @"\s+", " ").Trim();
        var title = series ?? (year.Success ? name[..year.Index] : name);
        title = Regex.Split(title, @"(?i)\b(?:2160p|1080p|720p|480p|WEB-DL|WEBRip|BluRay|x264|x265|HEVC)\b")[0].Trim(' ', '-', '(');
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);
        string? Artwork(string folder, params string[] stems) => stems.SelectMany(stem => new[] { ".jpg", ".jpeg", ".png" }.Select(ext => System.IO.Path.Combine(folder, stem + ext))).FirstOrDefault(File.Exists);
        var localPoster = Artwork(dir, System.IO.Path.GetFileNameWithoutExtension(path), "poster", "folder", "cover");
        var seriesDir = dir;
        int? sourcePart = null;
        while (IsReleaseDirectory(seriesDir) && System.IO.Path.GetDirectoryName(seriesDir) is { } parent &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            var part = Regex.Match(System.IO.Path.GetFileName(seriesDir), @"(?i)^Part\s*(\d+)\s*$");
            if (part.Success) sourcePart = int.Parse(part.Groups[1].Value);
            seriesDir = parent;
        }
        var seriesPoster = series is null ? null : Artwork(seriesDir, "poster", "folder", "cover");
        var seasonPoster = series is null ? null : Artwork(dir, "poster", "folder", "cover") ?? Artwork(seriesDir, $"season{season ?? 1:00}-poster", $"season{season ?? 1}-poster");
        var result = new MediaItem(id, path, title, kind == "Mixed" ? (series is null ? "Movie" : "Show") : kind,
            year.Success ? int.Parse(year.Value) : null, series, season, number, localPoster ?? seriesPoster,
            SeriesPoster: seriesPoster, SeasonPoster: seasonPoster);
        // Structured show folders own membership, regardless of aliases in individual filenames.
        var parentName = System.IO.Path.GetFileName(seriesDir);
        var structured = File.Exists(System.IO.Path.Combine(seriesDir, "tvshow.nfo")) || number is not null && (seasonFolder.Success || seriesContext || seriesDir != dir) &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) &&
            !Regex.IsMatch(parentName, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$");
        if (structured)
        {
            var folderTitle = parentName;
            var folderYear = Regex.Match(folderTitle, @"\s*\((19\d{2}|20\d{2})\)\s*$");
            folderTitle = Regex.Replace(folderTitle, @"\s*\((?:19|20)\d{2}\)\s*$", "");
            folderTitle = Regex.Replace(Regex.Replace(folderTitle, @"[._]+", " "), @"\s+", " ").Trim();
            result = result with { Series = folderTitle, Title = folderTitle, SeriesPath = seriesDir,
                Year = folderYear.Success ? int.Parse(folderYear.Groups[1].Value) : result.Year,
                ShowId = "show-" + LibraryIdentity.Hash(OperatingSystem.IsWindows() ? seriesDir.ToUpperInvariant() : seriesDir) };
        }
        var range = Regex.Match(episodeName, @"(?i)\bS\d{1,2}\s*E(\d{1,3})(?:\s*[-]?\s*E|\s*-\s*)(\d{1,3})\b");
        if (range.Success && int.TryParse(range.Groups[2].Value, out var end) && end >= number && end - number < 100) result = result with { EpisodeEnd = end };
        var folderSeason = seasonFolder.Success ? seasonFolder.Groups[1].Success ? int.Parse(seasonFolder.Groups[1].Value) : seasonFolder.Groups[2].Success ? int.Parse(seasonFolder.Groups[2].Value) : 0 : (int?)null;
        if (filenameSeason is not null && folderSeason is not null && filenameSeason != folderSeason && episode.Success)
            result = result with { NumberingConflict = $"Filename season {filenameSeason} differs from folder season {folderSeason}; using filename." };
        var absoluteFolder = Regex.IsMatch(seasonFolderName, @"(?i)\bEP\s*\d{3,4}\b");
        if (number is not null && !episode.Success && !localized.Success && result.Kind == "Anime" && (!seasonFolder.Success || absoluteFolder))
            result = result with { AbsoluteEpisode = number, NumberingOrder = "absolute" };
        var absoluteRange = Regex.Match(episodeName, @"(?i)^(.*?)\s+-\s+(\d{1,4})\s*-\s*(\d{1,4})\s*$");
        if (absoluteRange.Success && int.TryParse(absoluteRange.Groups[2].Value, out var start) && int.TryParse(absoluteRange.Groups[3].Value, out var finish) && start < 1900 && finish >= start && finish - start < 100)
            result = result with { Series = result.Series ?? absoluteRange.Groups[1].Value.Trim(), Title = result.Series ?? absoluteRange.Groups[1].Value.Trim(),
                Kind = kind == "Mixed" ? Regex.IsMatch(System.IO.Path.GetFileName(path), @"^\[[^\]]+\]") ? "Anime" : "Show" : kind, Season = result.Season ?? 1, Episode = start, EpisodeEnd = finish, AbsoluteEpisode = start, AbsoluteEpisodeEnd = finish, NumberingOrder = "absolute" };
        if (sourcePart is not null && filenameSeason is null && result.Episode is not null)
            result = result with { SourcePart = sourcePart, SourcePartEpisode = result.Episode, SourcePartEpisodeEnd = result.EpisodeEnd,
                Season = sourcePart > 1 ? null : result.Season, Episode = sourcePart > 1 ? null : result.Episode,
                EpisodeEnd = sourcePart > 1 ? null : result.EpisodeEnd, NumberingOrder = "absolute",
                AbsoluteEpisode = sourcePart > 1 ? null : result.AbsoluteEpisode };
        return LocalMetadata.Apply(LibraryIdentity.Ensure(result with { SourceLibraryKind = sourceKind, IsExtra = IsExtra(path),
            NumberingSource = sourcePart is not null ? "part" : filenameSeason is not null ? "filename" :
                seasonFolder.Success && number is not null && !absoluteFolder ? "season-folder" : result.AbsoluteEpisode is not null ? "absolute" : null }));
    }
    private static bool IsReleaseDirectory(string directory) => Regex.IsMatch(
        Regex.Replace(System.IO.Path.GetFileName(directory), @"[._]+", " "),
        @"(?i)(?:^|\s)(?:S\d{1,2}(?:E\d{1,3})?|Season\s*\d{1,2})(?=\b|[ -])|^\d+\s*(?:сезон|sezon)\b|^Specials$|^Part\s*\d+\s*$");
}
