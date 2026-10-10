using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Watchroom.Core;

static class MetadataDiscoveryFixture
{
    public static async Task Run(string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        await LabeledTitles(directory, check);
        await ProviderAliases(directory, check);
        await Responses(directory, check);
        await Pacing(check);
    }
    private static MediaItem Show(string title, int? year = 2020) => new("file", "episode.mkv", title, "Show", year, title, 1, 1);
    private static MetadataMatch Match(int id, string title, string? year = "2020", string type = "tv", params string[] aliases) =>
        new(id, type, title, year, null, "Plot", type == "tv" ? "Show" : "Movie", Animated: false, Aliases: aliases);
    private static async Task LabeledTitles(string directory, Action<bool, string> check)
    {
        var cases = new (string Name, MediaItem Item, MetadataMatch[] Results, int? Expected)[]
        {
            ("literal title", Show("Harbor"), [Match(1, "Harbor")], 1),
            ("original title", Show("Harbor"), [Match(1, "Le Port") with { OriginalTitle = "Harbor" }], 1),
            ("regional show alias", Show("Local Harbor"), [Match(1, "Le Port", aliases: ["Local Harbor"])], 1),
            ("regional movie alias", new("film", "film.mkv", "Local Harbor", "Movie", 2020, SourceLibraryKind: "Movie"), [Match(1, "Le Port", type: "movie", aliases: ["Local Harbor"])], 1),
            ("missing letter", Show("Harbr"), [Match(1, "Harbor")], 1),
            ("extra letter", Show("Harrbor"), [Match(1, "Harbor")], 1),
            ("transposed letters", Show("Habror"), [Match(1, "Harbor")], 1),
            ("misspelled provider alias", Show("Local Harbr"), [Match(1, "Le Port", aliases: ["Local Harbor"])], 1),
            ("Cyrillic spelling", Show("Корабл"), [Match(1, "Корабль")], 1),
            ("no year", Show("Harbr", null), [Match(1, "Harbor")], null),
            ("adjacent year", Show("Harbr", 2021), [Match(1, "Harbor")], null),
            ("unknown candidate year", Show("Harbr"), [Match(1, "Harbor", null)], null),
            ("unknown type", new("file", "file.mkv", "Harbr", "Mixed", 2020), [Match(1, "Harbor")], null),
            ("short title", Show("Dark"), [Match(1, "Dare")], null),
            ("two spelling errors", Show("Harrborz"), [Match(1, "Harbor")], null),
            ("same-name remake tie", Show("Harbor"), [Match(1, "Harbor"), Match(2, "Harbor")], null),
            ("alias tie", Show("Local Harbor"), [Match(1, "Port One", aliases: ["Local Harbor"]), Match(2, "Port Two", aliases: ["Local Harbor"])], null),
            ("typo tie", Show("Harbr"), [Match(1, "Harbor"), Match(2, "Harber")], null),
            ("near competitor", Show("Harbr"), [Match(1, "Harbor"), Match(2, "Harbors")], null),
            ("unknown-year competitor", Show("Harbr"), [Match(1, "Harbor"), Match(2, "Harbor", null)], null),
            ("changed sequel number", Show("Harbor 2"), [Match(1, "Harbor 3")], null),
            ("film versus show", Show("Harbr"), [Match(1, "Harbor", type: "movie")], null),
            ("live-action versus anime", Show("Harbr") with { Kind = "Anime" }, [Match(1, "Harbor")], null),
            ("anime evidence unknown", Show("Harbr") with { Kind = "Anime" }, [Match(1, "Harbor") with { Animated = null }], null),
            ("confirmed animation", Show("Harbr") with { Kind = "Anime" }, [Match(1, "Harbor") with { Animated = true, Kind = "Anime" }], 1),
            ("long title prefix", Show("A Rather Long Harbor Story"), [Match(1, "A Rather Long Harbor Story Continued")], 1)
        };
        var rows = new List<object>(); int beforeCorrect = 0, afterCorrect = 0, wrong = 0, unresolved = 0;
        var timer = Stopwatch.StartNew();
        foreach (var example in cases)
        {
            var plain = example.Results.Select(x => x with { Aliases = null }).ToArray();
            var before = MetadataMatching.Choose(plain, example.Item)?.Id;
            var after = await MetadataMatching.ChooseWithAliasesAsync(plain, example.Item,
                (x, _) => Task.FromResult(example.Results.Single(e => e.Id == x.Id && e.Type == x.Type).Aliases ?? []), default);
            check(after?.Id == example.Expected, "discovery corpus: " + example.Name);
            if (example.Expected is not null && before == example.Expected) beforeCorrect++;
            if (example.Expected is not null && after?.Id == example.Expected) afterCorrect++;
            if (after is not null && after.Id != example.Expected) wrong++;
            if (example.Expected is not null && after is null) unresolved++;
            rows.Add(new { example.Name, example.Expected, Before = before, After = after?.Id });
        }
        var aliasRequests = 0;
        var literal = await MetadataMatching.ChooseWithAliasesAsync([Match(1, "Harbor")], Show("Harbor"), (_, _) => { aliasRequests++; return Task.FromResult(Array.Empty<string>()); }, default);
        check(literal?.Id == 1 && aliasRequests == 0, "literal matches avoid provider alias requests");
        var crowded = await MetadataMatching.ChooseWithAliasesAsync(Enumerable.Range(1, 5).Select(i => Match(i, "Port " + i)), Show("Local Harbor"),
            (_, _) => { aliasRequests++; return Task.FromResult(new[] { "Local Harbor" }); }, default);
        check(crowded is null && aliasRequests == 0, "crowded alias searches stay unresolved without a truncated shortlist");
        var partial = await MetadataMatching.ChooseWithAliasesAsync([Match(1, "Port One"), Match(2, "Port Two")], Show("Local Harbor"),
            (x, _) => x.Id == 1 ? Task.FromResult(new[] { "Local Harbor" }) : Task.FromException<string[]>(new HttpRequestException("Unavailable")), default);
        check(partial is null, "failed alias lookup cannot turn a partially checked shortlist into a winner");
        check(MetadataMatching.ChooseTypoFallback([(Show("Harbr"), new[] { Match(1, "Harbor") }), (Show("Valey"), new[] { Match(2, "Valley") })]) is null,
            "conflicting typo matches across filename and folder evidence remain unresolved");
        check(MetadataMatching.ChooseTypoFallback([(Show("Harbr"), new[] { Match(1, "Harbor") }), (Show("Harbr"), new[] { Match(1, "Harbor"), Match(2, "Harber", "2021") })]) is null,
            "broadening a year-filtered search cannot hide a near-name competitor from typo matching");
        check(MetadataMatching.ChooseTypoFallback([(Show("Harbr"), new[] { Match(1, "Harbor") }), (Show("Harbr"), null)]) is null,
            "an incomplete later search blocks automatic typo fallback");
        var report = new { Cases = cases.Length, Resolvable = cases.Count(x => x.Expected is not null), BeforeCorrect = beforeCorrect, AfterCorrect = afterCorrect,
            Wrong = wrong, Unresolved = unresolved, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds, Note = "Synthetic labeled corpus; not a personal-library accuracy estimate.", Rows = rows };
        await File.WriteAllTextAsync(Path.Combine(directory, "discovery-corpus.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Discovery corpus: {beforeCorrect} -> {afterCorrect} correct; {wrong} wrong; {unresolved} resolvable titles remain unresolved.");
    }
    private static async Task ProviderAliases(string directory, Action<bool, string> check)
    {
        using var handler = new AliasProvider(); using var http = new HttpClient(handler); using var tmdb = new MetadataClient("fixture", handler);
        var movie = new MediaItem("film", "film.mkv", "Local Harbor", "Movie", 2020, SourceLibraryKind: "Movie");
        var tvMatch = await tmdb.MatchAsync(Show("Local Harbor")); var movieMatch = await tmdb.MatchAsync(movie);
        check(tvMatch is { Id: 1, Title: "Le Port" } && movieMatch is { Id: 2, Type: "movie" } &&
            handler.Paths.Contains("/3/tv/1/alternative_titles") && handler.Paths.Contains("/3/movie/2/alternative_titles"),
            "TMDB reads TV and movie alternative-title formats and retains canonical titles");
        var maze = await AutomaticArtwork.ResolveAsync(http, null, Show("Local Harbor"), new(Providers: ["tvmaze"]), directory, default);
        check(maze is { Id: 3, Title: "Le Port" } && handler.Paths.Contains("/shows/3/akas"), "TVmaze aliases identify a regional title");
        var typo = await tmdb.MatchAsync(Show("Harbr"));
        check(typo?.Id == 4, "TMDB search results can resolve a conservative spelling error");
        var exactFilename = await tmdb.MatchAsync(Show("Harbr"), queries: ["Harbr", "Exact Filename"]);
        check(exactFilename?.Id == 5, "exact filename evidence is tried before a typo fallback");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); var stopped = false;
        try { await tmdb.MatchAsync(Show("Local Harbor"), cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "cached alias and search results honor cancellation");
    }
    private static async Task Responses(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "responses"); var clock = new TestClock(); var cache = new MetadataResponseCache(path, clock: clock);
        using var handler = new JsonHandler(); using var http = new HttpClient(handler); using var second = new HttpClient(handler, false);
        const string url = "https://cache-fixture.invalid/show/1?language=en-US&api_key=private-key";
        var requests = Enumerable.Range(0, 12).Select(i => cache.Get(i % 2 == 0 ? http : second, url, default));
        var bodies = await Task.WhenAll(requests);
        check(handler.Requests == 1 && bodies.Distinct().Count() == 1, "identical in-flight requests across clients share one successful response");
        await new MetadataResponseCache(path, clock: clock).Get(http, url, default);
        check(handler.Requests == 1, "saved successful responses survive a new cache instance");
        clock.Advance(TimeSpan.FromHours(2)); await new MetadataResponseCache(path, clock: clock).Get(http, url, default);
        check(handler.Requests == 2, "expired provider responses fetch again");
        await new MetadataResponseCache(path, fresh: true, clock: clock).Get(http, url, default);
        check(handler.Requests == 3, "explicit refresh bypasses persisted responses");
        await cache.Get(http, url.Replace("en-US", "ru-RU"), default);
        await new MetadataResponseCache(path, variant: "different options", clock: clock).Get(http, url, default);
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "private-token");
        await cache.Get(second, url, default);
        check(handler.Requests == 6, "response keys separate language, provider options and credentials");
        var files = Directory.GetFiles(path, "*.json");
        check(files.All(x => !File.ReadAllText(x).Contains("private-") && !Path.GetFileName(x).Contains("private-")), "disk response cache does not store request credentials");
        foreach (var file in files) await File.WriteAllTextAsync(file, "broken");
        await new MetadataResponseCache(path, clock: clock).Get(http, url, default);
        check(handler.Requests == 7, "corrupt cache entries are replaced through a fresh request");
        var invalid = new MetadataResponseCache(); handler.Body = "not JSON"; var invalidFailed = false;
        try { await invalid.Get(http, url + "&invalid=1", default); } catch (JsonException) { invalidFailed = true; }
        handler.Body = "{\"id\":2}"; await invalid.Get(http, url + "&invalid=1", default);
        check(invalidFailed && handler.Requests == 9, "invalid JSON does not poison subsequent requests");
        handler.Body = "[]"; var before = handler.Requests;
        await invalid.Get(http, url + "&empty=1", default); await invalid.Get(http, url + "&empty=1", default);
        check(handler.Requests == before + 2, "empty searches are not cached as permanent misses");
        before = handler.Requests;
        var aliasCache = new MetadataResponseCache(Path.Combine(directory, "alias-responses"), clock: clock);
        const string aliasesUrl = "https://cache-fixture.invalid/shows/1/akas";
        await aliasCache.Get(http, aliasesUrl, default);
        clock.Advance(TimeSpan.FromDays(6)); await aliasCache.Get(http, aliasesUrl, default);
        check(handler.Requests == before + 1, "empty provider alias lists are reusable for seven days without repeating shortlist requests");
        clock.Advance(TimeSpan.FromDays(2)); await aliasCache.Get(http, aliasesUrl, default);
        check(handler.Requests == before + 2, "provider alias lists expire after seven days");
        handler.Body = "{\"error\":\"temporary\"}"; before = handler.Requests;
        await invalid.Get(http, url + "&error=1", default); await invalid.Get(http, url + "&error=1", default);
        check(handler.Requests == before + 2, "provider error JSON is not cached");
        foreach (var file in Directory.GetFiles(path, "*.json"))
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Expires = clock.GetUtcNow().AddHours(1), Body = (string?)null }));
        handler.Body = "{\"id\":3}"; before = handler.Requests;
        await new MetadataResponseCache(path, clock: clock).Get(http, url, default);
        check(handler.Requests == before + 1, "cache records with a missing body fall back to the provider");
        var blockedDirectory = Path.Combine(directory, "cache-is-a-file"); await File.WriteAllTextAsync(blockedDirectory, "fixture");
        handler.Body = "{\"id\":3}";
        check((await new MetadataResponseCache(blockedDirectory).Get(http, url, default)).Contains("3"), "an unwritable disk cache does not prevent metadata retrieval");

        handler.Body = "{\"id\":3}"; handler.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var first = invalid.Get(http, url + "&waiting=1", default);
        var waiter = invalid.Get(http, url + "&waiting=1", cancelled.Token); cancelled.Cancel();
        var stopped = false; try { await waiter; } catch (OperationCanceledException) { stopped = true; }
        handler.Block.SetResult(); await first;
        check(stopped && !first.IsCanceled, "cancelling a duplicate waiter leaves the original request usable");
        handler.Block = new(TaskCreationOptions.RunContinuationsAsynchronously); using var ownerCancel = new CancellationTokenSource();
        var owner = invalid.Get(http, url + "&owner=1", ownerCancel.Token); ownerCancel.Cancel();
        try { await owner; } catch (OperationCanceledException) { }
        handler.Block = null;
        check((await invalid.Get(http, url + "&owner=1", default)).Contains("3"), "a cancelled request does not leave a poisoned in-flight entry");

        using var statusHandler = new RetryHandler(); using var statusHttp = new HttpClient(statusHandler);
        await invalid.Get(statusHttp, "https://cache-fixture.invalid/retry", default);
        check(statusHandler.Requests == 2, "429 responses retry before a successful response is cached");
        statusHandler.LongDelay = true; using var delayCancel = new CancellationTokenSource();
        var delayed = MetadataHttp.GetBytes(statusHttp, "https://cache-fixture.invalid/long", delayCancel.Token);
        await statusHandler.Entered.Task; delayCancel.Cancel(); stopped = false;
        try { await delayed; } catch (OperationCanceledException) { stopped = true; }
        check(stopped && statusHandler.Requests == 3, "long Retry-After waits remain cancellable without issuing an early retry");
    }
    private static async Task Pacing(Action<bool, string> check)
    {
        var gate = new MetadataRequestGate(0, 2);
        using var first = await gate.Enter(default); using var second = await gate.Enter(default);
        var third = gate.Enter(default);
        check(!third.IsCompleted, "provider pacing bounds concurrent requests");
        first.Dispose(); using var acquired = await third.WaitAsync(TimeSpan.FromSeconds(2));
        var spaced = new MetadataRequestGate(1000); using (await spaced.Enter(default)) { }
        using var ct = new CancellationTokenSource(); var waiting = spaced.Enter(ct.Token);
        check(!waiting.IsCompleted, "provider pacing spaces starts even when prior requests finished");
        ct.Cancel(); var stopped = false; try { using var lease = await waiting; } catch (OperationCanceledException) { stopped = true; }
        var otherProvider = new MetadataRequestGate(0); using var other = await otherProvider.Enter(default);
        check(stopped && other is not null, "provider waits are cancellable and independent of other providers");
        var cooldown = new MetadataRequestGate(0); cooldown.Backoff(TimeSpan.FromSeconds(30));
        using var backoffCancel = new CancellationTokenSource(); var blocked = cooldown.Enter(backoffCancel.Token);
        check(!blocked.IsCompleted, "rate-limit backoff applies to subsequent requests from the same provider");
        backoffCancel.Cancel(); try { using var lease = await blocked; } catch (OperationCanceledException) { }
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
    private sealed class JsonHandler : HttpMessageHandler
    {
        public int Requests; public string Body = "{\"id\":1}";
        public TaskCompletionSource? Block;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            if (Block is { } block) await block.Task.WaitAsync(ct); else await Task.Delay(15, ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class RetryHandler : HttpMessageHandler
    {
        public int Requests; public bool LongDelay; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var response = new HttpResponseMessage(Requests == 1 || LongDelay ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent("{\"id\":1}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(LongDelay ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
            if (LongDelay) Entered.TrySetResult();
            return Task.FromResult(response);
        }
    }
    private sealed class AliasProvider : HttpMessageHandler
    {
        public ConcurrentBag<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!; Paths.Add(uri.AbsolutePath); var path = uri.AbsolutePath; string json;
            if (path.EndsWith("/alternative_titles")) json = path.Contains("/tv/") ? "{\"results\":[{\"title\":\"Local Harbor\"}]}" : "{\"titles\":[{\"title\":\"Local Harbor\"}]}";
            else if (path.EndsWith("/akas")) json = "[{\"name\":\"Local Harbor\"}]";
            else if (path.Contains("/search/"))
            {
                var query = Uri.UnescapeDataString(uri.Query);
                var id = query.Contains("Exact Filename") ? 5 : query.Contains("Harbr") ? 4 : path.Contains("movie") ? 2 : uri.Host == "api.tvmaze.com" ? 3 : 1;
                var title = id == 5 ? "Exact Filename" : id == 4 ? "Harbor" : "Le Port";
                json = uri.Host == "api.tvmaze.com" ? "[{\"show\":" + Data(id, title) + "}]" : "{\"results\":[" + Data(id, title) + "]}";
            }
            else { var id = int.Parse(path.Split('/').Last()); json = Data(id, id == 5 ? "Exact Filename" : id == 4 ? "Harbor" : "Le Port"); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
        private static string Data(int id, string title) => JsonSerializer.Serialize(new { id, name = title, title, original_name = title, original_title = title,
            first_air_date = "2020-01-01", release_date = "2020-01-01", premiered = "2020-01-01", overview = "Plot", summary = "Plot", type = "Scripted", url = "https://www.tvmaze.com/shows/" + id });
    }
}
