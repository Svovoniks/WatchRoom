using Watchroom.Core;
using System.Net;

static class LibraryIngestionFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var gto = FileNames.Parse(Path.Combine(directory, "[Kanjouteki] GTO - Great Teacher Onizuka - E01v2 [1080p AI Upscale][x265].mkv"));
        check(gto.Series == "GTO - Great Teacher Onizuka" && gto.Episode == 1 && gto.Kind == "Anime", "versioned E01v2 release groups into its anime show");
        var outlaw = FileNames.Parse(Path.Combine(directory, "Outlaw Star - 01 (BDRip 1440x1080p x265 HEVC AC3, FLACx2 2.0x3)(Dual Audio)[sxales].mkv"));
        check(outlaw.Series == "Outlaw Star" && outlaw.Season == 1 && outlaw.Episode == 1, "audio channel layout is not parsed as an episode");
        check(FileNames.Parse(Path.Combine(directory, "Outlaw Star - 26 END (BDRip 1080p).mkv")).Episode == 26, "last-episode END suffix preserves grouping");
        check(FileNames.Parse(Path.Combine(directory, "[Subsplease] Example - 11V2 (1080P).mp4")).Episode == 11, "uppercase release version recognized");
        check(FileNames.Parse(Path.Combine(directory, "Example S01E02v2.mkv")).Episode == 2, "versioned season/episode marker recognized");
        check(FileNames.Parse(Path.Combine(directory, "Movie 2024 (1080p x265).mkv")).Series is null, "ordinary movie remains outside show groups");
        var pink = FileNames.Parse(Path.Combine(directory, "Anime", "The Pink Panther", "001 The Pink Phink (Dec 18, 1964).mp4"));
        check(pink.Series == "The Pink Panther" && pink.Episode == 1 && pink.Year is null, "numbered cartoons use folder show name rather than episode date");
        var onePiece = FileNames.Parse(Path.Combine(directory, "Anime", "One Piece", "One Piece Season 19 (Whole Cake Island) EP 783-In Progress", "[Kayoanime] - 0783.mkv"));
        check(onePiece.Series == "One Piece" && onePiece.Season == 19 && onePiece.Episode == 783, "four-digit absolute anime episode uses named season folder");
        var bean = FileNames.Parse(Path.Combine(directory, "Shows", "Mr. Bean", "Mr. Bean 01.mp4"));
        check(bean.Series == "Mr Bean" && bean.Episode == 1, "plain numbered show suffix groups episodes");
        var russian = FileNames.Parse(Path.Combine(directory, "Shows", "Подозрительная Сова", "02 сезон", "Подозрительная Сова - 02 сезон 03 серия.avi"));
        check(russian.Series == "Подозрительная Сова" && russian.Season == 2 && russian.Episode == 3, "Russian season and episode labels recognized");
        check(FileNames.Parse(Path.Combine(directory, "Movies", "2001 A Space Odyssey.mp4")).Series is null,
            "movie release year is not inferred as a numbered episode");
        var caseVariant = outlaw with { Id = "case-variant", Series = "OUTLAW STAR", Episode = 2 };
        check(LibraryCatalog.Browse([outlaw, caseVariant]).Count == 1
            && LibraryCatalog.Browse([outlaw, caseVariant], "Outlaw Star", outlaw.Kind, 1).Count == 2,
            "case differences keep episodes in one browsable show");

        var media = Path.Combine(directory, "media"); Directory.CreateDirectory(media);
        var firstPath = Path.Combine(media, "Example S01E01.mkv"); var secondPath = Path.Combine(media, "Example S01E02.mkv");
        await File.WriteAllBytesAsync(firstPath, [0]); await File.WriteAllBytesAsync(secondPath, [0]);
        var store = new LibraryStore(Path.Combine(directory, "scan-db"));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var notified = 0;
        void OnSave(MediaItem item)
        {
            if (Interlocked.Increment(ref notified) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Scan fixture not released");
        }
        store.MediaSaved += OnSave;
        var scan = store.ScanAsync([new(media)]);
        try { check(entered.Wait(TimeSpan.FromSeconds(10)) && !scan.IsCompleted && store.All().Count == 1,
            "scan publishes a committed item before the remaining files finish"); }
        finally { release.Set(); }
        await scan; store.MediaSaved -= OnSave;
        check(notified == 2 && LibraryCatalog.Browse(store.All()).Count == 1, "incremental scan groups both episodes into one show");

        var poster = Path.Combine(directory, "season.png"); await File.WriteAllBytesAsync(poster, ArtworkFixture.Png);
        var artworkStore = new LibraryStore(Path.Combine(directory, "artwork-db"));
        artworkStore.Save(new("matched", "matched.mkv", "Example Show", "Show", 2020, "Example Show", 1, 1,
            Poster: Path.Combine(directory, "deleted.png"), Matched: true, SeasonPoster: poster, Overview: "Keep my overview",
            MetadataProvider: "tvmaze", MetadataId: 1, MetadataKind: "Show", MetadataType: "tv"));
        check(LibraryCatalog.Browse(artworkStore.All()).Single().Poster == poster, "show card uses season artwork while show poster is missing");
        var posterPublished = false; var textPublished = false;
        artworkStore.MediaSaved += item => { posterPublished |= File.Exists(item.SeriesPoster); textPublished |= item.EpisodeTitle == "Pilot"; };
        using var http = new HttpClient(new ArtworkFixture());
        await AutomaticArtwork.FetchAsync(artworkStore, directory, null, null, CancellationToken.None, http);
        var repaired = artworkStore.All().Single();
        check(File.Exists(repaired.SeriesPoster) && repaired.SeriesPoster != poster, "matched title with only a season image still downloads a show poster");
        check(repaired.Overview == "Keep my overview", "poster repair preserves an existing overview");
        check(posterPublished && textPublished, "poster and episode metadata changes publish individually");

        var failures = new LibraryStore(Path.Combine(directory, "failure-db"));
        foreach (var title in new[] { "A Fail", "B Fail", "C Fail", "Example Show" })
            failures.Save(new(title, title + ".mkv", title, "Show", 2020, title, 1, 1));
        using var failingHttp = new HttpClient(new FirstThreeFailures());
        await AutomaticArtwork.FetchAsync(failures, Path.Combine(directory, "failure-artwork"), null, null, CancellationToken.None, failingHttp);
        check(File.Exists(failures.All().Single(item => item.Id == "Example Show").SeriesPoster),
            "three earlier provider failures do not skip later shows");
    }
    private sealed class FirstThreeFailures : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(new ArtworkFixture());
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Query.Contains("Fail") ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
                : inner.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
