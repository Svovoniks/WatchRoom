using System.Net;
using BlackPearl.Core;

internal static class EpisodeArtworkFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var lookups = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<string?>>>(); var calls = 0;
        async Task<string?> Lookup(Func<Task<string?>> lookup) { try { return await EpisodeArtwork.Cached(lookups, "show:1:1", lookup); } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return "failed"; } }
        var failed = await Lookup(() => { calls++; return Task.FromException<string?>(new HttpRequestException("offline")); });
        var cancelled = await Lookup(() => { calls++; return Task.FromCanceled<string?>(new CancellationToken(true)); });
        var recovered = await Lookup(() => { calls++; return Task.FromResult<string?>("image"); });
        var cached = await Lookup(() => { calls++; return Task.FromResult<string?>("other"); });
        check(failed == "failed" && cancelled == "failed" && recovered == "image" && cached == "image" && calls == 3, "failed or cancelled episode lookups are retried while successful ones stay cached");
        var video = Path.Combine(directory, "short episode with spaces.avi"); VideoFixture.Write(video, 3);
        var item = new MediaItem("thumbnail", video, "Example", "Show", Series: "Example", Season: 1, Episode: 1);
        // Stands in for the desktop LibVLC grabber: seeks past the end of the 3-second video yield no frame.
        var seeks = new List<double>();
        async Task<bool> Capture(string path, double seconds, string output, CancellationToken ct)
        {
            seeks.Add(seconds); if (path != video || seconds > 3) return false;
            await File.WriteAllBytesAsync(output, ArtworkFixture.Png, ct); return true;
        }
        var service = new EpisodeArtwork(Capture);
        var result = await service.ImproveAsync(item, directory, null, default);
        check(ArtworkCache.IsUsable(result.GeneratedEpisodePoster), "short video gets a local thumbnail after unknown-duration seek fallback");
        check(seeks.SequenceEqual([180d, 20d, 5d, 1d]), "thumbnail capture skips opening credits, then retries earlier and earlier");
        check(LibraryCatalog.EpisodeArtwork(result) == result.GeneratedEpisodePoster, "generated episode frame takes priority over series fallback");
        var still = Path.Combine(directory, "still.png"); var showPoster = Path.Combine(directory, "show.png");
        File.WriteAllBytes(still, ArtworkFixture.Png); File.WriteAllBytes(showPoster, ArtworkFixture.Png);
        var shared = item with { Poster = showPoster, SeriesPoster = showPoster, EpisodePoster = still };
        check(LibraryCatalog.EpisodeArtwork(shared) == still, "episode stills outrank a show poster shared without its source");
        check(LibraryCatalog.EpisodeArtwork(shared with { PosterSource = "Local artwork", Poster = result.GeneratedEpisodePoster }) == result.GeneratedEpisodePoster,
            "local per-episode artwork still outranks fetched stills");
        seeks.Clear();
        var repeated = await service.ImproveAsync(result, directory, null, default);
        check(repeated == result && seeks.Count == 0, "local episode thumbnail is reused without decoding the video again");
        var locked = item with { LockedFields = ["EpisodePoster"] };
        check(await service.ImproveAsync(locked, directory, null, default) == locked && seeks.Count == 0, "manual episode artwork locks prevent thumbnail replacement");
        check(await EpisodeArtwork.GenerateAsync(item with { Id = "unavailable", Path = video + ".missing" }, directory, Capture, default) is null, "unavailable file cannot start thumbnail extraction");
        check(await EpisodeArtwork.GenerateAsync(item, Path.Combine(directory, "no-frame"), (_, _, _, _) => Task.FromResult(false), default) is null, "undecodable video leaves the existing artwork fallback usable");
        check(await new EpisodeArtwork().ImproveAsync(item, Path.Combine(directory, "no-capture"), null, default) == item, "without a frame grabber episodes keep their existing artwork");
        using var handler = new ImageHandler(); using var metadata = new MetadataClient("fixture-token", handler);
        var smaller = await metadata.CacheImageAsync("/still.jpg", "episode", directory);
        var larger = await metadata.CacheImageAsync("/still.jpg", "episode", directory, imageSize: "w780");
        check(smaller != larger && handler.Urls.Any(x => x.Contains("/w780/")), "larger episode stills use a separate cache entry and w780 request");
        check(!handler.Authenticated, "episode image CDN requests never contain the metadata credential");
    }
    private sealed class ImageHandler : HttpMessageHandler
    {
        public List<string> Urls = []; public bool Authenticated;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString()); Authenticated |= request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkFixture.Png) });
        }
    }
}
