using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watchroom.Core;

var bytes = Encoding.UTF8.GetBytes("fixture installer bytes");
var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var directory = Path.Combine(Path.GetTempPath(), "watchroom-update-checks", Guid.NewGuid().ToString("N"));
var handler = new Fixture(); using var http = new HttpClient(handler);
var updates = new GitHubUpdates(http, "Svovoniks/WatchRoom"); var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
string Release(string version = "0.3.0", string? url = null, string? checksum = null, bool prerelease = false) => JsonSerializer.Serialize(new {
    tag_name = "v" + version, draft = false, prerelease,
    assets = new[] { new { name = $"Watchroom-Setup-{version}-win-x64.exe", browser_download_url = url ?? $"https://github.com/Svovoniks/WatchRoom/releases/download/v{version}/Watchroom-Setup-{version}-win-x64.exe", size = bytes.Length, digest = checksum ?? digest, state = "uploaded" } }
});
async Task Reject(Func<Task> action, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (InvalidDataException) { Check(true, name); } }
try
{
    handler.Json = Release(); handler.Bytes = bytes;
    var update = await updates.CheckAsync(new(0, 2, 0), default);
    Check(update?.Version == new Version(0, 3, 0), "newer stable release is offered");
    Check(await updates.CheckAsync(new(0, 3, 0), default) is null, "same version is not offered");
    Check(await updates.CheckAsync(new(0, 4, 0), default) is null, "older version cannot downgrade the app");
    handler.Json = Release(prerelease: true); Check(await updates.CheckAsync(new(0, 2, 0), default) is null, "prerelease is ignored");
    handler.Json = Release(url: "https://evil.test/installer.exe"); await Reject(() => updates.CheckAsync(new(0, 2, 0), default), "installer from another host is rejected");
    handler.Json = Release(checksum: "invalid"); await Reject(() => updates.CheckAsync(new(0, 2, 0), default), "missing trustworthy checksum is rejected");
    handler.Json = Release();
    var file = await updates.DownloadAsync(update!, directory, null, default);
    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "verified download becomes an installer");
    handler.Bytes = Encoding.UTF8.GetBytes("tampered payload bytes!");
    await Reject(() => updates.DownloadAsync(update!, directory, null, default), "tampered download is rejected");
    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "failed download does not replace verified installer");
    handler.Bytes = bytes[..3]; await Reject(() => updates.DownloadAsync(update!, directory, null, default), "truncated download is rejected");
    Check(!Directory.EnumerateFiles(directory, "*.part").Any(), "failed downloads leave no partial installers");
    handler.Status = HttpStatusCode.NotFound; Check(await updates.CheckAsync(new(0, 2, 0), default) is null, "no published release is handled normally");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await updates.DownloadAsync(update!, directory, null, cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "shutdown cancels downloads"); }
    Console.WriteLine($"{checks} updater checks passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
sealed class Fixture : HttpMessageHandler
{
    public string Json = "{}"; public byte[] Bytes = []; public HttpStatusCode Status = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(Status) { Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(Json, Encoding.UTF8, "application/json") : new ByteArrayContent(Bytes) });
    }
}
