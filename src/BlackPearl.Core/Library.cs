using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

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
                    MetadataKind = null, MetadataType = null, MetadataProvider = null, MetadataId = null, ProviderTitle = null,
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
        // A library that already holds files still has the previous filename parser's readings; its next scan replaces them.
        if (Setting(FilenameReaderVersion) is null) Setting(FilenameReaderVersion, All().Any(x => !x.IsVirtual && !x.IsMusic) ? "pending" : CurrentFilenameReader);
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
                MetadataProvider = null, MetadataId = null, MetadataKind = null, MetadataType = null, ProviderIds = [], ProviderTitle = null,
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
    private const string FilenameReaderVersion = "filename-reader-version", CurrentFilenameReader = "1";
    // Back up through SQLite so an active WAL is included.
    private void BackUpBeforeFilenameReader()
    {
        var backupPath = System.IO.Path.Combine(DataDirectory, "library-before-filename-reader-v1.db");
        if (File.Exists(backupPath)) return;
        // Written aside first, so a crash partway through can't leave a partial backup that counts as done.
        var partial = backupPath + ".partial";
        File.Delete(partial);
        using (var source = Open())
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = partial, Pooling = false }.ToString()))
        { backup.Open(); source.BackupDatabase(backup); }
        File.Move(partial, backupPath);
    }
    private void DropMetadataRetries()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key LIKE 'metadata-retry:%'";
        command.ExecuteNonQuery();
    }
    // What the previous filename parser read for this file goes, and the new reading takes its place. A provider's identity and
    // text weren't parsed, so they stay, unless the new reading contradicts them: an identified film the name now shows to be an
    // episode. Numbering a provider resolved stays when it came from the number the file still reads as.
    private static MediaItem ForgetOldReading(MediaItem parsed, MediaItem previous)
    {
        var episodic = parsed.Episode is not null || parsed.SourcePartEpisode is not null || parsed.AirDate is not null;
        // A bare number can't overturn a film a provider identified, as "Rocky 2" beside "Rocky 3" reads as a run.
        if (previous.MetadataType == "movie" && parsed.Series is not null && episodic && parsed.NumberingSource is null) return previous;
        // A numbered film part can have been verified as a miniseries, so that identity stands.
        // So does a file now read as a special of a different show than the one it was stored in, or that show's match would pass to its new show.
        var contradicted = previous.MetadataType == "tv" && parsed.Season == 0 && previous.Season is > 0 && parsed.Series is { } specialOf && previous.Series is { } storedIn && MetadataTitles.Normalize(specialOf) != MetadataTitles.Normalize(storedIn) ||
            previous.MetadataType == "movie" && parsed.Series is not null && episodic ||
            previous.MetadataType == "tv" && parsed.Series is null && MetadataTitles.NumberedMoviePart(parsed) is null;
        // The wrong title's plot and poster go with its identity, or a later correct match would keep them.
        var item = contradicted ? AutomaticArtwork.ResetIdentity(previous) with { MetadataKind = null, MetadataType = null, ProviderIds = null, ArtworkFetchedAt = 0 } : previous;
        var identified = item.MetadataId is not null;
        // A file now read as a special isn't the same number as the episode it was stored as, whatever the digits say.
        var sameNumber = !(parsed.Season == 0 && previous.Season is > 0) && (previous.AbsoluteEpisode is not null && previous.AbsoluteEpisode == (parsed.AbsoluteEpisode ?? parsed.Episode) && previous.AbsoluteEpisodeEnd == (parsed.AbsoluteEpisodeEnd ?? parsed.EpisodeEnd) ||
            previous.SourcePartEpisode is not null && previous.SourcePartEpisode == parsed.SourcePartEpisode && previous.SourcePart == parsed.SourcePart ||
            previous.Season == parsed.Season && previous.Episode == parsed.Episode && previous.EpisodeEnd == parsed.EpisodeEnd ||
            // A miniseries verified from numbered film parts keeps the numbering it was verified with.
            identified && item.MetadataType == "tv" && parsed.Series is null && MetadataTitles.NumberedMoviePart(parsed) is not null);
        item = item with
        {
            Title = identified ? item.Title : parsed.Title, Kind = identified ? item.Kind : parsed.Kind,
            // A numbered file that states no year doesn't say the show has none.
            Year = identified ? item.Year : parsed.Year ?? (parsed.Series is not null ? item.Year : null),
            Series = identified && item.MetadataType == "tv" ? item.Series : parsed.Series,
            ShowId = identified && item.MetadataType == "tv" && parsed.Series is null ? item.ShowId : parsed.ShowId, SeasonId = null, SeriesPath = identified && item.MetadataType == "tv" && parsed.Series is null ? item.SeriesPath : parsed.SeriesPath, ExtraOf = parsed.ExtraOf
        };
        if (!sameNumber)
            item = item with
            {
                Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd, AbsoluteEpisode = parsed.AbsoluteEpisode, AbsoluteEpisodeEnd = parsed.AbsoluteEpisodeEnd,
                SourcePart = parsed.SourcePart, SourcePartEpisode = parsed.SourcePartEpisode, SourcePartEpisodeEnd = parsed.SourcePartEpisodeEnd,
                NumberingOrder = EpisodeOrder.ProviderDefined(item.NumberingOrder) ? item.NumberingOrder : parsed.NumberingOrder,
                // These described the episode the old reading pointed at.
                EpisodeTitle = parsed.EpisodeTitle, EpisodeOverview = null, EpisodePoster = null, EpisodePosterQualityPath = null, AirDate = parsed.AirDate, EpisodeSource = null, MetadataFetchedAt = 0,
                SeasonTitle = item.Season == parsed.Season ? item.SeasonTitle : null, SeasonOverview = item.Season == parsed.Season ? item.SeasonOverview : null, SeasonSource = item.Season == parsed.Season ? item.SeasonSource : null
            };
        else if (item.EpisodeSource is null) item = item with { EpisodeTitle = parsed.EpisodeTitle, AirDate = parsed.AirDate };
        return TmdbRetention.Rebase(KeepLockedText(item, previous), parsed);
    }
    // A single field the user locked stays as they set it, whatever the new reading does to the rest of the item.
    private static MediaItem KeepLockedText(MediaItem item, MediaItem previous)
    {
        bool Locked(string field) => LibraryIdentity.Locked(previous, field);
        return item with
        {
            Year = Locked("Year") ? previous.Year : item.Year,
            EpisodeTitle = Locked("EpisodeTitle") ? previous.EpisodeTitle : item.EpisodeTitle,
            EpisodeOverview = Locked("EpisodeOverview") ? previous.EpisodeOverview : item.EpisodeOverview,
            EpisodePoster = Locked("EpisodePoster") ? previous.EpisodePoster : item.EpisodePoster,
            EpisodePosterQualityPath = Locked("EpisodePoster") ? previous.EpisodePosterQualityPath : item.EpisodePosterQualityPath,
            EpisodeSource = Locked("EpisodeTitle") || Locked("EpisodeOverview") || Locked("EpisodePoster") ? previous.EpisodeSource : item.EpisodeSource,
            AirDate = Locked("AirDate") ? previous.AirDate : item.AirDate,
            SeasonTitle = Locked("SeasonTitle") ? previous.SeasonTitle : item.SeasonTitle,
            SeasonOverview = Locked("SeasonOverview") ? previous.SeasonOverview : item.SeasonOverview,
            SeasonSource = Locked("SeasonTitle") || Locked("SeasonOverview") ? previous.SeasonSource : item.SeasonSource
        };
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
                        MetadataKind = null, MetadataType = null, MetadataProvider = null, MetadataId = null, ProviderIds = [], ProviderTitle = null,
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
    private int DropReplacedReleases(LibraryFolder[] roots)
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
            // A file whose library folder is away, such as an unplugged drive, wasn't replaced; another copy holding its episode leaves it be.
            if (roots.Where(r => IsWithin(gone.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault() is { } root && !Directory.Exists(root.Path)) continue;
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
        return ReadCatalog();
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
        item = LibraryRuntimes.Apply(LibraryIdentity.Ensure(TmdbRetention.TrackLegacy(item)));
        lock (writeGate) Write(item);
        MediaSaved?.Invoke(item);
    }
    // Records the length read from a video's file header. The entry is re-read under the write
    // lock, so a metadata save that lands first is kept rather than replaced with an older copy.
    public MediaItem? FillFileRuntime(string id, int minutes)
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
                if (!LibraryRuntimes.NeedsRuntime(current)) return null;
                updated = LibraryRuntimes.Apply(current with { FileRuntimeMinutes = Math.Max(0, minutes) });
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
            previous.MetadataProvider == item.MetadataProvider && previous.MetadataId is not null && item.MetadataId is not null && previous.MetadataId != item.MetadataId ||
            // A dropped match takes its show's plot, poster and IDs with it, or the show would keep describing the wrong title.
            previous.MetadataProvider is not null && item.MetadataProvider is null && item.MetadataId is null && (item.ProviderIds?.Count ?? 0) == 0);
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
        if (!MetadataOptions.Load(this).CombineShowFolders) { MarkShowsWithCopies(null); return; }
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
                var members = items.Where(x => ids.Contains(LibraryIdentity.ShowKey(x))).ToArray();
                if (SharesSeason(members) && !(SameNumbering(members) && OrdersSettled(members))) continue;
                foreach (var entry in members) { Save(entry with { ShowId = ids[0] }); merged = true; }
                RemoveVirtual(RedundantPlaceholders(members, provider));
            }
            if (merged) items = null;
        }
        MarkShowsWithCopies(items);
        CleanHierarchy();
    }
    private void MarkShowsWithCopies(MediaItem[]? items)
    {
        foreach (var show in (items ?? All().Where(x => x.Series is not null && !x.IsExtra && !x.IsMusic).ToArray()).GroupBy(LibraryIdentity.ShowKey))
            if (EpisodeOrder.HoldsCopies(show)) EpisodeOrder.MarkCopies(this, show.Key);
    }
    private static IEnumerable<MediaItem> NumberedEpisodes(IEnumerable<MediaItem> members) => members.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus && x.Season > 0);
    private static bool SharesSeason(IEnumerable<MediaItem> members) => NumberedEpisodes(members)
        .GroupBy(x => x.Season).Any(season => season.Select(LibraryIdentity.ShowKey).Distinct().Count() > 1);
    // Two folders holding the same season are copies of it, such as another release, and become versions of one show's episodes.
    // That needs their numbers to mean the same episodes, and the combined show keeps one order for all its files, so copies in
    // different orders, such as aired and DVD or running numbers and seasons, stay apart.
    private static bool SameNumbering(IEnumerable<MediaItem> members) => NumberedEpisodes(members)
        .Select(x => x.NumberingOrder).Distinct(StringComparer.Ordinal).Count() == 1;
    // A copy whose own episode titles can move it to another order waits until a fetch has weighed exactly those titles to the order it
    // holds; titled files added since need weighing too. Titles too few to choose, or an order titles don't change, leave a copy as it
    // is. A weighing that failed or never ran keeps the copies apart, and so does a titled file that is offline or locked, since a fetch
    // weighs only available, unlocked files. Once combined, the show keeps its order (EpisodeOrder.HoldsCopies).
    private bool OrdersSettled(IEnumerable<MediaItem> members) => NumberedEpisodes(members).GroupBy(LibraryIdentity.ShowKey).All(copy =>
    {
        var files = copy.ToArray(); var evidence = EpisodeOrder.Collect(files);
        if (evidence.Length < EpisodeOrder.MinimumEvidence || EpisodeOrder.HoldsCopies(files) || EpisodeOrder.MarkedCopies(this, copy.Key)) return true;
        // Whether titles can move the copy depends on the files a fetch weighs, as there.
        var weighable = files.Where(x => x.Available && !x.MetadataLocked).ToArray();
        if (weighable.Length > 0 && !EpisodeOrder.CanChoose(weighable)) return true;
        return SeriesMetadata.OrderWeighed(this, copy.Key) == EpisodeOrder.Weighed(files[0].NumberingOrder, evidence);
    });
    // Each folder listed the others' episodes as missing. Once combined, an episode with a file isn't missing, and one that is needs listing once.
    private static IEnumerable<string> RedundantPlaceholders(IReadOnlyCollection<MediaItem> members, string provider)
    {
        var held = members.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus)
            .SelectMany(x => LibraryIdentity.EpisodeNumbers(x).Select(number => (x.Season, number))).ToHashSet();
        return members.Where(x => x.IsVirtual).GroupBy(x => (x.Season, x.Episode)).SelectMany(episode => episode.Key.Episode is { } number && held.Contains((episode.Key.Season, number))
            ? episode.Select(x => x.Id)
            : episode.OrderByDescending(x => x.MetadataProvider == provider).Skip(1).Select(x => x.Id));
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
        Interlocked.Increment(ref FileNames.ScanGeneration);
        return await Task.Run(() =>
        {
            var changes = Prune(roots, excluded);
            var forgetOldReadings = Setting(FilenameReaderVersion) == "pending";
            if (forgetOldReadings) { BackUpBeforeFilenameReader(); DropMetadataRetries(); }
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
                    else
                    {
                        // One file that can't be read must not stop the scan.
                        try { parsed = FileNames.Parse(path, folder.Kind, folder.Path) with { FileSize = file.Length }; }
                        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException or OverflowException or InvalidOperationException or RegexMatchTimeoutException)
                        {
                            PlaybackDiagnostics.Record("library-file-unreadable", new { error = ex.GetType().Name });
                            continue;
                        }
                    }
                    if (parsed.IsExtra) continue;
                    // Merge into the record as stored now: edits and fetches can land while a scan runs.
                    MediaItem item;
                    lock (writeGate)
                    {
                        var previous = ByIds([parsed.Id]).FirstOrDefault() ?? ByPath(parsed.Path);
                        item = music ? previous is null ? parsed : MusicFiles.Merge(parsed, previous)
                            : previous is null ? MergeScanned(parsed, null) : MergeScanned(parsed with { Id = previous.Id }, forgetOldReadings && !previous.IsVirtual && CanRepairGrouping(previous) ? ForgetOldReading(parsed, previous) : previous) with { FileSize = parsed.FileSize };
                        // A replaced file has its length read again, and the old file's length isn't shown for it meanwhile.
                        if (previous is not null && !music && previous.FileSize != parsed.FileSize && previous.FileRuntimeMinutes is not null)
                            item = item with { FileRuntimeMinutes = null, RuntimeMinutes = previous.FileRuntimeMinutes > 0 && previous.RuntimeMinutes == previous.FileRuntimeMinutes &&
                                LibraryRuntimes.Overrides(previous) ? null : item.RuntimeMinutes };
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
                    var extra = stored.IsExtra || FileNames.IsExtra(stored.Path, root?.Path) && (!stored.IsBonus || FileNames.Parse(stored.Path, root?.Kind ?? "Mixed", root?.Path).IsExtra);
                    var missing = stored with { IsExtra = extra, Available = extra && File.Exists(stored.Path) };
                    if (missing != stored) { Save(missing); changes++; }
                }
            changes += DropReplacedReleases(roots);
            AttachLooseReleases();
            MergeDuplicateShows();
            GroupShowsByProvider();
            changes += WantedTitles.Attach(this, DataDirectory);
            // A folder that's offline keeps its old readings, so the step stays open until the folder has been read.
            if (forgetOldReadings && roots.Where(root => root.Kind != "Music").All(root => Directory.Exists(root.Path))) { CleanHierarchy(); Setting(FilenameReaderVersion, CurrentFilenameReader); }
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
    // A lookup keeps a title the file already has while still marking the episode as looked up, so TMDB's own titles are the ones
    // retention tracks. Other providers' aren't tracked, so anything they looked up counts as theirs.
    private static bool EpisodeTitleFromProvider(MediaItem item) => item.TmdbFields?.ContainsKey("episodeTitle") == true ||
        item.EpisodeSource is { } source && !source.Contains("themoviedb.org", StringComparison.OrdinalIgnoreCase);
    // The show name the scan gave a file, before a provider's title replaced it.
    private static string? ReadSeries(MediaItem item)
    {
        if (item.TmdbFields?.TryGetValue("series", out var tracked) != true) return item.Series;
        try { return System.Text.Json.JsonSerializer.Deserialize<string?>(tracked!.Original) ?? item.Series; }
        catch (System.Text.Json.JsonException) { return item.Series; }
    }
    private static MediaItem MergeScanned(MediaItem parsed, MediaItem? previous)
    {
        var item = previous is not null ? previous with { Path = parsed.Path, Available = true, Year = previous.Matched || previous.MetadataType is not null ? previous.Year : parsed.Year, Kind = previous.MetadataKind ?? (previous.ShowId is not null && previous.ShowId != parsed.ShowId ? previous.Kind : parsed.Kind), Series = previous.MetadataType == "movie" ? null : previous.MetadataType == "tv" || previous.Matched ? previous.Series : parsed.Series ?? previous.Series, Season = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Season : parsed.Season ?? previous.Season, Episode = previous.MetadataType == "movie" ? null : previous.AbsoluteEpisode is not null ? previous.Episode : parsed.Episode ?? previous.Episode, SeriesPoster = parsed.SeriesPoster ?? previous.SeriesPoster, SeasonPoster = parsed.SeasonPoster ?? previous.SeasonPoster, Poster = previous.Matched && File.Exists(previous.Poster) ? previous.Poster : parsed.Poster ?? (File.Exists(previous.Poster) ? previous.Poster : null) } : parsed;
        item = item with { ShowId = previous?.ShowId ?? parsed.ShowId, SeriesPath = parsed.SeriesPath ?? previous?.SeriesPath,
            // A clip's title follows its file name, and so does an episode's unless metadata gave it one.
            EpisodeTitle = LibraryIdentity.Locked(item, "EpisodeTitle") ? item.EpisodeTitle : parsed.IsBonus && !item.Matched ? parsed.EpisodeTitle ?? item.EpisodeTitle :
                parsed.IsBonus || EpisodeTitleFromProvider(item) ? item.EpisodeTitle ?? parsed.EpisodeTitle : parsed.EpisodeTitle,
            AirDate = LibraryIdentity.Locked(item, "AirDate") ? item.AirDate : parsed.AirDate ?? item.AirDate,
            EpisodeEnd = previous?.AbsoluteEpisode is not null ? previous.EpisodeEnd : parsed.EpisodeEnd ?? previous?.EpisodeEnd,
            AbsoluteEpisode = parsed.AbsoluteEpisode ?? previous?.AbsoluteEpisode, AbsoluteEpisodeEnd = parsed.AbsoluteEpisodeEnd ?? previous?.AbsoluteEpisodeEnd,
            SourcePart = parsed.SourcePart ?? previous?.SourcePart, SourcePartEpisode = parsed.SourcePartEpisode ?? previous?.SourcePartEpisode,
            SourcePartEpisodeEnd = parsed.SourcePartEpisodeEnd ?? previous?.SourcePartEpisodeEnd,
            NumberingConflict = parsed.NumberingConflict, NumberingOrder = previous?.NumberingOrder ?? parsed.NumberingOrder };
        var sourceNumbering = parsed.NumberingSource is "filename" or "season-folder";
        if (previous is not null && CanRepairGrouping(previous) && previous.MetadataType is null && parsed.Series is not null && !LibraryIdentity.Locked(previous, "Title"))
            item = item with { Title = parsed.Title };
        if (sourceNumbering && previous is not null && CanRepairGrouping(previous) && !EpisodeOrder.ProviderDefined(previous.NumberingOrder))
            item = item with { Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd,
                AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, NumberingOrder = "aired" };
        if (parsed.AbsoluteEpisodeEnd is not null && previous is not null && CanRepairGrouping(previous) && previous.AbsoluteEpisode is null && !EpisodeOrder.ProviderDefined(previous.NumberingOrder))
            item = item with { NumberingOrder = parsed.NumberingOrder };
        // A film's own NFO, added since, says the same as a film's kind does.
        var filmSidecar = previous?.Kind != "Movie" && parsed.Series is null && parsed.Episode is null && (parsed.MetadataType == "movie" || LocalMetadata.SaysFilm(parsed.Path));
        if (filmSidecar && previous is not null && CanRepairGrouping(previous) && previous.MetadataType is null) item = item with { Kind = parsed.Kind };
        if (parsed.Series is null && parsed.Episode is null && previous is not null && CanRepairGrouping(previous) && previous.MetadataType is null &&
            (previous.Kind == "Movie" || filmSidecar || parsed.Year is >= 1900 && previous.Episode == parsed.Year))
            item = item with { Title = parsed.Title, Series = null, ShowId = null, SeasonId = null, SeriesPath = null,
                Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null };
        if (previous is not null)
        {
            if (previous.Matched || LibraryIdentity.Locked(previous, "Season")) item = item with { Season = previous.Season };
            if (previous.Matched || LibraryIdentity.Locked(previous, "Episode")) item = item with
            { Episode = previous.Episode, EpisodeEnd = previous.EpisodeEnd, AbsoluteEpisode = previous.AbsoluteEpisode, AbsoluteEpisodeEnd = previous.AbsoluteEpisodeEnd };
            if (LibraryIdentity.Locked(previous, "Title")) item = item with { Title = previous.Title, Series = previous.Series };
            if (LibraryIdentity.Locked(previous, "Kind")) item = item with { Kind = previous.Kind };
            if (LibraryIdentity.Locked(previous, "Year")) item = item with { Year = previous.Year };
            // Older parsers read "S03E07 - 42" as a range; drop a stored range the filename no longer supports.
            if (previous.Matched && !LibraryIdentity.Locked(previous, "Episode") && previous.AbsoluteEpisode is null && previous.EpisodeEnd is not null &&
                parsed.NumberingSource == "filename" && parsed.EpisodeEnd is null && parsed.Season == previous.Season && parsed.Episode == previous.Episode)
                item = item with { EpisodeEnd = null };
        }
        // A name read as a show's, on a file that stays a film, doesn't move it out of Movies or give it an episode title.
        if (previous is not null && item.Series is null && parsed.Series is not null) item = item with { Kind = previous.Kind, EpisodeTitle = previous.EpisodeTitle };
        var bonus = previous?.Matched == true || previous is not null && LibraryIdentity.Locked(previous, "Episode") ? previous.IsBonus : parsed.IsBonus;
        // A film a provider identified stays a film, whatever a clip word in its name suggests: as a clip it would drop out of refreshes and wanted titles.
        if (previous is { MetadataType: "movie" } && parsed.IsBonus && parsed.ExtraOf is null) { bonus = previous.IsBonus; item = item with { EpisodeTitle = previous.EpisodeTitle, SeriesPath = previous.SeriesPath }; }
        // A film's clip stays with its film, including one an earlier scan had filed under a show of its own.
        if (parsed.ExtraOf is not null && (previous is null || CanRepairGrouping(previous)))
        { item = item with { ExtraOf = parsed.ExtraOf, Title = parsed.Title, Kind = parsed.Kind, Series = null, ShowId = null, SeasonId = null, SeriesPath = null, Season = null, Episode = null, EpisodeEnd = null, AbsoluteEpisode = null, AbsoluteEpisodeEnd = null, Year = null }; bonus = true; }
        // A clip's home season comes from its folder, so a rescan places clips indexed before seasons were recorded.
        if (bonus && parsed.IsBonus && parsed.AirsAfterSeason is not null) item = item with { AirsAfterSeason = parsed.AirsAfterSeason };
        // A folder that no longer names its files' show, because they name unrelated shows of their own, lets them go: the file takes the
        // show its name gives, and the match it shared with the folder is looked up again. A file someone matched or locked stays.
        if (previous is { SeriesPath: { } heldBy } && !string.Equals(parsed.SeriesPath, heldBy, StringComparison.OrdinalIgnoreCase) && parsed.Series is { } ownShow && CanRepairGrouping(previous) &&
            !LibraryIdentity.Locked(previous, "Series") && MetadataTitles.Normalize(ownShow) != MetadataTitles.Normalize(ReadSeries(previous) ?? ""))
            item = AutomaticArtwork.ResetIdentity(TmdbRetention.Revert(item)) with { Title = parsed.Title, Series = ownShow, Kind = parsed.Kind, Year = parsed.Year,
                ShowId = parsed.ShowId, SeasonId = parsed.SeasonId, SeriesPath = null, MetadataKind = null, MetadataType = null, ProviderIds = null, ArtworkFetchedAt = 0,
                Season = parsed.Season, Episode = parsed.Episode, EpisodeEnd = parsed.EpisodeEnd, EpisodeTitle = parsed.EpisodeTitle, NumberingOrder = parsed.NumberingOrder };
        return LocalMetadata.Apply(item with { IsBonus = bonus, BonusKind = bonus ? parsed.IsBonus ? parsed.BonusKind : previous?.BonusKind : null, NumberingSource = parsed.NumberingSource,
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
    private const string PlainExtras = @"(?:bd |dvd )?extras|trailers|featurettes|behind the scenes";
    // These are also ordinary names for a person, a show or a film, so they only count inside a folder that holds a title's videos or seasons.
    private const string ShortExtras = @"(?:bd |dvd )?(?:extra|bonus)|sps|nc|nc ?op-?ed|ncops?|nceds?|creditless(?: ops?)?(?: ?& ?eds?)?|(?:bd |web )?menus?|(?:web )?pvs?|cms?|interviews.*";
    // "Extras2" and "Extras-handbraked" are still extras folders; a short name like "Bonus" stays exact, since "Bonus-Track" names a title.
    private const string PlainExtrasFolder = @"(?:" + PlainExtras + @")\d{0,2}(?:[-_. ]+[\p{L}\p{N}]+)?";
    private const string ExtrasFolders = @"(?i)^(?:" + PlainExtrasFolder + "|" + ShortExtras + ")$";
    // "(S01-S07+Movies)" and "[Season 1 + 2]" hold several seasons, so neither is a season folder.
    private const string SeasonFolder = @"(?i)(?:^|\s|\(|\[)(?:(?:Season|Сезон|Saison|Temporada|Staffel|Stagione)\s*|S)([0-9]{1,2}|(?:19|20)[0-9]{2})(?![0-9]|E[0-9])\b(?!\s*[-+~,&–]\s*(?:S|Season\s*)?[0-9])|^([0-9]{1,3})\s*(?:сезон|sezon)\b|^T?([0-9]{1,2})(?:\s+-\s+\w+)?$|^Specials$";
    private static string[] Parts(string path) => path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    private static bool IsSeasonFolder(string name) => Regex.IsMatch(Regex.Replace(name, @"[._]+", " "), SeasonFolder);
    private static int? SeasonNumber(string name) => Regex.Match(Regex.Replace(name, @"[._]+", " "), SeasonFolder) is { Success: true } match
        ? match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0 : null;
    // The extras folder a file sits in, if any. Folders above the library say nothing about its files. With no library to go by, a
    // short name only counts as the file's own folder.
    private static int RootDepth(string[] parts, string? libraryRoot)
    {
        if (libraryRoot is null) return -1;
        var root = Parts(System.IO.Path.GetFullPath(libraryRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar));
        return root.Length < parts.Length && root.SequenceEqual(parts[..root.Length], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) ? root.Length : -1;
    }
    private static int ExtrasFolderIndex(string[] parts, string? libraryRoot)
    {
        var depth = RootDepth(parts, libraryRoot);
        var within = depth >= 0;
        var first = within ? depth : 1;
        for (var index = first; index < parts.Length - 1; index++)
        {
            if (Regex.IsMatch(parts[index], "(?i)^(?:" + PlainExtrasFolder + ")$")) return index;
            if (Regex.IsMatch(parts[index], "(?i)^(?:" + ShortExtras + ")$") && (within ? index > first : index == parts.Length - 2) && HoldsTitle(string.Join(System.IO.Path.DirectorySeparatorChar, parts[..index]))) return index;
        }
        return -1;
    }
    // Whether a folder is a title's own rather than one that groups things: it has seasons or videos in it, looking through folders like "BD".
    private static bool HoldsTitle(string folder)
    {
        try
        {
            while (FilenameFolders.IsContainer(System.IO.Path.GetFileName(folder)) && System.IO.Path.GetDirectoryName(folder) is { } above) folder = above;
            if (FilenameFolders.IsGeneric(System.IO.Path.GetFileName(folder)) && !IsSeasonFolder(System.IO.Path.GetFileName(folder))) return false;
            return Directory.Exists(folder) && (File.Exists(System.IO.Path.Combine(folder, "tvshow.nfo")) ||
                Directory.EnumerateFiles(folder).Any(file => Extensions.Contains(System.IO.Path.GetExtension(file))) ||
                Directory.EnumerateDirectories(folder).Any(child => IsSeasonFolder(System.IO.Path.GetFileName(child))));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    // Raw path policy: a file in an extras folder, or named as a trailer or sample. Parse keeps a show's extras as bonus clips.
    public static bool IsExtra(string path, string? libraryRoot = null) => (ExtrasFolderIndex(Parts(path), libraryRoot) >= 0 || Parts(path).SkipLast(1).Any(part => Regex.IsMatch(part, @"(?i)^samples?$")) ||
        Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:^|[-._ ])(?:trailer|sample|featurette)\d*$")) &&
        FilenameEpisodes.Read(path, false) is not { ExplicitSeason: true, Season: 0 };
    private static bool IsSample(string path) => Parts(path).SkipLast(1).Any(part => Regex.IsMatch(part, @"(?i)^samples?$")) ||
        Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), @"(?i)(?:^|[-._ ])sample\d*$");
    private static string Identity(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path)))[..32];
    // An extras folder belongs to a show when it sits under a season folder, or beside season folders or numbered
    // episodes. A folder with an unnumbered video beside it is a film's own extras folder, even inside an anime library.
    private static (bool Show, string? Film, string? Owner) ExtrasHome(string path, bool seriesContext, string? libraryRoot)
    {
        if (IsSample(path)) return (false, null, null);
        var parts = Parts(path);
        var extras = ExtrasFolderIndex(parts, libraryRoot);
        if (extras < 1) return (false, null, null);
        var owner = string.Join(System.IO.Path.DirectorySeparatorChar, parts[..extras]);
        if (parts[Math.Max(0, RootDepth(parts, libraryRoot))..extras].Any(IsSeasonFolder)) return (true, null, owner);
        var parent = string.Join(System.IO.Path.DirectorySeparatorChar, parts[..extras]);
        try
        {
            if (!Directory.Exists(parent)) return (seriesContext, null, seriesContext ? owner : null);
            if (File.Exists(System.IO.Path.Combine(parent, "tvshow.nfo")) || Directory.EnumerateDirectories(parent).Any(folder => IsSeasonFolder(System.IO.Path.GetFileName(folder)))) return (true, null, owner);
            var videos = Directory.EnumerateFiles(parent).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file)) && !IsExtra(file, libraryRoot)).OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();
            if (videos.Length == 0) return (false, null, null);
            // Mostly numbered videos are a show's episodes, even with a stray special among them.
            var films = videos.Where(file => FilenameEpisodes.Read(file, true) is null).ToArray();
            return films.Length * 2 <= videos.Length ? (true, null, owner) : (false, films[0], null);
        }
        catch (IOException) { return (false, null, null); }
        catch (UnauthorizedAccessException) { return (false, null, null); }
    }
    // The folder facts a name is read against: whether the path is a TV or anime location, and any season folder holding the file.
    internal static (bool Series, Match SeasonFolder, int? FolderSeason) NameContext(string path, string kind, string? libraryRoot = null)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var series = kind is "Show" or "Anime" || kind == "Mixed" && path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => Regex.IsMatch(part, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$")) || File.Exists(System.IO.Path.Combine(dir, "tvshow.nfo"));
        var season = Regex.Match(Regex.Replace(System.IO.Path.GetFileName(dir), @"[._]+", " "), SeasonFolder);
        int? number = season.Success ? season.Groups[1].Success ? int.Parse(season.Groups[1].Value) : season.Groups[2].Success ? int.Parse(season.Groups[2].Value) : season.Groups[3].Success ? int.Parse(season.Groups[3].Value) : 0 : null;
        // A bare "2" is only a season under a folder that names something, and for a file with a number of its own: a film in a
        // folder called "1" or "Vol 2" is still a film. "24 - 12" in a folder called 24 is the show's name, and so is that folder
        // directly in a library, which names no show above it.
        if (season.Success && season.Groups[3].Success && (dir.Length == 0 || System.IO.Path.GetDirectoryName(dir) is not { } above || FilenameFolders.IsGeneric(System.IO.Path.GetFileName(above)) || IsLibraryRoot(above, libraryRoot) ||
            !FilenameEpisodes.HasOwnNumber(path) || NamesShowInFolder(path, dir) || NamesOtherShow(path, above))) return (series, Match.Empty, null);
        return (series, season, number);
    }
    private static bool IsLibraryRoot(string dir, string? libraryRoot) =>
        libraryRoot is not null && string.Equals(System.IO.Path.GetFullPath(libraryRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar), dir.TrimEnd(System.IO.Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    // A file that names its show the same as the number its folder is called: "24 - 12" or "[Group] 86 - 05".
    private static bool NamesShowInFolder(string path, string dir)
    {
        var folder = System.IO.Path.GetFileName(dir);
        return Regex.IsMatch(folder, @"^[0-9]{1,4}$") && Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path), $@"^(?:\[[^\]]*\]\s*)?{Regex.Escape(folder)}[\s._-]+.*\d");
    }
    // A file that names a show other than the folder above its numbered folder: "Ninety - 05" and "Naruto S01E05" in "Backup\1".
    private static bool NamesOtherShow(string path, string above)
    {
        var named = LibraryIdentity.Normalize(FilenameEpisodes.Analyze(path, false).Numbering?.Series ?? "");
        var folder = LibraryIdentity.Normalize(FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(above)).Title);
        return named.Length > 0 && folder.Length > 0 && !named.StartsWith(folder, StringComparison.Ordinal) && !folder.StartsWith(named, StringComparison.Ordinal);
    }
    // A folder called only a number, directly in a library or a folder that names no show, holding numbered files: "TV\24\01 - Midnight".
    private static bool IsNumberedShow(string dir, string? libraryRoot) => Regex.IsMatch(System.IO.Path.GetFileName(dir), @"^[0-9]{1,4}$") &&
        System.IO.Path.GetDirectoryName(dir) is { } above && (IsLibraryRoot(above, libraryRoot) || FilenameFolders.IsGeneric(System.IO.Path.GetFileName(above)));
    // A library root, or a folder whose name only says what kind of files sit in it.
    private static bool IsLooseFolder(string dir, string? libraryRoot) => IsLibraryRoot(dir, libraryRoot) || FilenameFolders.IsGeneric(System.IO.Path.GetFileName(dir));
    // The closest folder above a file whose name says something about it, for files named with only a number.
    private static string? NearestTitledFolder(string dir, string? libraryRoot)
    {
        for (var folder = dir; folder is not null && !IsLooseFolder(folder, libraryRoot); folder = System.IO.Path.GetDirectoryName(folder))
        {
            var name = System.IO.Path.GetFileName(folder);
            if (name.Length > 0 && !FilenameFolders.IsContainer(name) && !Regex.IsMatch(name, "(?i)^(?:" + PlainExtras + ")$") && FilenameEpisodes.AnalyzeFolder(name).Title is { Length: > 0 } title) return title;
        }
        return null;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, bool Many)> TitleCounts =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    // Files in a show folder mostly share one title, even under a localized folder name. Files in a collection don't.
    private static bool HoldsManyTitles(string dir, bool seriesContext)
    {
        try
        {
            var written = Directory.GetLastWriteTimeUtc(dir);
            if (TitleCounts.TryGetValue(dir, out var cached) && cached.Written == written) return cached.Many;
            var many = false;
            if (Directory.Exists(dir))
            {
                var titles = Directory.EnumerateFiles(dir).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file))).Take(400)
                    .Select(file => FilenameEpisodes.Analyze(file, seriesContext).Title).Where(title => title.Length > 0).Select(title => MetadataTitles.Normalize(title)).ToArray();
                var distinct = titles.Distinct().Count();
                many = distinct >= 5 && distinct * 4 > titles.Length;
            }
            if (TitleCounts.Count > 4096) TitleCounts.Clear();
            TitleCounts[dir] = (written, many);
            return many;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, bool Several, long Checked, int Scan)> NamedShows = new(StringComparer.OrdinalIgnoreCase);
    private const long NamedShowsFresh = 10_000;
    // Counts scans, so a folder answer reused without stamping its subfolders never outlives the scan that made it.
    internal static int ScanGeneration;
    // "Desktop\hand_brake\Season 1-handbraked" holding Villainess S01E01-E12 and Komi S01E01-E12 side by side: the same episodes under two
    // unrelated show names are two shows, so the folder above can't name them all. Copies of one show under two spellings ("Ushio to Tora"
    // and "Ushitora", "Tetsujin 28-go FX" and "Iron Hero 28 FX") share a word or a start, or sit in separate release folders, and stay together.
    private static bool HoldsSeveralNamedShows(string dir)
    {
        try
        {
            // Every file of a season folder asks about the same folder in turn, so an answer seconds old stands without stamping its subfolders again.
            if (NamedShows.TryGetValue(dir, out var cached) && cached.Scan == Volatile.Read(ref ScanGeneration) && Environment.TickCount64 - cached.Checked < NamedShowsFresh) return cached.Several;
            if (!Directory.Exists(dir)) return false;
            var folders = Directory.EnumerateDirectories(dir).Take(64).Prepend(dir).ToArray();
            var stamp = string.Join("|", folders.Select(folder => Directory.GetLastWriteTimeUtc(folder).Ticks));
            if (cached.Stamp == stamp) { NamedShows[dir] = cached with { Checked = Environment.TickCount64, Scan = Volatile.Read(ref ScanGeneration) }; return cached.Several; }
            var folderTitle = FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(dir)).Title;
            var several = false;
            foreach (var folder in folders)
            {
                var episodes = new Dictionary<(int, int), HashSet<string>>();
                foreach (var file in Directory.EnumerateFiles(folder).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file))).Take(400))
                {
                    var reading = FilenameEpisodes.Analyze(file, true);
                    if (reading.Numbering is not { Season: > 0, Episode: { } episode } numbering || string.IsNullOrWhiteSpace(numbering.Series)) continue;
                    var key = (numbering.Season!.Value, episode);
                    if (!episodes.TryGetValue(key, out var names)) episodes[key] = names = new(StringComparer.OrdinalIgnoreCase);
                    names.Add(numbering.Series);
                }
                // It takes two episodes held under two names that share nothing, neither of them the folder's own name.
                var clashes = episodes.Values.Count(names => names.Any(a => names.Any(b => Unrelated(a, b) && (folderTitle.Length == 0 || Unrelated(a, folderTitle) && Unrelated(b, folderTitle)))));
                if (clashes >= 2) { several = true; break; }
            }
            if (NamedShows.Count > 4096) NamedShows.Clear();
            NamedShows[dir] = (stamp, several, Environment.TickCount64, Volatile.Read(ref ScanGeneration));
            return several;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    // Two names share nothing when no word of three letters or two digits is common to both and they don't start alike.
    private static bool Unrelated(string left, string right)
    {
        static string[] Words(string title) => Regex.Split(Fold(title).ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(word => word.Length >= 3 || word.Length == 2 && word.All(char.IsDigit)).Where(word => word is not ("the" or "and")).ToArray();
        var a = Words(left); var b = Words(right);
        if (a.Length == 0 || b.Length == 0 || a.Intersect(b).Any()) return false;
        var x = string.Concat(a); var y = string.Concat(b);
        var common = 0;
        while (common < Math.Min(x.Length, y.Length) && x[common] == y[common]) common++;
        return common < 4;
    }
    private static bool OvaFolder(string name) => Regex.IsMatch(name, @"(?i)^(?:ova|oad|ona)$");
    private static bool HoldsEpisodeFiles(string name) => !Regex.IsMatch(name, @"(?i)^(?:ova|oad|ona|specials?|movies?|films?)$") && !Regex.IsMatch(name, ExtrasFolders);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string?), (DateTime Written, bool Holds)> NumberedEpisodes = new();
    // Whether the scan reads a file in a show's folder, or one level down, as a listed, numbered episode of that show. Asking the scan itself means a folder
    // that's a show of its own, a name that's only a title, and a sample it never lists don't count. A clip further down can bring this back here for a folder
    // nearer the root, which always ends. The answer is kept until one of those folders changes, so many clips cost one read.
    private static bool HoldsNumberedEpisodes(string show, string kind, string? libraryRoot)
    {
        try
        {
            if (!Directory.Exists(show)) return false;
            var folders = new[] { show }.Concat(Directory.EnumerateDirectories(show).Where(child => HoldsEpisodeFiles(System.IO.Path.GetFileName(child)))).ToArray();
            var written = folders.Max(Directory.GetLastWriteTimeUtc);
            if (NumberedEpisodes.TryGetValue((show, kind, libraryRoot), out var known) && known.Written == written) return known.Holds;
            var holds = folders.SelectMany(folder => Directory.EnumerateFiles(folder).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file))).Take(50))
                .Any(file => Parse(file, kind, libraryRoot) is { IsBonus: false, IsExtra: false, Season: not 0, SeriesPath: { } home } found && (found.Episode is not null || found.AirDate is not null) &&
                    string.Equals(home, show, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (NumberedEpisodes.Count > 4096) NumberedEpisodes.Clear();
            NumberedEpisodes[(show, kind, libraryRoot)] = (written, holds);
            return holds;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    // Whether a show's folder holds videos of its own, beside any OVA, specials, films or extras folders.
    private static bool HoldsOwnEpisodes(string show)
    {
        try
        {
            return Directory.Exists(show) && (Directory.EnumerateFiles(show).Any(file => Extensions.Contains(System.IO.Path.GetExtension(file))) ||
                Directory.EnumerateDirectories(show).Any(child => HoldsEpisodeFiles(System.IO.Path.GetFileName(child)) &&
                    Directory.EnumerateFiles(child).Any(file => Extensions.Contains(System.IO.Path.GetExtension(file)))));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, bool), (DateTime Written, bool Several)> SeveralShows = new();
    private static bool HoldsSeveralShows(string dir, bool seriesContext)
    {
        try
        {
            var written = Directory.GetLastWriteTimeUtc(dir);
            if (SeveralShows.TryGetValue((dir, seriesContext), out var cached) && cached.Written == written) return cached.Several;
            var several = Directory.Exists(dir) && Directory.EnumerateFiles(dir).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file))).Take(400)
                .Select(file => FilenameEpisodes.Read(file, seriesContext)?.Series).Where(show => !string.IsNullOrWhiteSpace(show)).Select(show => TitleWords(show!)).Distinct().Skip(1).Any();
            if (SeveralShows.Count > 4096) SeveralShows.Clear();
            SeveralShows[(dir, seriesContext)] = (written, several);
            return several;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static MediaItem Parse(string path, string kind = "Mixed", string? libraryRoot = null)
    {
        path = System.IO.Path.GetFullPath(path);
        var sourceKind = kind;
        var id = Identity(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var (seriesContext, seasonFolder, folderSeason) = NameContext(path, kind, libraryRoot);
        var (showExtras, filmExtra, extrasOwner) = ExtrasHome(path, seriesContext, libraryRoot);
        var seasonFolderName = System.IO.Path.GetFileName(dir);
        var reading = FilenameEpisodes.Analyze(path, seriesContext || seasonFolder.Success, folderSeason, sourceKind != "Movie", libraryRoot, sourceKind == "Movie");
        var name = reading.Cleaned;
        var episodeName = name;
        var numbering = reading.Numbering;
        // An obfuscated file inside a named release: the folder "Show.S01E02.720p" says what "gNWDXow11s.mkv" doesn't.
        if (numbering is null && reading.Clip is null && System.IO.Path.GetFileName(dir) is { Length: > 0 } releaseName && FilenameEpisodes.ReleaseNumbering(releaseName) is { } releaseFolder && !FilenameEpisodes.SameTitle(reading.Title, releaseFolder.Series)) numbering = releaseFolder;
        // "Deleted Scene 2" beside "Harbor - 01": extra material numbering itself isn't a second show in the folder.
        if (numbering is { ExplicitSeason: false, Series: { } numberedTitle } && sourceKind != "Movie" && filmExtra is null && !showExtras && FilenameExtras.Named(numberedTitle))
        {
            var folderShow = EpisodeFolderSeries(dir, name, libraryRoot, seriesContext || seasonFolder.Success);
            if (folderShow is not null && !FilenameEpisodes.SameTitle(folderShow, numberedTitle) ? FilenameExtras.OnlyExtraAfter(numberedTitle, folderShow) : FilenameExtras.OnlyExtra(numberedTitle)) { numbering = null; reading = reading with { Title = reading.Cleaned }; }
            // "Roots\Part 3" with nothing else beside it is a miniseries' episode.
            else if (FilenameExtras.IsPart(numberedTitle)) numbering = NearestTitledFolder(dir, libraryRoot) is { } miniseries ? numbering with { Series = miniseries } : null;
        }
        var season = numbering?.Season; var number = numbering?.Episode;
        string? series = numbering?.Series;
        // A clip is a show's when its location says so; anywhere else it needs numbered episodes beside it (below).
        var bonus = reading.Clip is not null && !reading.Film && (seriesContext || seasonFolder.Success);
        // "Azumanga Daioh - XX - Original Opening Sequence" beside the show's numbered episodes is one of its clips, not a film.
        // "One Piece - 492A" is half of an episode, so it stays where its folder put it.
        var besideEpisodes = numbering is null && sourceKind != "Movie" && filmExtra is null && !showExtras && !IsExtra(path, libraryRoot) &&
            !Regex.IsMatch(name, @"(?i)(?:^|\s-\s)\d{1,4}[a-z]\b")
            ? EpisodeFolderSeries(dir, name, libraryRoot, seriesContext || seasonFolder.Success) : null;
        if (besideEpisodes is not null) { bonus = true; series = besideEpisodes; }
        // "One Piece - Egghead SP4" beside "One Piece - 1100" is a One Piece special, not a show called "One Piece - Egghead".
        // "OVA 1" to "OVA 3" read alone as a run of a show called OVA, which only says what they are.
        else if (numbering is { ExplicitSeason: true, Season: 0 } && EpisodeFolderSeries(dir, name, libraryRoot, seriesContext || seasonFolder.Success) is { } owner &&
            !(string.IsNullOrWhiteSpace(numbering.Series) && name.StartsWith(owner, StringComparison.OrdinalIgnoreCase))) series = owner;
        else if (bonus && string.IsNullOrWhiteSpace(series)) series = reading.Title;
        // "Show - 06.5" alone says it's part of a show, though there's no whole number to file it under: it stays a show a lookup can find, named by its number.
        // A film's own NFO beside it says otherwise.
        var loneHalf = reading is { Kind: "Bare", Clip: not null, Film: false } && numbering is null && !bonus && sourceKind != "Movie" && filmExtra is null && !showExtras && !string.IsNullOrWhiteSpace(reading.Title) &&
            !LocalMetadata.SaysFilm(path);
        if (loneHalf) series = reading.Title;
        // "OP.mkv" loose in a library, with no folder to name a show, has no show to belong to.
        if (bonus && string.IsNullOrWhiteSpace(series) && IsLooseFolder(dir, libraryRoot) && NearestTitledFolder(dir, libraryRoot) is null) { bonus = false; series = null; }
        if (numbering is not null && !numbering.ExplicitSeason && number is not null && kind == "Mixed" && reading.LeadingGroup) kind = "Anime";
        // "Show Season_2 - 01" inside "Show S02": the folder and the name agree, so the folder's numbering stands.
        var filenameSeason = numbering?.ExplicitSeason == true && !(reading.Kind == "Bare" && season == folderSeason) ? season : null;
        if (seasonFolder.Success)
        {
            season = filenameSeason ?? folderSeason ?? 0;
            if (string.IsNullOrWhiteSpace(series) || series == seasonFolderName)
                // "Doctor Who\Specials" holds the show's specials, and says nothing of its own.
                series = FilenameEpisodes.AnalyzeFolder(seasonFolderName).Title is { Length: > 0 } own && !FilenameFolders.IsContainer(seasonFolderName) ? own : FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir)!)).Title;
        }
        // A file named only by its number with no folder to name it has only its own remaining words.
        if (numbering is not null && string.IsNullOrWhiteSpace(series) && (NamesShowInFolder(path, dir) || IsNumberedShow(dir, libraryRoot))) series = System.IO.Path.GetFileName(dir);
        if (numbering is not null && string.IsNullOrWhiteSpace(series)) series = NearestTitledFolder(dir, libraryRoot) ?? (IsLooseFolder(dir, libraryRoot) && numbering.Subtitle is { Length: > 0 } words ? words : System.IO.Path.GetFileName(dir));
        var seriesDir = showExtras && extrasOwner is not null ? extrasOwner : dir;
        int? sourcePart = null;
        // "Serials\24\Season 1\01.mkv": a folder called a number, with a season folder inside and files that name nothing, is the show.
        bool HoldsSeason() => seriesDir != dir && string.IsNullOrWhiteSpace(numbering?.Series) && Regex.IsMatch(System.IO.Path.GetFileName(seriesDir), @"^[0-9]{1,4}$") && IsSeasonFolder(System.IO.Path.GetFileName(dir));
        // "Shelf\TV\Show - 03" names its own show, so a folder that says where files are doesn't pass it up to Shelf. "Show\TV\Show Part Two 01" is the same show's.
        // A folder that describes its files, "Show\DVDRip.AVI.640x480", is still inside the show.
        var ownShow = TitleWords(!string.IsNullOrWhiteSpace(numbering?.Series) ? numbering.Series : bonus ? reading.Title : "");
        bool OtherShowAbove(string parent)
        {
            if (ownShow.Length == 2) return false;
            var above = TitleWords(FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(parent)).Title);
            // A folder above that says nothing, like "Season 1", isn't another show.
            if (above.Length == 2) return false;
            // "Show\OVA\Show Ultimate - 01", "Show Ultimate OVA 01" and "Show Ultimate - S01E01" have a title of their own, and providers list them apart.
            // "Show OAD - S00E01" only says what it is, and "Show Two - S02E01" gives a later season, so both are the show's. Extras stay with whatever holds them.
            if (OvaFolder(System.IO.Path.GetFileName(seriesDir)) && filenameSeason is null or 0 or 1 && !showExtras)
            {
                if (TitleWords(Regex.Replace(ownShow, @"(?i)(?<![\p{L}\p{N}])(?:ova|oad|ona)(?![\p{L}\p{N}])", " ")) != above) return true;
                // A clip only joins a show with episodes. A title holding only clips or a film has to stay one a lookup can find, and a film library has no shows.
                return numbering is null && (sourceKind == "Movie" || besideEpisodes is null && !HoldsNumberedEpisodes(parent, sourceKind, libraryRoot));
            }
            if (!ownShow.Contains(above, StringComparison.Ordinal) && !above.Contains(ownShow, StringComparison.Ordinal)) return true;
            // "Star Trek\TV" with TNG and DS9 beside each other: each file is its own show.
            return HoldsSeveralShows(seriesDir, seriesContext || seasonFolder.Success);
        }
        var throughOva = false;
        while (!NamesShowInFolder(path, seriesDir) && !HoldsSeason() && System.IO.Path.GetDirectoryName(seriesDir) is { } parent && (IsReleaseDirectory(seriesDir) || FilenameFolders.IsContainer(System.IO.Path.GetFileName(seriesDir)) && !(FilenameFolders.IsLocation(System.IO.Path.GetFileName(seriesDir)) && OtherShowAbove(parent))) &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            var part = Regex.Match(System.IO.Path.GetFileName(seriesDir), @"(?i)^Part\s*([0-9]{1,4})\s*$");
            if (part.Success) sourcePart = int.Parse(part.Groups[1].Value);
            throughOva |= OvaFolder(System.IO.Path.GetFileName(seriesDir));
            seriesDir = parent;
        }
        // "Show\OVA\Show - 01" beside the show's own episodes isn't episode 1: like its specials, a show's OVAs number apart, in the
        // show's order rather than as a run through it, unless the file or a season folder gives a season. A title with only OVAs numbers them itself.
        var apartFromShow = throughOva && filenameSeason is null && number is not null && !seasonFolder.Success && !IsLooseFolder(seriesDir, libraryRoot) && HoldsOwnEpisodes(seriesDir);
        if (apartFromShow) season = 0;
        // A show's extras folder joins the show as bonus clips, whatever the file names say; an explicit S00 file there is a numbered special.
        var extraClip = showExtras && seriesDir != dir && numbering is not { ExplicitSeason: true, Season: 0 };
        // Beside several shows, a clip belongs to the one its own name gives rather than to the folder holding them all.
        if (extraClip) { bonus = true; series = !string.IsNullOrWhiteSpace(reading.Title) && HoldsSeveralNamedShows(seriesDir) ? reading.Title :
            IsLooseFolder(seriesDir, libraryRoot) ? NearestTitledFolder(extrasOwner!, libraryRoot) ?? reading.Title : FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(seriesDir)).Title; }
        if (extraClip && string.IsNullOrWhiteSpace(series)) series = System.IO.Path.GetFileName(seriesDir);
        // Dates in an episode title are not the premiere year of the entire series.
        var title = series ?? reading.Title;
        var titleYear = reading.Year;
        // "00002.m2ts" says nothing, so a disc's streams and bare numbered files are read through the nearest folder that says something.
        // A dated one is a film called that: "2001 (1968)".
        if (series is null && (FilenameEpisodes.IsDiscFile(title) || reading.Year is null && Regex.IsMatch(title, @"^0\d{2,}$|^\d{5,}$")) && NearestTitledFolder(dir, libraryRoot) is { } discTitle) title = discTitle;
        // A name that says only its numbering is dated by the folder that names it.
        if (titleYear is null && string.IsNullOrWhiteSpace(reading.Title) || series is null && title != reading.Title)
            for (var folder = dir; folder is not null && titleYear is null && !IsLooseFolder(folder, libraryRoot); folder = System.IO.Path.GetDirectoryName(folder))
                titleYear = FilenameEpisodes.AnalyzeFolder(System.IO.Path.GetFileName(folder)).Year;
        if (string.IsNullOrWhiteSpace(title)) title = reading.Cleaned;
        if (string.IsNullOrWhiteSpace(title)) title = System.IO.Path.GetFileNameWithoutExtension(path);
        string? Artwork(string folder, params string[] stems) => stems.SelectMany(stem => new[] { ".jpg", ".jpeg", ".png" }.Select(ext => System.IO.Path.Combine(folder, stem + ext))).FirstOrDefault(File.Exists);
        var localPoster = Artwork(dir, System.IO.Path.GetFileNameWithoutExtension(path), "poster", "folder", "cover");
        var seriesPoster = series is null ? null : Artwork(seriesDir, "poster", "folder", "cover");
        var seasonPoster = series is null ? null : Artwork(dir, "poster", "folder", "cover") ?? Artwork(seriesDir, $"season{season ?? 1:00}-poster", $"season{season ?? 1}-poster");
        var result = new MediaItem(id, path, title, kind == "Mixed" ? (series is null ? "Movie" : "Show") : kind,
            titleYear, series, season, number, localPoster ?? seriesPoster,
            SeriesPoster: seriesPoster, SeasonPoster: seasonPoster, EpisodeTitle: numbering?.Name ?? (loneHalf ? Regex.Match(reading.Clip!, @"^[0-9]+\.[0-9]").Value : null), AirDate: numbering?.AirDate,
            EpisodeEnd: numbering?.End, NumberingConflict: numbering?.Conflict);
        // Structured show folders own membership, regardless of aliases in individual filenames.
        var parentName = System.IO.Path.GetFileName(seriesDir);
        // A folder of many different titles is a collection, and a name like "UHD" or "Video" isn't a show at all.
        var collection = !File.Exists(System.IO.Path.Combine(seriesDir, "tvshow.nfo")) && (IsLooseFolder(seriesDir, libraryRoot) && !HoldsSeason() && !(numbering is not null && string.IsNullOrWhiteSpace(numbering.Series) && IsNumberedShow(seriesDir, libraryRoot)) || HoldsManyTitles(seriesDir, seriesContext || seasonFolder.Success) || HoldsManyTitles(dir, seriesContext || seasonFolder.Success) ||
            seriesDir != dir && HoldsSeveralNamedShows(seriesDir));
        // "Backup\1\Harbor S01E05": the file names its show, and the folders above only hold it.
        var heldElsewhere = Regex.IsMatch(System.IO.Path.GetFileName(dir), @"^\d{1,3}$") && System.IO.Path.GetDirectoryName(dir) is { } holder && NamesOtherShow(path, holder);
        var structured = File.Exists(System.IO.Path.Combine(seriesDir, "tvshow.nfo")) || !heldElsewhere && (numbering is not null || bonus) && (seasonFolder.Success || seriesContext || seriesDir != dir) &&
            (libraryRoot is null || !string.Equals(System.IO.Path.GetFullPath(libraryRoot), seriesDir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) &&
            !Regex.IsMatch(parentName, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы)$") && !collection && !(OvaFolder(parentName) && (!string.IsNullOrWhiteSpace(numbering?.Series) || bonus && !string.IsNullOrWhiteSpace(reading.Title)));
        if (structured)
        {
            var folder = FilenameEpisodes.AnalyzeFolder(parentName);
            var folderTitle = folder.Title.Length > 0 ? folder.Title : parentName;
            result = result with { Series = folderTitle, Title = folderTitle, SeriesPath = seriesDir,
                Year = folder.Year ?? result.Year,
                ShowId = "show-" + LibraryIdentity.Hash(OperatingSystem.IsWindows() ? seriesDir.ToUpperInvariant() : seriesDir) };
        }
        // A special filed inside a season folder is that season's special, not a numbering conflict.
        if (filenameSeason is not null and not 0 && folderSeason is not null && filenameSeason != folderSeason)
            result = result with { NumberingConflict = $"Filename season {filenameSeason} differs from folder season {folderSeason}; using filename." };
        var absoluteFolder = Regex.IsMatch(seasonFolderName, @"(?i)\bEP\s*\d{3,4}\b");
        if (number is not null && filenameSeason is null && !apartFromShow && result.Kind == "Anime" && (!seasonFolder.Success || absoluteFolder))
            result = result with { AbsoluteEpisode = number, AbsoluteEpisodeEnd = numbering?.End, NumberingOrder = "absolute" };
        // Unlabelled ranges retain absolute ordering unless a season folder confirms relative numbering.
        if (numbering?.End is not null && filenameSeason is null && !apartFromShow && (!seasonFolder.Success || absoluteFolder))
            result = result with { AbsoluteEpisode = number, AbsoluteEpisodeEnd = numbering.End, NumberingOrder = "absolute" };
        if (sourcePart is not null && filenameSeason is null && result.Episode is not null)
            result = result with { SourcePart = sourcePart, SourcePartEpisode = result.Episode, SourcePartEpisodeEnd = result.EpisodeEnd,
                Season = sourcePart > 1 ? null : result.Season, Episode = sourcePart > 1 ? null : result.Episode,
                EpisodeEnd = sourcePart > 1 ? null : result.EpisodeEnd, NumberingOrder = "absolute",
                AbsoluteEpisode = sourcePart > 1 ? null : result.AbsoluteEpisode };
        // Bonus clips sit in season 0 without numbering; a clip from an extras folder drops any numbers its file name carried.
        // A special's own year isn't its show's: a 2016 special of a 2010 show would otherwise date the whole show 2016.
        if (bonus && (structured || extraClip || besideEpisodes is not null) && (numbering is null || extraClip))
            result = result with { IsBonus = true, Year = structured ? FilenameEpisodes.AnalyzeFolder(parentName).Year : null,
                Kind = sourceKind == "Anime" || path.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p.Equals("Anime", StringComparison.OrdinalIgnoreCase)) ? "Anime" : "Show",
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
        // A clip's kind follows its title: "NCOP1" is an opening, "Original Closing Sequence" an ending, "Gallery" none of these.
        if (result.IsBonus) result = result with { BonusKind = FilenameExtras.Kind(result.EpisodeTitle) };
        return LocalMetadata.Apply(LibraryIdentity.Ensure(result with { SourceLibraryKind = sourceKind, IsExtra = IsExtra(path, libraryRoot) && !showExtras && filmExtra is null,
            NumberingSource = result.IsBonus ? null : apartFromShow ? "season-folder" : sourcePart is not null ? "part" : numbering?.AirDate is not null ? "date" : filenameSeason is not null ? "filename" :
                seasonFolder.Success && number is not null && !absoluteFolder ? "season-folder" : result.AbsoluteEpisode is not null ? "absolute" : null }));
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
    private static string Fold(string text) => new string(FilenameScan.Composed(text.Replace("&", " and ")).Normalize(NormalizationForm.FormD)
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
    // " star trek tng ": a title's words, so one title can be looked for inside another on whole words.
    private static string TitleWords(string title) => " " + string.Join(' ', Regex.Split(Fold(title).ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(word => word.Length > 0)) + " ";
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
                shows = Directory.EnumerateFiles(dir).Where(file => Extensions.Contains(System.IO.Path.GetExtension(file)) && !IsExtra(file, libraryRoot))
                    .Select(file => FilenameEpisodes.Read(file, seriesContext)).Where(found => found is { Episode: not null } && found.Season != 0 && found.Conflict is null && !FilenameExtras.OnlyExtra(found.Series) && !FilenameExtras.IsPart(found.Series))
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
        var folder = NearestTitledFolder(dir, libraryRoot) ?? "";
        // A name that starts with the show's name is judged on that name alone: "Sherlock The Abominable Bride 2016" is a clip,
        // "Westworld 1973 REMASTERED" the film. Any other dated name is a film, so the folder can't claim it.
        var dated = Regex.IsMatch(name, @"(?<!\d)(?:19|20)\d{2}(?!\d)");
        return shows.OrderByDescending(show => show.Count).Select(show => show.Series).FirstOrDefault(series =>
            MetadataTitles.Normalize(series).Length >= 2 && (NamedAfter(name, series, clip: false)
                ? NamedAfter(name, series, clip: true) : !root && !dated && NamedAfter(folder, series, clip: false)));
    }
    private static bool IsReleaseDirectory(string directory) => IsSeasonFolder(System.IO.Path.GetFileName(directory)) || Regex.IsMatch(
        Regex.Replace(System.IO.Path.GetFileName(directory), @"[._]+", " "),
        @"(?i)(?:^|\s)(?:S\d{1,2}(?:E\d{1,3})?|Season\s*\d{1,2})(?=\b|[ -])|^\d+\s*(?:сезон|sezon)\b|^Specials$|^Part\s*\d+\s*$") ||
        Regex.IsMatch(System.IO.Path.GetFileName(directory), ExtrasFolders);
}
