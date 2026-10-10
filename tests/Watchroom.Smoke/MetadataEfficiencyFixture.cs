using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Watchroom.Core;

static class MetadataEfficiencyFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        await FilenameAliases(directory, check);
        TargetedReads(directory, check);
        await AnimeIndex(directory, check);
    }

    private static async Task FilenameAliases(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "Shows");
        var folder = Path.Combine(root, "Localized Folder", "Season 01"); Directory.CreateDirectory(folder);
        var paths = new[] { Path.Combine(folder, "S01E01.mkv"), Path.Combine(folder, "Original.Title.S01E02.1080p.mkv") };
        foreach (var path in paths) await File.WriteAllBytesAsync(path, [0]);
        var store = new LibraryStore(Path.Combine(directory, "aliases-db"));
        await store.ScanAsync([new(root, "Show")]);
        var entries = store.All().ToArray(); var first = entries.Single(x => x.Episode == 1); var second = entries.Single(x => x.Episode == 2);
        var queries = MetadataTitles.Queries(first, entries);
        check(first.DisplayTitle == "Localized Folder" && queries.Contains("Original Title") && first.ShowId == second.ShowId,
            "structured folder membership retains an independent filename alias from another episode");
        var foreign = FileNames.Parse(Path.Combine(root, "Other Folder", "Season 01", "Foreign.Title.S01E01.mkv"), "Show", root);
        check(!MetadataTitles.Queries(first, [foreign]).Contains("Foreign Title"), "filename evidence cannot leak across separate show groups");
        check(MetadataTitles.FilenameAlias("[Group] Original Title - 013-014 [1080p].mkv") == "Original Title" &&
            MetadataTitles.FilenameAlias("Original.Title.2x03.mkv") == "Original Title" &&
            MetadataTitles.FilenameAlias("Original.Title.S01-E03.mkv") == "Original Title" &&
            MetadataTitles.FilenameAlias("Original.Title - 03 WEB.DL.mkv") == "Original Title" &&
            MetadataTitles.FilenameAlias("Название 2 сезон 3 серия.mkv") == "Название" &&
            MetadataTitles.FilenameAlias("1923.S01E03.mkv") == "1923" &&
            MetadataTitles.FilenameAlias("3.Body.Problem.S01E03.mkv") == "3 Body Problem",
            "filename aliases support anime ranges, alternate numbering, Russian markers and numeric title words");
        check(new[] { "S01E01.mkv", "01 - Pilot.mkv", "Harbor.2024.mkv", "W.A.L.L.E.2008.mkv" }
            .All(path => MetadataTitles.FilenameAlias(path) is null), "episode-only names, subtitles and film years do not become show aliases");

        new MetadataOptions(Providers: ["tmdb"]).Save(store);
        using var handler = new AliasHandler(); using var http = new HttpClient(handler); using var tmdb = new MetadataClient("fixture", handler);
        await AutomaticArtwork.FetchAsync(store, directory, "fixture", null, default, http, metadata: tmdb);
        check(store.All().All(x => x.MetadataId == 77 && x.ShowId == first.ShowId && x.EpisodeTitle == "Episode " + x.Episode) &&
            handler.Queries.Contains("Original Title"), "TMDB fetch identifies the full show using a later episode's filename alias");
        await store.ScanAsync([new(root, "Show")]);
        check(store.All().Select(x => x.Id).Order().SequenceEqual(entries.Select(x => x.Id).Order()) && store.All().All(x => x.ShowId == first.ShowId),
            "alias matching and rescan preserve file IDs and folder-owned show membership");

        var maze = new LibraryStore(Path.Combine(directory, "maze-aliases-db"));
        foreach (var entry in entries) maze.Save(entry);
        new MetadataOptions(Providers: ["tvmaze"]).Save(maze);
        await SeriesMetadata.FetchAsync(maze, directory, null, null, default, http);
        check(maze.All().All(x => x.MetadataProvider == "tvmaze" && x.MetadataId == 77 && x.EpisodeTitle == "Episode " + x.Episode),
            "standalone episode fetching also uses aliases from the whole show with TVmaze");
        using var tied = new MetadataClient("fixture", new AliasHandler(tied: true));
        check(await tied.MatchAsync(second) is null, "new filename aliases do not weaken rejection of tied provider candidates");
    }

    private static void TargetedReads(string directory, Action<bool, string> check)
    {
        var data = Path.Combine(directory, "read-db"); var store = new LibraryStore(data);
        var first = new MediaItem("first", Path.Combine(directory, "first.mkv"), "Local title", "Show", 2020, "Local title", 1, 1,
            Overview: "Local plot", ShowId: "shared-show", LockedFields: ["Title"], LocalMetadataFields: ["Overview"]);
        var second = first with { Id = "second", Path = Path.Combine(directory, "second.mkv"), Episode = 2, LockedFields = [], LocalMetadataFields = [] };
        store.Save(first); store.Save(second);
        // A sibling edit updates shared hierarchy without rewriting the first file.
        new LibraryStore(data).Save(second with { Title = "Updated show", Series = "Updated show", Overview = "Shared plot", SeasonOverview = "Updated season",
            Year = 2021, ProviderIds = new() { ["tmdb"] = "77" } });
        var records = store.ByIds([first.Id, second.Id]); var locked = records.Single(x => x.Id == first.Id);
        check(locked.Series == "Local title" && locked.Overview == "Local plot" && locked.Year == 2021 && locked.SeasonOverview == "Updated season" &&
            locked.ProviderIds?["tmdb"] == "77" && records.Single(x => x.Id == second.Id).Series == "Updated show",
            "targeted reads observe current shared hierarchy while preserving per-file locks and NFO fields");
        check(Wire.Serialize(records) == Wire.Serialize(store.All()), "targeted and full reads produce identical hydrated library records");
        check(store.InShow(first).Count == 2 && store.InShow(first with { ShowId = "another-show" }).Count == 0,
            "indexed show reads include the whole group and exclude other identities");
        using var db = new SqliteConnection("Data Source=" + Path.Combine(data, "library.db")); db.Open();
        using (var transaction = db.BeginTransaction())
        {
            using var insert = db.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO media VALUES($id,$path,$json)";
            insert.Parameters.Add("$id", SqliteType.Text); insert.Parameters.Add("$path", SqliteType.Text); insert.Parameters.Add("$json", SqliteType.Text);
            for (var index = 0; index < 520; index++)
            {
                var movie = new MediaItem("movie-" + index, Path.Combine(directory, "movie-" + index + ".mkv"), "Movie " + index, "Movie");
                insert.Parameters["$id"].Value = movie.Id; insert.Parameters["$path"].Value = movie.Path; insert.Parameters["$json"].Value = Wire.Serialize(movie); insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var ids = Enumerable.Range(0, 520).Select(i => "movie-" + i).Concat(["movie-0", "missing"]).ToArray();
        check(store.ByIds(ids).Count == 520 && store.ByIds([]).Count == 0, "targeted reads handle large ID sets, duplicates, missing IDs and empty selections");
        using var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM media WHERE id='movie-0'"; delete.ExecuteNonQuery();
        check(store.ByIds(["movie-0"]).Count == 0, "targeted reads do not resurrect records removed during a fetch");
    }

    private static byte[] Gzip(string xml)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(Encoding.UTF8.GetBytes(xml));
        return output.ToArray();
    }
    private static async Task AnimeIndex(string directory, Action<bool, string> check)
    {
        const string xml = """<animetitles><anime><title xml:lang="x-jat">Romaji</title><title xml:lang="en">English Title</title><title xml:lang="en">English.Title</title><title>Shared</title></anime><anime><title>Shared</title><title xml:lang="en">Other Title</title></anime></animetitles>""";
        var folder = Path.Combine(directory, "anime-index"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "anidb-titles.xml.gz"); var attempt = Path.Combine(folder, "anidb-titles-attempt.txt");
        using var handler = new IndexHandler(Gzip(xml)); using var http = new HttpClient(handler);
        var all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => AnimeTitles.AliasesAsync(http, folder, "Romaji", default)));
        check(handler.Requests == 1 && all.All(x => x.SequenceEqual(new[] { "English Title", "English.Title" })),
            "concurrent AniDB lookups share one validated download and preserve unique title aliases");
        check((await AnimeTitles.AliasesAsync(http, folder, "Shared", default)).Length == 0,
            "indexed AniDB lookup retains ambiguity across different anime");
        all[0][0] = "Changed by caller";
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            check((await AnimeTitles.AliasesAsync(http, folder, "English Title", default))[0] == "English Title",
                "warm AniDB aliases use the in-memory index without reopening the dump or sharing mutable result arrays");
        const string updated = """<animetitles><anime><title>Romaji</title><title xml:lang="en">Replacement English Title</title></anime></animetitles>""";
        await File.WriteAllBytesAsync(path, Gzip(updated)); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        check((await AnimeTitles.AliasesAsync(http, folder, "Romaji", default)).SequenceEqual(new[] { "Replacement English Title" }) && handler.Requests == 1,
            "changing the on-disk AniDB dump invalidates the parsed index without another download");
        handler.Data = Gzip(xml); File.SetLastWriteTimeUtc(attempt, DateTime.UtcNow.AddDays(-2));
        check((await AnimeTitles.AliasesAsync(http, folder, "Romaji", default))[0] == "English Title" && handler.Requests == 2,
            "a successful AniDB refresh installs its new parsed index immediately");
        handler.Data = [1, 2, 3, 4]; File.SetLastWriteTimeUtc(attempt, DateTime.UtcNow.AddDays(-2));
        check((await AnimeTitles.AliasesAsync(http, folder, "Romaji", default))[0] == "English Title" && handler.Requests == 3,
            "invalid AniDB downloads preserve the previous validated dump and index");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var stopped = false;
        try { await AnimeTitles.AliasesAsync(http, folder, "Romaji", cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "AniDB cache hits honor cancellation");
    }

    private sealed class IndexHandler(byte[] data) : HttpMessageHandler
    {
        public int Requests;
        public byte[] Data = data;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) }); }
    }
    private sealed class AliasHandler(bool tied = false) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; string json;
            const string tmdb = """{"id":77,"name":"Original Title","overview":"Matched plot","poster_path":null,"genres":[]}""";
            const string maze = """{"id":77,"name":"Original Title","type":"Scripted","summary":"Matched plot","url":"https://www.tvmaze.com/shows/77","image":null}""";
            if (uri.AbsolutePath.Contains("/search/"))
            {
                var query = Uri.UnescapeDataString(uri.Query).Split('&').Select(x => x.TrimStart('?').Split('=', 2))
                    .First(x => x[0] is "q" or "query")[1]; Queries.Add(query);
                var result = query == "Original Title" ? (uri.Host == "api.tvmaze.com" ? "{\"show\":" + maze + "}" : tmdb) : "";
                if (tied && result.Length > 0) result += "," + result.Replace("77", "78");
                json = uri.Host == "api.tvmaze.com" ? "[" + result + "]" : "{\"results\":[" + result + "]}";
            }
            else if (uri.AbsolutePath.Contains("/season/")) json = """{"episodes":[{"episode_number":1,"name":"Episode 1"},{"episode_number":2,"name":"Episode 2"}]}""";
            else if (uri.AbsolutePath.EndsWith("/seasons")) json = """[{"number":1,"name":"Season 1"}]""";
            else if (uri.AbsolutePath.EndsWith("/episodes")) json = """[{"season":1,"number":1,"name":"Episode 1"},{"season":1,"number":2,"name":"Episode 2"}]""";
            else json = uri.Host == "api.tvmaze.com" ? maze : tmdb;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
