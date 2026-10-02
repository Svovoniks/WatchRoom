using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

public sealed record AppUpdate(Version Version, string Name, Uri DownloadUrl, long Size, string Sha256);
public sealed class GitHubUpdates(HttpClient http, string repository)
{
    private string Repository => Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ? repository : throw new ArgumentException("Invalid update repository.");
    private sealed record Release(string tag_name, bool draft, bool prerelease, Asset[] assets);
    private sealed record Asset(string name, string browser_download_url, long size, string? digest, string state);
    public async Task<AppUpdate?> CheckAsync(Version installed, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Watchroom/" + installed); request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: token);
        if (release is null || release.draft || release.prerelease || !Version.TryParse(release.tag_name.TrimStart('v'), out var version) || version <= installed) return null;
        var name = $"Watchroom-Setup-{version}-win-x64.exe";
        var asset = release.assets.FirstOrDefault(x => x.name == name && x.state == "uploaded");
        if (asset is null) throw new InvalidDataException("The latest release has no Windows installer yet.");
        if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com" ||
            !url.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase) || url.Segments.Last() != name ||
            asset.size <= 0 || asset.size > 2L * 1024 * 1024 * 1024 || asset.digest is null || !Regex.IsMatch(asset.digest, @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The release installer or its SHA-256 checksum is invalid.");
        return new(version, name, url, asset.size, asset.digest[7..]);
    }
    public async Task<string> DownloadAsync(AppUpdate update, string directory, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, update.Name);
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += count; if (length > update.Size) throw new InvalidDataException("Installer size does not match the release.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token); progress?.Report(length * 100d / update.Size);
                }
                if (length != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer verification failed. Nothing was installed.");
            }
            File.Move(partial, destination, true); return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
