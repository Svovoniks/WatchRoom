using System.Net;
using System.Text.Json;
using Watchroom.Core;

static class TmdbAccessFixture
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Images;
        public bool Authorized;
        public bool ImageAuthorized;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var image = request.RequestUri!.Host == "image.tmdb.org";
            var auth = request.Headers.Authorization is not null || request.RequestUri.Query.Contains("api_key=");
            if (image) { Images++; ImageAuthorized |= auth; } else Authorized |= auth;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = image ? new ByteArrayContent(ArtworkFixture.Png) : new StringContent("{\"results\":[]}") });
        }
    }
    private static MediaItem Age(MediaItem item) => item with
    {
        TmdbFields = item.TmdbFields!.ToDictionary(x => x.Key, x => x.Value with
        { FetchedAt = Wire.Now - (long)TimeSpan.FromDays(176).TotalMilliseconds })
    };
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        check(TmdbCredentials.Select(null, "fixture-default") == "fixture-default", "blank personal key selects bundled credential");
        check(TmdbCredentials.Select(" personal-key ", "fixture-default") == "personal-key", "personal credential overrides bundled credential");
        check(TmdbCredentials.Select("", null) is null, "developer builds without a key retain credential-free fallbacks");
        try { TmdbCredentials.Select("bad\r\ncredential", null); check(false, "invalid credential rejected"); }
        catch (ArgumentException ex) { check(!ex.Message.Contains("credential\r"), "invalid credential is rejected without echoing its value"); }
        using var handler = new Handler(); using var client = new MetadataClient("fixture-token", handler);
        await client.SearchAsync("Example", true);
        check(handler.Authorized, "metadata requests use the application credential");
        var imageDir = Path.Combine(directory, "posters");
        var poster = (await client.CacheImageAsync("/example.jpg", "tv-1", imageDir))!;
        await client.CacheImageAsync("/example.jpg", "tv-1", imageDir);
        check(handler.Images == 1, "fresh downloaded artwork is reused");
        File.SetLastWriteTimeUtc(poster, DateTime.UtcNow.AddDays(-176));
        await client.CacheImageAsync("/example.jpg", "tv-1", imageDir);
        check(handler.Images == 2, "expired downloaded artwork is fetched again");
        check(!handler.ImageAuthorized, "application credential never reaches image CDN");

        var original = new MediaItem("one", Path.Combine(directory, "Local Show - 01.mkv"), "Local Show", "Show", Series: "Local Show", Season: 1, Episode: 1,
            Overview: "My synopsis", ProviderIds: new() { ["tvmaze"] = "77" }, TmdbRetentionVersion: 1);
        var remote = TmdbRetention.Record(original, original with { Series = "Provider Show", Overview = "Provider synopsis", MetadataProvider = "tmdb", MetadataId = 1,
            Poster = poster, SeriesPoster = poster, PosterSource = "https://www.themoviedb.org/tv/1", ProviderIds = new() { ["tvmaze"] = "77", ["tmdb"] = "1" } }, "tmdb",
            TmdbRetention.TitleFields.Concat(TmdbRetention.PosterFields).ToArray());
        var store = new LibraryStore(directory); store.Save(Age(remote));
        store.ExpireTmdbContent(force: true);
        var restored = store.All().Single();
        check(restored.Series == original.Series && restored.Overview == original.Overview, "expired TMDB fields restore local values through shared show hydration");
        check(restored.MetadataId is null && !restored.ProviderIds!.ContainsKey("tmdb") && restored.ProviderIds["tvmaze"] == "77", "expiry removes downloaded identity while preserving independent provider IDs");
        check(restored.Poster is null && restored.SeriesPoster is null, "expired artwork references are cleared from both media and hierarchy");
        check(store.Shows().Single().Title == "Local Show" && store.Shows().Single().Poster is null, "shared hierarchy cannot resurrect expired title or artwork");

        var edited = Age(remote) with { Overview = "User replacement", LockedFields = ["Overview"] };
        store.Save(edited); store.ExpireTmdbContent(force: true);
        check(store.All().Single().Overview == "User replacement", "independently edited and locked text survives expiry");
        store.Save(Age(remote) with { MetadataLocked = true }); store.ExpireTmdbContent(force: true);
        check(store.All().Single().Overview == "My synopsis", "locking unchanged downloaded metadata does not bypass expiry");

        var fresh = TmdbRetention.Record(original, original with { Overview = "Fresh TMDB synopsis" }, "tmdb", "overview");
        store.Save(fresh); store.ExpireTmdbContent(force: true);
        check(store.All().Single().Overview == "Fresh TMDB synopsis", "unexpired TMDB content is retained");
        LocalMetadata.Export(fresh);
        check(!File.ReadAllText(Path.ChangeExtension(fresh.Path, ".nfo")).Contains("Fresh TMDB synopsis"), "NFO export does not create permanent copies of tracked TMDB text");

        File.SetLastWriteTimeUtc(poster, DateTime.UtcNow.AddDays(-176));
        var localPoster = Path.Combine(imageDir, "my-poster.jpg"); File.WriteAllBytes(localPoster, ArtworkFixture.Png); File.SetLastWriteTimeUtc(localPoster, DateTime.UtcNow.AddDays(-200));
        var httpCache = Path.Combine(directory, "metadata-http-cache"); Directory.CreateDirectory(httpCache);
        var response = Path.Combine(httpCache, "old.json"); File.WriteAllText(response, "{}"); File.SetLastWriteTimeUtc(response, DateTime.UtcNow.AddDays(-10));
        store.ExpireTmdbContent(force: true);
        check(!File.Exists(poster) && File.Exists(localPoster), "offline maintenance removes old app-owned TMDB images and preserves local artwork");
        check(!File.Exists(response), "offline maintenance prunes old HTTP cache without a request");

        var nfoPoster = await AutomaticArtwork.Download(new HttpClient(handler), "https://image.tmdb.org/t/p/w500/nfo.png", directory, default);
        File.SetLastWriteTimeUtc(nfoPoster, DateTime.UtcNow.AddDays(-176)); store.ExpireTmdbContent(force: true);
        check(!File.Exists(nfoPoster), "TMDB images fetched through NFO URLs obey offline retention");

        var report = Path.Combine(directory, "metadata-fetch-report.json"); File.WriteAllText(report, "{}"); File.SetLastWriteTimeUtc(report, DateTime.UtcNow.AddDays(-176));
        store.Setting("metadata-outstanding-v2", Wire.Serialize(new[] { new MetadataIssues.Issue("old", "title", "Old title", "no-candidates", ["one"], "", Wire.Now - (long)TimeSpan.FromDays(176).TotalMilliseconds) }));
        store.ExpireTmdbContent(force: true);
        check(!File.Exists(report) && store.Setting("metadata-outstanding-v2") == "[]", "offline maintenance expires old reports and outstanding issues");

        store.Save(Age(remote));
        using (var source = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(directory, "library.db")))
        using (var backup = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(directory, "library-before-metadata-repair-v1.db")))
        { source.Open(); backup.Open(); source.BackupDatabase(backup); }
        store.ExpireTmdbContent(force: true);
        using (var backup = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(directory, "library-before-metadata-repair-v1.db")))
        {
            backup.Open(); using var read = backup.CreateCommand(); read.CommandText = "SELECT json FROM media WHERE id='one'";
            var recovered = Wire.Read<MediaItem>((string)read.ExecuteScalar()!);
            check(recovered.Overview == "My synopsis" && recovered.MetadataId is null, "repair backups expire TMDB content while retaining recoverable local records");
        }

        var logs = Path.Combine(directory, "logs"); Directory.CreateDirectory(logs);
        var logFile = Path.Combine(logs, "diagnostics.jsonl");
        File.WriteAllLines(logFile, [Wire.Serialize(new { kind = "metadata-candidates", utcMs = Wire.Now - (long)TimeSpan.FromDays(176).TotalMilliseconds, data = "Old provider title" }),
            Wire.Serialize(new { kind = "playback", utcMs = 1, data = "Playback information" })]);
        await using (var log = new DiagnosticLog(logs)) { await log.FlushAsync(); }
        check(!File.ReadAllText(logFile).Contains("Old provider title") && File.ReadAllText(logFile).Contains("Playback information"), "diagnostic retention removes old metadata and preserves playback records");

        var legacy = new MediaItem("legacy", Path.Combine(directory, "Original Movie (2000).mkv"), "Remote Movie", "Movie", Overview: "Legacy text", MetadataProvider: "tmdb", MetadataId: 2);
        store.Save(legacy); store.ExpireTmdbContent(force: true);
        check(store.All().Single(x => x.Id == "legacy").Title == "Original Movie" && store.All().Single(x => x.Id == "legacy").Overview is null,
            "legacy TMDB content with unknown fetch dates expires conservatively");
        if (TmdbCredentials.HasBundledKey)
        {
            using var bundledHandler = new Handler(); using var bundledClient = new MetadataClient(null, bundledHandler);
            await bundledClient.SearchAsync("Example", true);
            check(bundledHandler.Authorized, "compiled bundled credential enables requests without a personal key");
        }
    }
}
