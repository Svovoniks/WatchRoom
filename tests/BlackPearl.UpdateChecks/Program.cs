using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlackPearl.Core;

var bytes = Encoding.UTF8.GetBytes("fixture installer bytes");
var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
var directory = Path.Combine(Path.GetTempPath(), "blackpearl-update-checks", Guid.NewGuid().ToString("N"));
var handler = new Fixture(); using var http = new HttpClient(handler);
var updates = new GitHubUpdates(http, "Svovoniks/BlackPearl;Svovoniks/WatchRoom"); var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
object Asset(string name, string version, byte[] content, string? url = null, string? checksum = null) => new {
    name, browser_download_url = url ?? $"https://github.com/Svovoniks/BlackPearl/releases/download/v{version}/{name}", size = content.Length,
    digest = checksum ?? "sha256:" + Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), state = "uploaded" };
object ReleaseObject(string version = "0.3.0", string? url = null, string? checksum = null, bool prerelease = false, params object[] more) => new {
    tag_name = "v" + version, draft = false, prerelease,
    assets = new[] { Asset($"BlackPearl-Setup-{version}-win-x64.exe", version, bytes, url, checksum) }.Concat(more).ToArray()
};
string Release(string version = "0.3.0", string? url = null, string? checksum = null, bool prerelease = false, params object[] more) =>
    JsonSerializer.Serialize(ReleaseObject(version, url, checksum, prerelease, more));
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
    handler.Json = JsonSerializer.Serialize(new { tag_name = "v0.3.0", draft = false, prerelease = false, assets = (object?)null });
    await Reject(() => updates.CheckAsync(new(0, 2, 0), default), "release without an asset list reports a missing installer");
    handler.Json = JsonSerializer.Serialize(new { tag_name = (string?)null, draft = false, prerelease = false, assets = Array.Empty<object>() });
    Check(await updates.CheckAsync(new(0, 2, 0), default) is null, "release without a tag is ignored");
    handler.Json = Release();
    Check(update is { Partial: false, Steps.Count: 1 } && update.Steps[0] == update.Full, "without update installers the full installer is the only step");
    var file = (await updates.DownloadAsync(update!, directory, null, null, default)).Single();
    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "verified download becomes an installer");
    handler.Bytes = Encoding.UTF8.GetBytes("tampered payload bytes!");
    await Reject(() => updates.DownloadAsync(update!, directory, null, null, default), "tampered download is rejected");
    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "failed download does not replace verified installer");
    handler.Bytes = bytes[..3]; await Reject(() => updates.DownloadAsync(update!, directory, null, null, default), "truncated download is rejected");
    Check(!Directory.EnumerateFiles(directory, "*.part").Any(), "failed downloads leave no partial installers");
    handler.Status = HttpStatusCode.NotFound; Check(await updates.CheckAsync(new(0, 2, 0), default) is null, "no published release is handled normally");
    handler.Status = HttpStatusCode.OK;
    // The repository was Svovoniks/WatchRoom before it became Svovoniks/BlackPearl.
    handler.Missing = "Svovoniks/BlackPearl"; handler.Asked.Clear();
    Check((await updates.CheckAsync(new(0, 2, 0), default))?.Version == new Version(0, 3, 0) && handler.Asked.Contains("Svovoniks/WatchRoom"),
        "before the repository rename, releases are read under its former name");
    handler.Json = Release(url: "https://github.com/Svovoniks/WatchRoom/releases/download/v0.3.0/BlackPearl-Setup-0.3.0-win-x64.exe");
    Check((await updates.CheckAsync(new(0, 2, 0), default))?.Full.DownloadUrl.AbsolutePath.StartsWith("/Svovoniks/WatchRoom/") == true, "downloads under the former repository name are accepted");
    handler.Missing = null;
    handler.Json = Release(url: "https://github.com/Svovoniks/Other/releases/download/v0.3.0/BlackPearl-Setup-0.3.0-win-x64.exe");
    await Reject(() => updates.CheckAsync(new(0, 2, 0), default), "downloads from another repository are rejected");
    handler.Json = Release();
    try { _ = new GitHubUpdates(http, "Svovoniks/BlackPearl;not a repository"); throw new Exception("Invalid repository list accepted"); }
    catch (ArgumentException) { Check(true, "an invalid repository name in the list is rejected"); }
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { await updates.DownloadAsync(update!, directory, null, null, cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "shutdown cancels downloads"); }
    handler.Bytes = bytes; handler.Status = HttpStatusCode.OK;
    var small = Encoding.UTF8.GetBytes("fixture update installer");
    handler.Files["BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe"] = small;
    handler.Json = Release(more: Asset("BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe", "0.3.0", small));
    var partial = await updates.CheckAsync(new(0, 2, 0), default);
    Check(partial?.Name == "BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe" && partial.Size == small.Length && partial.Full.Name == "BlackPearl-Setup-0.3.0-win-x64.exe",
        "an update installer built from the installed version is preferred, with the full installer behind it");
    Check((await updates.CheckAsync(new(0, 1, 0), default))?.Name == "BlackPearl-Setup-0.3.0-win-x64.exe", "other installed versions download the full installer");
    Check(File.ReadAllBytes((await updates.DownloadAsync(partial!, directory, null, null, default)).Single()).SequenceEqual(small), "the update installer downloads and verifies");

    // The installed files are checked against the installed release's published file list before an update installer.
    var installation = Path.Combine(directory, "installation"); Directory.CreateDirectory(Path.Combine(installation, "Models"));
    var model = Encoding.UTF8.GetBytes("model"); File.WriteAllBytes(Path.Combine(installation, "Models", "model.bin"), model);
    var list = Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(model))}\t{model.Length}\tModels\\model.bin\n");
    handler.Files["BlackPearl-files-0.2.0-win-x64.txt"] = list;
    handler.Tags["v0.2.0"] = JsonSerializer.Serialize(ReleaseObject("0.2.0", more: Asset("BlackPearl-files-0.2.0-win-x64.txt", "0.2.0", list)));
    Check(File.ReadAllBytes((await updates.DownloadAsync(partial!, directory, installation, null, default)).Single()).SequenceEqual(small), "an installation matching its release takes the update installer");
    File.WriteAllText(Path.Combine(installation, "Models", "model.bin"), "edited");
    Check(File.ReadAllBytes((await updates.DownloadAsync(partial!, directory, installation, null, default)).Single()).SequenceEqual(bytes), "a changed installation gets the full installer");
    handler.Tags.Clear();
    Check(File.ReadAllBytes((await updates.DownloadAsync(partial!, directory, installation, null, default)).Single()).SequenceEqual(small), "a release without a file list relies on the installer's own version check");
    handler.Files["BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe"] = Encoding.UTF8.GetBytes("tampered update installer");
    Check(File.ReadAllBytes((await updates.DownloadAsync(partial!, directory, null, null, default)).Single()).SequenceEqual(bytes), "a corrupted update installer falls back to the full installer");

    // Further behind than the update installers reach, a chain through intermediate releases is used when it is small.
    var hop1 = Encoding.UTF8.GetBytes("hop 1"); var hop2 = Encoding.UTF8.GetBytes("hop 2"); var direct = new byte[8];
    handler.Files["BlackPearl-Update-0.1.0-to-0.2.0-win-x64.exe"] = hop1; handler.Files["BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe"] = hop2;
    var latest = ReleaseObject(more: Asset("BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe", "0.3.0", hop2));
    handler.Json = JsonSerializer.Serialize(latest);
    handler.List = JsonSerializer.Serialize(new[] { latest, ReleaseObject("0.2.0", more: Asset("BlackPearl-Update-0.1.0-to-0.2.0-win-x64.exe", "0.2.0", hop1)),
        ReleaseObject("0.1.5", more: Asset("BlackPearl-Update-0.1.0-to-0.1.5-win-x64.exe", "0.1.5", direct)) });
    var chain = await updates.CheckAsync(new(0, 1, 0), default);
    Check(chain is { Partial: true } && string.Join(",", chain.Steps.Select(x => x.Name)) == "BlackPearl-Update-0.1.0-to-0.2.0-win-x64.exe,BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe",
        "an older installation chains update installers through intermediate releases");
    var paths = await updates.DownloadAsync(chain!, directory, null, null, default);
    Check(paths.Count == 2 && File.ReadAllBytes(paths[0]).SequenceEqual(hop1) && File.ReadAllBytes(paths[1]).SequenceEqual(hop2), "every step of a chain downloads and verifies in order");
    handler.Files["BlackPearl-Update-0.1.0-to-0.2.0-win-x64.exe"] = Encoding.UTF8.GetBytes("hop X");
    Check((await updates.DownloadAsync(chain!, directory, null, null, default)).Single().EndsWith("BlackPearl-Setup-0.3.0-win-x64.exe"), "a failed step replaces the whole chain with the full installer");
    Check((await updates.CheckAsync(new(0, 0, 9), default))?.Partial == false, "an installation no chain reaches gets the full installer");
    var large = new byte[bytes.Length];
    handler.List = JsonSerializer.Serialize(new[] { ReleaseObject(more: Asset("BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe", "0.3.0", large)),
        ReleaseObject("0.2.0", more: Asset("BlackPearl-Update-0.1.0-to-0.2.0-win-x64.exe", "0.2.0", hop1)) });
    handler.Json = JsonSerializer.Serialize(ReleaseObject(more: Asset("BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe", "0.3.0", large)));
    Check((await updates.CheckAsync(new(0, 1, 0), default))?.Partial == false, "a chain larger than half the full installer is not worth it");
    handler.List = "[]";
    handler.Json = Release(more: Asset("BlackPearl-Update-0.2.0-to-0.3.0-win-x64.exe", "0.3.0", small, url: "https://evil.test/update.exe"));
    Check((await updates.CheckAsync(new(0, 2, 0), default))?.Name == "BlackPearl-Setup-0.3.0-win-x64.exe", "an update installer from another host is ignored for the full installer");
    var installed = Path.Combine(directory, "Programs", "BlackPearl"); var build = Path.Combine(directory, "src", "bin", "Release");
    Check(UpdateLocation.InstallTarget(installed + Path.DirectorySeparatorChar, installed.ToUpperInvariant(), build) == installed, "the registered installation updates itself in place");
    Check(UpdateLocation.InstallTarget(build, installed, installed) is null, "a build folder never installs updates over itself");
    Check(UpdateLocation.InstallTarget(installed, null, installed) == installed && UpdateLocation.InstallTarget(build, " ", installed) is null,
        "without a registration only the default install folder updates");
    Check(UpdateLocation.InstallTarget(installed, "bad\0path", installed) is null, "an unreadable registration disables self-updates");
    Console.WriteLine($"{checks} updater checks passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
sealed class Fixture : HttpMessageHandler
{
    public string Json = "{}", List = "[]"; public byte[] Bytes = []; public HttpStatusCode Status = HttpStatusCode.OK;
    public string? Missing; public readonly HashSet<string> Asked = [];
    public readonly Dictionary<string, byte[]> Files = [];
    public readonly Dictionary<string, string> Tags = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        if (uri.Host == "api.github.com" && uri.AbsolutePath.Split('/') is [_, "repos", var owner, var name, ..])
        {
            Asked.Add(owner + "/" + name);
            if (Missing == owner + "/" + name) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        if (uri.Host != "api.github.com") return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Files.GetValueOrDefault(uri.Segments.Last(), Bytes)) });
        var json = uri.AbsolutePath.EndsWith("/releases") ? List : uri.AbsolutePath.Contains("/releases/tags/") ? Tags.GetValueOrDefault(uri.Segments.Last()) : Json;
        return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(Status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
