using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Watchroom.Core;

static class MetadataDiagnosticsFixture
{
    public static async Task Run(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        PlaybackDiagnostics.Initialize(Path.Combine(root, "logs"));
        check(MetadataDiagnostics.Request("https://user:secret@example.com:8443/search?api_key=secret#token") == "https://example.com:8443/search",
            "metadata diagnostics omit URL credentials, query values and fragments");
        var error = Wire.Serialize(MetadataDiagnostics.Error(new IOException("Image failed: HTTPS://example.com/image?api_key=secret",
            new HttpRequestException("Denied https://user:secret@example.com/search?token=secret", null, HttpStatusCode.Forbidden))));
        check(!error.Contains("secret") && error.Contains("403") && error.Contains("Image failed"),
            "metadata diagnostics preserve nested failure details and HTTP status while redacting URL secrets");
        using (MetadataDiagnostics.BeginItem("fixture-request", new("diagnostic-fixture", "fixture.mkv", "Diagnostic Fixture", "Show", Season: 1, Episode: 2)))
        using (var http = new HttpClient(new RetryThenFail()))
        {
            try { await MetadataHttp.GetBytes(http, "https://example.com/retry?api_key=secret", default); throw new Exception("Expected provider failure"); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { }
        }
        MetadataDiagnostics.Record("metadata-context-cleared", new { });
        var store = new LibraryStore(Path.Combine(root, "library"));
        new MetadataOptions(Providers: ["tvmaze"]).Save(store);
        store.Save(new("failure", "fixture.mkv", "Failure Fixture", "Show", Series: "Failure Fixture", Season: 1, Episode: 1,
            MetadataProvider: "tvmaze", MetadataId: 1, ProviderIds: new() { ["tvmaze"] = "1" }, Matched: true));
        using (var http = new HttpClient(new Response(HttpStatusCode.Forbidden, "denied")))
            await AutomaticArtwork.FetchAsync(store, root, null, null, default, http, force: true);
        using (var http = new HttpClient(new Response(HttpStatusCode.OK, "not an image")))
        {
            try { await MetadataHttp.GetImage(http, "https://example.com/image?token=secret", default); throw new Exception("Expected image failure"); }
            catch (IOException) { }
        }
        var blocked = Path.Combine(root, "blocked-cache"); await File.WriteAllTextAsync(blocked, "fixture");
        using (var http = new HttpClient(new Response(HttpStatusCode.OK, "{\"id\":1}")))
            await new MetadataResponseCache(blocked).Get(http, "https://example.com/cache?token=secret", default);
        await PlaybackDiagnostics.FlushAsync();
        using var stream = new FileStream(Path.Combine(PlaybackDiagnostics.DirectoryPath!, "diagnostics.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();
        var events = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var retry = events.Select(e => e.RootElement).First(e => e.GetProperty("kind").GetString() == "metadata-http-response" &&
                e.GetProperty("data").GetProperty("data").GetProperty("request").GetString() == "https://example.com/retry");
            check(retry.GetProperty("data").GetProperty("context").GetProperty("title").GetString() == "Diagnostic Fixture" &&
                retry.GetProperty("data").GetProperty("data").GetProperty("willRetry").GetBoolean(), "HTTP retry diagnostics identify the affected title");
            check(events.Any(e => e.RootElement.GetProperty("kind").GetString() == "metadata-context-cleared" &&
                e.RootElement.GetProperty("data").GetProperty("context").ValueKind == JsonValueKind.Null), "metadata request context is restored after a title finishes");
            check(new[] { "metadata-http-failure", "metadata-image-failure", "metadata-cache-failure" }.All(kind => text.Contains("\"kind\":\"" + kind + "\"")) &&
                text.Contains("season-episode") && text.Contains("provider-match") && !text.Contains("secret"),
                "HTTP, image, cache and swallowed provider/episode failures are recorded without URL secrets");
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "metadata-fetch-report.json")));
            check(report.RootElement.GetProperty("issues").EnumerateArray().Any(i => i.TryGetProperty("error", out var detail) && detail.GetProperty("status").GetInt32() == 403),
                "metadata fetch report retains the exact exception and status for failed titles");
        }
        finally { foreach (var entry in events) entry.Dispose(); }
    }
    private sealed class RetryThenFail : HttpMessageHandler
    {
        private int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(++requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
    private sealed class Response(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
