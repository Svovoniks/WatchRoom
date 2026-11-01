using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

/// <summary>A verified download from a release: the full installer, an update installer from From, or a file list.</summary>
public sealed record ReleaseFile(Version Release, string Name, Uri DownloadUrl, long Size, string Sha256, Version? From = null);
/// <summary>
/// How to reach Version from Installed: Steps run in order. They are the full installer, one update installer with only
/// the files changed since the installed version, or a chain of update installers through intermediate releases when
/// the installed version is too old for a direct one. Full replaces the steps whenever they cannot be used.
/// </summary>
public sealed record AppUpdate(Version Version, Version Installed, IReadOnlyList<ReleaseFile> Steps, ReleaseFile Full)
{
    public long Size => Steps.Sum(x => x.Size);
    public bool Partial => Steps.Count != 1 || Steps[0] != Full;
    public string Name => Steps[^1].Name;
}
// The installer updates the folder it is given and re-registers the installation there, so only the
// installed copy may update itself. A build or copied folder would otherwise take over the registration.
public static class UpdateLocation
{
    public static string? InstallTarget(string running, string? registered, string defaultDirectory)
    {
        try
        {
            static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var here = Normal(running);
            var installed = Normal(string.IsNullOrWhiteSpace(registered) ? defaultDirectory : registered);
            return string.Equals(here, installed, StringComparison.OrdinalIgnoreCase) ? here : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
/// <summary>
/// Reads releases from repository: one owner/name, or several separated by semicolons when the repository was renamed.
/// The repository was Svovoniks/WatchRoom before it became Svovoniks/BlackPearl. Before the rename only the old name
/// exists, and afterwards GitHub redirects the old name to the new one but lists downloads under the new one, so every
/// name is tried in order and downloads under any of them are accepted.
/// </summary>
public sealed class GitHubUpdates(HttpClient http, string repository)
{
    private readonly string[] repositories = repository.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } names &&
        names.All(x => Regex.IsMatch(x, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) ? names : throw new ArgumentException("Invalid update repository.");
    // The name that answered last; a missing name is not asked again on every request.
    private int found;
    private sealed record Release(string tag_name, bool draft, bool prerelease, Asset[] assets);
    private sealed record Asset(string name, string browser_download_url, long size, string? digest, string state);
    private static string Text(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    // Releases published before the rename carry only the old Watchroom-* names; this version's own releases and every
    // later one carry BlackPearl-* names, so those are all an installed BlackPearl needs.
    public static string SetupName(Version version) => $"BlackPearl-Setup-{Text(version)}-win-x64.exe";
    public static string UpdateName(Version from, Version to) => $"BlackPearl-Update-{Text(from)}-to-{Text(to)}-win-x64.exe";
    public static string FilesName(Version version) => $"BlackPearl-files-{Text(version)}-win-x64.txt";

    public async Task<AppUpdate?> CheckAsync(Version installed, CancellationToken token)
    {
        var release = await GetAsync<Release>("releases/latest", installed, token);
        if (release is null || release.draft || release.prerelease || !Version.TryParse(release.tag_name?.TrimStart('v'), out var version) || version <= installed) return null;
        var full = File(release, version, SetupName(version)) ?? throw new InvalidDataException("The latest release has no Windows installer yet.");
        // An update installer only applies over the exact version it was built from, and it checks that again itself.
        if (Optional(() => File(release, version, UpdateName(installed, version), installed)) is { } direct) return new(version, installed, [direct], full);
        // Releases carry update installers from the last few dozen releases; an older installation chains several.
        var chain = await ChainAsync(installed, version, token);
        return new(version, installed, chain is not null && chain.Sum(x => x.Size) < full.Size / 2 ? chain : [full], full);
    }
    private async Task<T?> GetAsync<T>(string path, Version installed, CancellationToken token) where T : class
    {
        // Every name is the same repository, so a release or tag missing under one name is missing under all of them;
        // asking the others only finds a repository that exists under them.
        for (var i = 0; i < repositories.Length; i++)
        {
            var index = (found + i) % repositories.Length;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repositories[index]}/{path}");
            request.Headers.UserAgent.ParseAdd("BlackPearl/" + Text(installed)); request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            found = index;
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
        }
        return null;
    }
    private static ReleaseFile? Optional(Func<ReleaseFile?> file) { try { return file(); } catch (InvalidDataException) { return null; } }
    /// <summary>The cheapest chain of update installers from installed to target; versions only move forward, so one pass in version order finds it.</summary>
    private async Task<IReadOnlyList<ReleaseFile>?> ChainAsync(Version installed, Version target, CancellationToken token)
    {
        var releases = await GetAsync<Release[]>("releases?per_page=100", installed, token) ?? [];
        var edges = new List<ReleaseFile>();
        foreach (var release in releases)
        {
            if (release is null || release.draft || release.prerelease || !Version.TryParse(release.tag_name?.TrimStart('v'), out var to) || to <= installed || to > target) continue;
            foreach (var asset in release.assets ?? [])
                if (asset?.name is { } name && Regex.Match(name, @"^BlackPearl-Update-(\d+\.\d+\.\d+)-to-") is { Success: true } match &&
                    Version.Parse(match.Groups[1].Value) is var from && from >= installed && from < to && Optional(() => File(release, to, UpdateName(from, to), from)) is { } file)
                    edges.Add(file);
        }
        var best = new Dictionary<Version, (long Cost, ReleaseFile? Step)> { [installed] = (0, null) };
        foreach (var edge in edges.OrderBy(x => x.Release).ThenBy(x => x.From))
            if (best.TryGetValue(edge.From!, out var start) && (!best.TryGetValue(edge.Release, out var known) || start.Cost + edge.Size < known.Cost))
                best[edge.Release] = (start.Cost + edge.Size, edge);
        if (!best.ContainsKey(target)) return null;
        var steps = new List<ReleaseFile>();
        for (var at = target; best[at].Step is { } step; at = step.From!) steps.Insert(0, step);
        return steps;
    }
    private ReleaseFile? File(Release release, Version version, string name, Version? from = null)
    {
        var asset = release.assets?.FirstOrDefault(x => x is not null && x.name == name && x.state == "uploaded");
        if (asset is null) return null;
        if (!Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Host != "github.com" ||
            !repositories.Any(x => url.AbsolutePath.StartsWith($"/{x}/releases/download/", StringComparison.OrdinalIgnoreCase)) || url.Segments.Last() != name ||
            asset.size <= 0 || asset.size > 2L * 1024 * 1024 * 1024 || asset.digest is null || !Regex.IsMatch(asset.digest, @"^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The release installer or its SHA-256 checksum is invalid.");
        return new(version, name, url, asset.size, asset.digest[7..], from);
    }

    /// <summary>
    /// Downloads and verifies the installers to run in order. Update installers rely on the installed files being the
    /// installed release's, so with an installation folder they are checked against that release's file list first.
    /// Anything that fails falls back to the full installer.
    /// </summary>
    public async Task<IReadOnlyList<string>> DownloadAsync(AppUpdate update, string directory, string? installation, IProgress<double>? progress, CancellationToken token)
    {
        if (!update.Partial) return [await DownloadFileAsync(update.Full, directory, Progress(progress, 0, update.Full.Size), token)];
        try
        {
            if (installation is not null && !await MatchesReleaseAsync(update.Installed, installation, directory, token)) throw new InvalidDataException("The installed files differ from their release.");
            var paths = new List<string>(); long done = 0;
            foreach (var step in update.Steps)
            {
                paths.Add(await DownloadFileAsync(step, directory, Progress(progress, done, update.Size), token));
                done += step.Size;
            }
            return paths;
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException)
        {
            progress?.Report(0);
            return [await DownloadFileAsync(update.Full, directory, Progress(progress, 0, update.Full.Size), token)];
        }
    }
    private static IProgress<long>? Progress(IProgress<double>? progress, long before, long total) =>
        progress is null ? null : new SynchronousProgress(bytes => progress.Report((before + bytes) * 100d / total));
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }

    /// <summary>
    /// True when every file the installed release shipped is in the installation with its published hash. Releases from
    /// before file lists were published cannot be checked; their update installers still verify the registered version.
    /// </summary>
    public async Task<bool> MatchesReleaseAsync(Version installed, string installation, string directory, CancellationToken token)
    {
        var release = await GetAsync<Release>($"releases/tags/v{Text(installed)}", installed, token);
        if (release is null || File(release, installed, FilesName(installed)) is not { } list) return true;
        var path = await DownloadFileAsync(list, directory, null, token);
        try
        {
            var lines = await System.IO.File.ReadAllLinesAsync(path, token);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation)) + Path.DirectorySeparatorChar;
            return await Task.Run(() => lines.Where(x => x.Length > 0).All(line =>
            {
                token.ThrowIfCancellationRequested();
                var parts = line.Split('\t', 3);
                if (parts.Length != 3 || !long.TryParse(parts[1], out var size)) throw new InvalidDataException("The release file list is malformed.");
                var file = Path.GetFullPath(Path.Combine(root, parts[2]));
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The release file list is malformed.");
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return stream.Length == size && Convert.ToHexString(SHA256.HashData(stream)).Equals(parts[0], StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { return false; }
            }), token);
        }
        finally { System.IO.File.Delete(path); }
    }
    private async Task<string> DownloadFileAsync(ReleaseFile file, string directory, IProgress<long>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, file.Name);
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long length = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    length += count; if (length > file.Size) throw new InvalidDataException("Installer size does not match the release.");
                    hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token); progress?.Report(length);
                }
                if (length != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer verification failed. Nothing was installed.");
            }
            System.IO.File.Move(partial, destination, true); return destination;
        }
        finally { if (System.IO.File.Exists(partial)) System.IO.File.Delete(partial); }
    }
}
