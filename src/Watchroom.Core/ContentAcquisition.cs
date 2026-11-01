using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Watchroom.Core;

// A program that fetches missing or upcoming videos. Each plugin is a folder in the plugins folder holding plugin.json;
// Watchroom runs its command with a request on standard input saying what to get and where it belongs, and the files
// join the library on the next scan. See PLUGINS.md.
public record AcquisitionPlugin(string Id, string Name, string Description, string[] ContentTypes, string Command, string[] Arguments,
    string Directory, int TimeoutSeconds)
{
    public bool Supports(string contentType) => ContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);
}
public record AcquisitionManifest(string? Id = null, string? Name = null, string? Description = null, string[]? ContentTypes = null,
    string? Command = null, string[]? Arguments = null, int? TimeoutSeconds = null);
// A library folder and what it holds: anime, show, movie or mixed.
public record AcquisitionLibrary(string Path, string Kind);
public record AcquisitionEpisode(int Season, int Episode, string? Title, string? AirDate, bool Upcoming, int? AbsoluteEpisode = null);
// Scope is episode, season or movie. Name is the video to get, such as "Frieren S01E05", "Frieren S01" or "Dune (2021)".
// TargetDirectory is where the files belong: the season's folder for an episode, the show's folder for a season and a
// library folder for a film. It may not exist yet when nothing of the show or season is on disk.
public record AcquisitionRequest(int Version, string Scope, string ContentType, string Name, string Title, string? OriginalTitle, int? Year,
    string TargetDirectory, bool TargetExists, string? ShowDirectory, string? SeasonDirectory, int? Season, AcquisitionEpisode[] Episodes,
    Dictionary<string, string> ProviderIds, AcquisitionLibrary[] Libraries);
public record AcquisitionResult(bool Success, string Message);
// What a plugin may print as its last line of output.
public record AcquisitionResponse(bool? Success = null, string? Status = null, string? Message = null);

public static class ContentAcquisition
{
    public const string ManifestName = "plugin.json";
    public const int RequestVersion = 1;
    public static readonly string[] ContentTypes = ["anime", "show", "movie"];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string ContentType(string kind) => kind switch { "Anime" => "anime", "Movie" => "movie", _ => "show" };
    public static string LibraryKind(string kind) => kind is "Anime" or "Movie" or "Show" ? kind.ToLowerInvariant() : "mixed";
    public static string ContentTypeName(string type) => type switch { "anime" => "Anime", "movie" => "Movies", _ => "Shows" };
    private static string? NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "anime" => "anime", "show" or "shows" or "tv" or "series" => "show", "movie" or "movies" or "film" or "films" => "movie", _ => null
    };

    // Every plugin folder with a readable manifest, and a line for each one that couldn't be used.
    public static (List<AcquisitionPlugin> Plugins, List<string> Problems) Discover(string directory)
    {
        var plugins = new List<AcquisitionPlugin>(); var problems = new List<string>();
        if (!System.IO.Directory.Exists(directory)) return (plugins, problems);
        string[] folders;
        try { folders = System.IO.Directory.GetDirectories(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{directory}: {ex.Message}"); return (plugins, problems); }
        foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path)) continue;
            var name = Path.GetFileName(folder);
            try
            {
                var manifest = Wire.Read<AcquisitionManifest>(File.ReadAllText(path));
                var types = (manifest.ContentTypes ?? []).Select(NormalizeType).ToArray();
                if (string.IsNullOrWhiteSpace(manifest.Command)) { problems.Add($"{name}: plugin.json has no command."); continue; }
                if (types.Length == 0) { problems.Add($"{name}: plugin.json lists no content types (anime, show, movie)."); continue; }
                if (types.Any(x => x is null)) { problems.Add($"{name}: unknown content type in {string.Join(", ", manifest.ContentTypes!)}; use anime, show or movie."); continue; }
                var id = string.IsNullOrWhiteSpace(manifest.Id) ? name : manifest.Id.Trim();
                if (plugins.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) { problems.Add($"{name}: another plugin already has the id {id}."); continue; }
                plugins.Add(new(id, string.IsNullOrWhiteSpace(manifest.Name) ? name : manifest.Name.Trim(), manifest.Description?.Trim() ?? "",
                    types.OfType<string>().Distinct().ToArray(), manifest.Command.Trim(), manifest.Arguments ?? [], folder, Math.Clamp(manifest.TimeoutSeconds ?? 120, 5, 3600)));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            { problems.Add($"{name}: plugin.json can't be read: {ex.Message}"); }
        }
        return (plugins, problems);
    }

    // Music folders never receive videos.
    public static AcquisitionLibrary[] Libraries(IEnumerable<LibraryFolder> folders) => folders.Where(x => x.Kind != MusicFiles.Kind).Select(x => new AcquisitionLibrary(x.Path, LibraryKind(x.Kind))).ToArray();
    private static bool Upcoming(string? date) => DateOnly.TryParse(date, out var day) && day > DateOnly.FromDateTime(DateTime.UtcNow);
    private static AcquisitionEpisode Episode(MediaItem item) => new(item.Season ?? 0, item.Episode ?? 0, item.EpisodeTitle, item.AirDate, Upcoming(item.AirDate), item.AbsoluteEpisode);
    // The folder most of the paths share.
    private static string? Common(IEnumerable<string?> paths) => paths.OfType<string>().GroupBy(x => x.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), PathComparer)
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).FirstOrDefault()?.Key;
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } result ? result : "Untitled";
    }
    // The library folder a title of this kind belongs in: one of its own kind, then a mixed one, then films beside films.
    private static string? Root(IReadOnlyList<LibraryFolder> folders, string kind, bool movie)
    {
        var order = kind == "Anime" ? movie ? new[] { "Anime", "Movie", "Mixed" } : ["Anime", "Show", "Mixed"] : movie ? ["Movie", "Mixed"] : ["Show", "Mixed", "Anime"];
        return order.Select(k => folders.FirstOrDefault(f => f.Kind == k)?.Path).FirstOrDefault(x => x is not null) ?? folders.FirstOrDefault(f => f.Kind != MusicFiles.Kind)?.Path;
    }
    private static Dictionary<string, string> Ids(MediaItem item)
    {
        var ids = new Dictionary<string, string>(item.ProviderIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (item.MetadataProvider is { } provider && item.MetadataId is > 0) ids.TryAdd(provider, item.MetadataId.Value.ToString());
        return ids;
    }
    private static string? ExistingDirectory(string? path) => path is not null && System.IO.Directory.Exists(path) ? path : null;

    // Where a show's files are, and where one of its seasons is or would go. A show with no files yet gets a folder
    // named after it in the library folder for its kind; a season with no files gets a Season NN folder unless the
    // show keeps every episode in one folder.
    public static (string? Show, string? Season) Folders(MediaItem member, int? season, IEnumerable<MediaItem> library, IReadOnlyList<LibraryFolder> folders)
    {
        var files = library.Where(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus && LibraryIdentity.SameShow(x, member)).ToArray();
        var show = Common(files.Select(x => x.SeriesPath)) ?? Common(files.Select(x => Path.GetDirectoryName(x.Path)));
        if (show is null && Root(folders, member.Kind, false) is { } root)
            show = Path.Combine(root, Safe((member.Series ?? member.Title) + (member.Year is { } year ? $" ({year})" : "")));
        if (season is null || show is null) return (show, null);
        var seasonFolder = Common(files.Where(x => x.Season == season).Select(x => Path.GetDirectoryName(x.Path)));
        if (seasonFolder is not null) return (show, seasonFolder);
        var flat = files.Length > 0 && files.All(x => PathComparer.Equals(Path.GetDirectoryName(x.Path)?.TrimEnd(Path.DirectorySeparatorChar), show.TrimEnd(Path.DirectorySeparatorChar)));
        return (show, flat ? show : Path.Combine(show, season == 0 ? "Specials" : $"Season {season:00}"));
    }

    // One missing or upcoming episode; it belongs in its season's folder. Null when there's no library folder to put it in.
    public static AcquisitionRequest? ForEpisode(MediaItem episode, IEnumerable<MediaItem> library, IReadOnlyList<LibraryFolder> folders)
    {
        if (episode.Series is null || episode.Season is not { } season || episode.Episode is not { } number) return null;
        var (show, seasonFolder) = Folders(episode, season, library, folders);
        if (seasonFolder is null) return null;
        return new(RequestVersion, "episode", ContentType(episode.Kind), $"{Safe(episode.Series)} S{season:00}E{number:00}", episode.Series, episode.OriginalTitle, episode.Year,
            seasonFolder, System.IO.Directory.Exists(seasonFolder), show, seasonFolder, season, [Episode(episode)], Ids(episode), Libraries(folders));
    }
    // The missing and upcoming episodes of a season; the season belongs in the show's folder. Null when nothing is missing.
    public static AcquisitionRequest? ForSeason(MediaItem member, int season, IEnumerable<MediaItem> library, IReadOnlyList<LibraryFolder> folders)
    {
        if (member.Series is null) return null;
        var all = library as IReadOnlyCollection<MediaItem> ?? library.ToArray();
        var missing = all.Where(x => x.IsVirtual && x.Season == season && x.Episode is not null && LibraryIdentity.SameShow(x, member)).OrderBy(x => x.Episode).ToArray();
        if (missing.Length == 0) return null;
        var (show, seasonFolder) = Folders(member, season, all, folders);
        if (show is null) return null;
        var first = missing[0];
        return new(RequestVersion, "season", ContentType(member.Kind), $"{Safe(member.Series)} S{season:00}", member.Series, first.OriginalTitle ?? member.OriginalTitle, first.Year ?? member.Year,
            show, System.IO.Directory.Exists(show), show, ExistingDirectory(seasonFolder), season, missing.Select(Episode).ToArray(), Ids(first), Libraries(folders));
    }
    // A film added before its file; it belongs in the library folder for films of its kind.
    public static AcquisitionRequest? ForMovie(MediaItem film, IReadOnlyList<LibraryFolder> folders)
    {
        if (film.Series is not null || Root(folders, film.Kind, true) is not { } root) return null;
        return new(RequestVersion, "movie", ContentType(film.Kind), Safe(film.Title) + (film.Year is { } year ? $" ({year})" : ""), film.Title, film.OriginalTitle, film.Year,
            root, System.IO.Directory.Exists(root), null, null, null, [], Ids(film), Libraries(folders));
    }

    // Runs the plugin with the request on standard input, also saved to the file named by WATCHROOM_REQUEST_FILE.
    // Success is exit code 0 unless the last line printed is JSON with success or status; its message is shown to the user.
    public static async Task<AcquisitionResult> RunAsync(AcquisitionPlugin plugin, AcquisitionRequest request, CancellationToken ct)
    {
        var json = Wire.Serialize(request);
        var requestFile = Path.Combine(Path.GetTempPath(), $"watchroom-acquire-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestFile, json, ct);
        try
        {
            var command = Path.IsPathRooted(plugin.Command) ? plugin.Command : File.Exists(Path.Combine(plugin.Directory, plugin.Command)) ? Path.GetFullPath(Path.Combine(plugin.Directory, plugin.Command)) : plugin.Command;
            var start = new ProcessStartInfo
            {
                WorkingDirectory = plugin.Directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false)
            };
            // PowerShell scripts run through Windows PowerShell, so a plugin can be a single script.
            if (command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = "powershell.exe";
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", command }) start.ArgumentList.Add(arg);
            }
            else start.FileName = command;
            foreach (var arg in plugin.Arguments) start.ArgumentList.Add(arg.Replace("{request}", requestFile));
            start.Environment["WATCHROOM_REQUEST_FILE"] = requestFile;
            start.Environment["WATCHROOM_PLUGIN_DIR"] = plugin.Directory;
            using var process = new Process { StartInfo = start };
            try { if (!process.Start()) return new(false, $"{plugin.Name} didn't start."); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(false, $"{plugin.Name} couldn't start: {ex.Message}"); }
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try { await process.StandardInput.WriteAsync(json); process.StandardInput.Close(); }
            catch (IOException) { } // The plugin may not read its input, and may have finished already.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(plugin.TimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return new(false, $"{plugin.Name} didn't finish within {plugin.TimeoutSeconds} seconds.");
            }
            return Result(plugin, process.ExitCode, await output, await errors);
        }
        finally { try { File.Delete(requestFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    internal static AcquisitionResult Result(AcquisitionPlugin plugin, int exitCode, string output, string errors)
    {
        static string? Last(string text) => text.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
        var last = Last(output);
        if (last is not null && last.StartsWith('{'))
        {
            try
            {
                var response = Wire.Read<AcquisitionResponse>(last);
                var success = response.Success ?? (response.Status is { } status ? status.Equals("ok", StringComparison.OrdinalIgnoreCase) || status.Equals("success", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("queued", StringComparison.OrdinalIgnoreCase) || status.Equals("started", StringComparison.OrdinalIgnoreCase) : exitCode == 0);
                return new(success, string.IsNullOrWhiteSpace(response.Message) ? Default(plugin, success, exitCode) : response.Message.Trim());
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        }
        var ok = exitCode == 0;
        return new(ok, (ok ? last : Last(errors) ?? last) ?? Default(plugin, ok, exitCode));
    }
    private static string Default(AcquisitionPlugin plugin, bool success, int exitCode) => success ? $"{plugin.Name} is getting it." : $"{plugin.Name} failed (exit code {exitCode}).";
}
