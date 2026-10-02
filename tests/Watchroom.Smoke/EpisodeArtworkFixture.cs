using System.Net;
using Watchroom.Core;

internal static class EpisodeArtworkFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "short episode with spaces.avi"); VideoFixture.Write(video, 3);
        var item = new MediaItem("thumbnail", video, "Example", "Show", Series: "Example", Season: 1, Episode: 1);
        var service = new EpisodeArtwork();
        var result = await service.ImproveAsync(item, directory, null, "ffmpeg", default);
        check(ArtworkCache.IsUsable(result.GeneratedEpisodePoster), "short video gets a local thumbnail after unknown-duration seek fallback");
        check(LibraryCatalog.EpisodeArtwork(result) == result.GeneratedEpisodePoster, "generated episode frame takes priority over series fallback");
        var repeated = await service.ImproveAsync(result, directory, null, "missing-ffmpeg", default);
        check(repeated == result, "local episode thumbnail is reused without starting FFmpeg again");
        var locked = item with { LockedFields = ["EpisodePoster"] };
        check(await service.ImproveAsync(locked, directory, null, "ffmpeg", default) == locked, "manual episode artwork locks prevent thumbnail replacement");
        check(await EpisodeArtwork.GenerateAsync(item with { Id = "unavailable", Path = video + ".missing" }, directory, null, default) is null, "unavailable file cannot start thumbnail extraction");
        check(await EpisodeArtwork.GenerateAsync(item, Path.Combine(directory, "no-ffmpeg"), "nonexistent-watchroom-ffmpeg", default) is null, "missing FFmpeg leaves the existing artwork fallback usable");
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
