using System.Net;
using System.Text.Json;
using Watchroom.Core;

static class MetadataCacheFixture
{
    private sealed class CountingHandler(bool fail = false) : DelegatingHandler(new ArtworkFixture())
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return fail ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : base.SendAsync(request, ct);
        }
    }
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var poster = Path.Combine(directory, "poster.png"); await File.WriteAllBytesAsync(poster, ArtworkFixture.Png);
        var complete = new LibraryStore(Path.Combine(directory, "complete"));
        new MetadataOptions(Providers: ["tmdb", "tvmaze"]).Save(complete);
        var cached = new MediaItem("cached", "cached.mkv", "Example Show", "Show", Series: "Example Show", Season: 1, Episode: 1,
            Poster: poster, SeriesPoster: poster, Overview: "Already fetched", MetadataProvider: "tvmaze", MetadataId: 1,
            MetadataKind: "Show", MetadataType: "tv", ArtworkFetchedAt: Wire.Now, MetadataFetchedAt: Wire.Now);
        complete.Save(cached);
        using var completeHandler = new CountingHandler(); using var completeHttp = new HttpClient(completeHandler);
        await AutomaticArtwork.FetchAsync(complete, directory, "fixture-token", null, default, completeHttp);
        check(completeHandler.Requests == 0, "fresh metadata with no TMDB ID, year or episode title makes no network requests");

        var pendingDirectory = Path.Combine(directory, "pending");
        var pending = new LibraryStore(pendingDirectory); new MetadataOptions(Providers: ["tvmaze"]).Save(pending);
        pending.Save(new("unknown", "unknown.mkv", "Completely Unknown Series", "Show", Series: "Completely Unknown Series", Season: 1, Episode: 1));
        using var handler = new CountingHandler(); using var http = new HttpClient(handler);
        await AutomaticArtwork.FetchAsync(pending, directory, null, null, default, http);
        var initial = handler.Requests; check(initial > 0, "new unmatched titles are checked immediately");
        pending = new LibraryStore(pendingDirectory);
        await AutomaticArtwork.FetchAsync(pending, directory, null, null, default, http);
        check(handler.Requests == initial, "unmatched-title cooldown survives app restart without marking a false match");
        check(pending.All().Single().MetadataId is null && pending.All().Single().ArtworkFetchedAt == 0, "unsuccessful attempts remain separate from successful metadata timestamps");
        await AutomaticArtwork.FetchAsync(pending, directory, null, null, default, http, refresh: MetadataRefresh.FillMissing);
        check(handler.Requests > initial, "manual fill-missing refresh bypasses retry cooldown");
        initial = handler.Requests;
        new MetadataOptions(Providers: ["tvmaze"], Language: "ru-RU").Save(pending);
        await AutomaticArtwork.FetchAsync(pending, directory, null, null, default, http);
        check(handler.Requests > initial, "changed provider options invalidate failed-match cooldown");
        initial = handler.Requests;
        foreach (var stage in new[] { "title", "episodes" })
        {
            var key = "metadata-retry:" + stage + ":unknown";
            using var state = JsonDocument.Parse(pending.Setting(key)!);
            pending.Setting(key, Wire.Serialize(new { fingerprint = state.RootElement.GetProperty("fingerprint").GetString(), retryAt = Wire.Now - 1 }));
        }
        await AutomaticArtwork.FetchAsync(pending, directory, null, null, default, http);
        using (var retry = JsonDocument.Parse(pending.Setting("metadata-retry:title:unknown")!))
            check(retry.RootElement.GetProperty("retryAt").GetInt64() > Wire.Now,
                "unmatched titles are re-evaluated when the cooldown expires, including with cached provider responses");

        var errors = new LibraryStore(Path.Combine(directory, "errors")); new MetadataOptions(Providers: ["tvmaze"]).Save(errors);
        errors.Save(new("error", "error.mkv", "Example Show", "Show", Series: "Example Show", Season: 1, Episode: 1));
        using var failureHandler = new CountingHandler(fail: true); using var failureHttp = new HttpClient(failureHandler);
        await AutomaticArtwork.FetchAsync(errors, directory, null, null, default, failureHttp);
        initial = failureHandler.Requests;
        await AutomaticArtwork.FetchAsync(errors, directory, null, null, default, failureHttp);
        check(failureHandler.Requests == initial && initial > 0, "provider errors do not repeat on every startup");

        var episodes = new LibraryStore(Path.Combine(directory, "episodes")); new MetadataOptions(Providers: ["tvmaze"]).Save(episodes);
        episodes.Save(cached with { Id = "unlisted", Episode = 99, EpisodeTitle = null });
        using var episodeHandler = new CountingHandler(); using var episodeHttp = new HttpClient(episodeHandler);
        await SeriesMetadata.FetchAsync(episodes, directory, null, null, default, episodeHttp);
        check(episodeHandler.Requests == 0, "provider-missing episode titles reuse fresh season metadata");
        await SeriesMetadata.FetchAsync(episodes, directory, null, null, default, episodeHttp, refresh: MetadataRefresh.FillMissing);
        check(episodeHandler.Requests > 0 && episodes.All().Single().EpisodeTitle is null, "manual retry fetches the season even if an episode title is unavailable");
        initial = episodeHandler.Requests;
        await SeriesMetadata.FetchAsync(episodes, directory, null, null, default, episodeHttp);
        check(episodeHandler.Requests == initial, "unavailable provider episode titles do not trigger another automatic fetch");
        episodes.Save(cached with { Id = "new-episode", Path = "new-episode.mkv", Episode = 1, MetadataFetchedAt = 0 });
        await SeriesMetadata.FetchAsync(episodes, directory, null, null, default, episodeHttp);
        check(episodes.All().Single(x => x.Id == "new-episode") is { MetadataFetchedAt: > 0, EpisodeTitle: "Pilot" },
            "newly added episodes bypass older episode cooldowns and can use cached season metadata");
        initial = episodeHandler.Requests;
        foreach (var item in episodes.All()) episodes.Save(item with { MetadataFetchedAt = Wire.Now - (long)TimeSpan.FromDays(8).TotalMilliseconds });
        // Expire recorded retries as well as success timestamps to model the scheduled refresh.
        foreach (var item in episodes.All())
        {
            var key = "metadata-retry:episodes:" + item.Id;
            using var state = JsonDocument.Parse(episodes.Setting(key)!);
            episodes.Setting(key, Wire.Serialize(new { fingerprint = state.RootElement.GetProperty("fingerprint").GetString(), retryAt = Wire.Now - 1 }));
        }
        await SeriesMetadata.FetchAsync(episodes, directory, null, null, default, episodeHttp);
        check(episodes.All().All(x => x.MetadataFetchedAt > Wire.Now - 60_000), "scheduled refresh still updates expired episode metadata");
    }
}
