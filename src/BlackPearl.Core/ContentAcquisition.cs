using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// A program that fetches missing or upcoming videos. Each plugin is a folder in the plugins folder holding plugin.json;
// BlackPearl runs its command with a request on standard input saying what to get and where it belongs, and the files
// join the library on the next scan. See PLUGINS.md.
public record AcquisitionPlugin(string Id, string Name, string Description, string[] ContentTypes, string Command, string[] Arguments,
    string Directory, int TimeoutSeconds, bool Interactive = false, string? SettingsSchema = null)
{
    public bool Supports(string contentType) => ContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);
}
public record AcquisitionManifest(string? Id = null, string? Name = null, string? Description = null, string[]? ContentTypes = null,
    string? Command = null, string[]? Arguments = null, int? TimeoutSeconds = null, bool Interactive = false, string? SettingsSchema = null);
public record AcquisitionSetting(string Key, string Label, string Type = "text", string[]? Choices = null, string? Default = null, string? Help = null);
public record AcquisitionOptions(string? Query = null, string? SourceUrl = null, int? Resolution = null, string? Filter = null, int? EpisodeOffset = null, int? ProviderSeason = null);
// Assignable: a release of the right title with no episode number, such as a season pack or an OVA. It is never
// ticked automatically, but the user may tick it on its own to download it for the request.
public record AcquisitionChoice(string Id, string Title, string Detail = "", bool Matches = false, string? Episode = null, int? Resolution = null, string? SuggestedFilter = null, bool Assignable = false)
{
    // The episode the release provides: the requested one it matches, or else the one its name gives, such as S01E01.
    [System.Text.Json.Serialization.JsonIgnore] public string? EpisodeCode => Episode ?? ContentAcquisition.EpisodeInName(Title);
}
// A library folder and what it holds: anime, show, movie or mixed.
public record AcquisitionLibrary(string Path, string Kind);
public record AcquisitionEpisode(int Season, int Episode, string? Title, string? AirDate, bool Upcoming, int? AbsoluteEpisode = null);
// Scope is episode, season or movie. Name is the video to get, such as "Frieren S01E05", "Frieren S01" or "Dune (2021)".
// TargetDirectory is where the files belong: the season's folder for an episode, the show's folder for a season and a
// library folder for a film. It may not exist yet when nothing of the show or season is on disk.
public record AcquisitionRequest(int Version, string Scope, string ContentType, string Name, string Title, string? OriginalTitle, int? Year,
    string TargetDirectory, bool TargetExists, string? ShowDirectory, string? SeasonDirectory, int? Season, AcquisitionEpisode[] Episodes,
    Dictionary<string, string> ProviderIds, AcquisitionLibrary[] Libraries, string? Action = null, AcquisitionOptions? Options = null, string[]? SelectedReleases = null);
public record AcquisitionResult(bool Success, string Message, string? Stage = null, AcquisitionChoice[]? Choices = null);
// What a plugin may print as its last line of output.
public record AcquisitionResponse(bool? Success = null, string? Status = null, string? Message = null, string? Stage = null, AcquisitionChoice[]? Choices = null);

public static class ContentAcquisition
{
    public const string ManifestName = "plugin.json";
    public const int RequestVersion = 1;
    public static readonly string[] ContentTypes = ["anime", "show", "movie"];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // The best downloadable release of each episode comes first (what Select matching picks), then the other
    // downloadable releases, then other releases of the requested episodes, then season packs and specials, then the
    // rest. The episode groups run in episode order; within an episode, and among the rest, the user's search terms (for example a release group)
    // decide first. Score each distinct term once. Then the picks are chosen together as a batch: one release per
    // episode, named as much as possible like each other and like the show's files on disk (existing), so a season
    // comes from one group and encode. The other releases follow by how close their names are to the picks. Tied
    // results prefer a named release group and then retain the provider's order; ranking never changes whether a
    // release matches.
    public static AcquisitionChoice[] RankChoices(IEnumerable<AcquisitionChoice> choices, string? query, IReadOnlyCollection<string>? existing = null)
    {
        // "S02E10" sorts after "S02E9"; a choice without numbers sorts by its text.
        static string EpisodeKey(string? episode) => Regex.Replace(episode ?? "", @"\d+", match => match.Value.PadLeft(6, '0'));
        var terms = NameWords(query);
        var library = (existing ?? []).Select(x => new ReleaseName(x)).ToArray();
        var scored = choices.Select((choice, index) =>
        {
            var words = NameWords(choice.Title).ToHashSet(StringComparer.Ordinal);
            var name = new ReleaseName(choice.Title);
            return new
            {
                Choice = choice, Index = index, Name = name, Named = name.Group.Count > 0,
                Exact = terms.Count(words.Contains),
                Prefix = terms.Count(term => !words.Contains(term) && words.Any(word => word.StartsWith(term, StringComparison.Ordinal))),
                Library = library.Length == 0 ? 0 : library.Min(file => Distance(name, file))
            };
        }).ToArray();

        // Candidates: the downloadable releases of each episode that best fit the search terms.
        var episodes = scored.Where(x => x.Choice.Matches).GroupBy(x => x.Choice.Episode)
            .Select(x => { var best = x.Max(y => (y.Exact, y.Prefix)); return x.Where(y => (y.Exact, y.Prefix) == best).ToArray(); }).ToArray();
        // Matching the files on disk counts as much as matching the rest of the batch.
        var libraryWeight = Math.Max(1, episodes.Length - 1);
        var picks = new int[episodes.Length];
        int Best(int episode, Func<int, int> cost) => Enumerable.Range(0, episodes[episode].Length)
            .OrderBy(cost).ThenByDescending(i => episodes[episode][i].Named).ThenBy(i => episodes[episode][i].Index).First();
        // Start from the releases nearest the files on disk, or else nearest the other episodes' releases; then re-pick
        // each episode against the others until the batch settles.
        for (var e = 0; e < episodes.Length; e++)
        {
            var episode = e;
            picks[e] = Best(e, i => libraryWeight * episodes[episode][i].Library + Enumerable.Range(0, episodes.Length).Where(other => other != episode)
                .Sum(other => episodes[other].Min(y => Distance(episodes[episode][i].Name, y.Name))));
        }
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            for (var e = 0; e < episodes.Length; e++)
            {
                var episode = e;
                var pick = Best(e, i => libraryWeight * episodes[episode][i].Library + Enumerable.Range(0, episodes.Length).Where(other => other != episode)
                    .Sum(other => Distance(episodes[episode][i].Name, episodes[other][picks[other]].Name)));
                if (pick != picks[e]) { picks[e] = pick; changed = true; }
            }
            if (!changed) break;
        }
        var chosen = episodes.Select((x, e) => x[picks[e]]).ToArray();
        var chosenSet = chosen.Select(x => x.Choice).ToHashSet(ReferenceEqualityComparer.Instance);
        var ranked = scored.OrderByDescending(x => x.Choice.Matches).ThenByDescending(x => x.Choice.Episode is { Length: > 0 }).ThenByDescending(x => x.Choice.Assignable)
            .ThenBy(x => EpisodeKey(x.Choice.Episode), StringComparer.Ordinal).ThenByDescending(x => x.Exact).ThenByDescending(x => x.Prefix)
            .ThenByDescending(x => chosenSet.Contains(x.Choice))
            .ThenBy(x => libraryWeight * x.Library + chosen.Where(y => y.Choice.Episode != x.Choice.Episode).Sum(y => Distance(x.Name, y.Name)))
            .ThenByDescending(x => x.Named).ThenBy(x => x.Index).Select(x => x.Choice).ToArray();
        return [.. ranked.Where(chosenSet.Contains), .. ranked.Where(x => !chosenSet.Contains(x))];
    }

    private static string[] NameWords(string? text)
    {
        var normalized = new string((text ?? "").Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return Regex.Matches(normalized, @"[\p{L}\p{N}]+").Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray();
    }
    // A release or file name as the words that tell releases apart. Words that only say which episode it is cost
    // nothing: episode codes, bare numbers, versions and CRC checksums such as [ABCDEF12].
    private sealed class ReleaseName
    {
        private static readonly Regex EpisodeWord = new(@"^(?:\d+(?:v\d+)?|s\d+e\d+(?:v\d+)?|e\d+|\d+x\d+|v\d+|(?=[a-f]*\d)[0-9a-f]{8})$", RegexOptions.CultureInvariant);
        public readonly HashSet<string> Words, Group;
        public ReleaseName(string? name)
        {
            Words = NameWords(name).Where(x => !EpisodeWord.IsMatch(x)).ToHashSet(StringComparer.Ordinal);
            Group = NameWords(ReleaseGroup(name)).ToHashSet(StringComparer.Ordinal);
        }
    }
    // How different two names are: each word in only one of them costs 1, or 3 when it names a release group, so
    // "S01E01" to "S01E02" costs nothing and "MeGusta" to "ELiTE" costs 6.
    private static int Distance(ReleaseName a, ReleaseName b)
    {
        var cost = 0;
        foreach (var word in a.Words) if (!b.Words.Contains(word)) cost += a.Group.Contains(word) || b.Group.Contains(word) ? 3 : 1;
        foreach (var word in b.Words) if (!a.Words.Contains(word)) cost += a.Group.Contains(word) || b.Group.Contains(word) ? 3 : 1;
        return cost;
    }
    public static int NameDistance(string? a, string? b) => Distance(new ReleaseName(a), new ReleaseName(b));

    // The group that made a release: "[Erai-raws] Show - 01 [1080p].mkv" or "Show.S01E01.1080p.WEB.x265-MeGusta".
    private static readonly Regex LeadingGroup = new(@"^\s*\[([^\]]+)\]", RegexOptions.CultureInvariant);
    // Scene names attach the group with a dash ("x264-KILLERS"); "Show - OVA" is a title, not a group.
    private static readonly Regex TrailingGroup = new(@"(?<=\S)-([A-Za-z0-9][A-Za-z0-9_.]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex TrailingNoise = new(@"(?:\.(?:mkv|mp4|avi|m4v|ts|wmv)$|\s*[\[\(][^\]\)]*[\]\)]\s*$|[\s.]+(?:eztv|rarbg|ettv|eztvx\.to)$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string? ReleaseGroup(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var name = Path.GetFileName(title.Trim());
        if (LeadingGroup.Match(name) is { Success: true } leading) return leading.Groups[1].Value.Trim();
        for (string previous = ""; previous != name;) { previous = name; name = TrailingNoise.Replace(name, "").TrimEnd(); }
        // "WEB-DL" or "Blu-ray" end a name without a group; a group has at least two characters.
        return TrailingGroup.Match(name) is { Success: true } trailing && trailing.Groups[1].Value.Length >= 2
            && !Regex.IsMatch(trailing.Groups[1].Value, @"^(?:DL|Rip|ray|\d+)$", RegexOptions.IgnoreCase) ? trailing.Groups[1].Value : null;
    }
    // The names of the show's files on disk that releases are compared with: its files of the requested season, or
    // of the whole show when that season has none.
    public static string[] ExistingNames(AcquisitionRequest request, IEnumerable<MediaItem> library)
    {
        if (request.Scope == "movie") return [];
        var files = library.Where(x => !x.IsVirtual && x.Series is not null && string.Equals(x.Series, request.Title, StringComparison.OrdinalIgnoreCase)).ToArray();
        var season = files.Where(x => x.Season == request.Season).ToArray();
        return (season.Length > 0 ? season : files).Select(x => Path.GetFileNameWithoutExtension(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();
    }

    private static readonly Regex EpisodeName =new(@"(?<![a-z0-9])(?:s(\d{1,2})e(\d{1,3})|(\d{1,2})x(\d{1,3}))(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string? EpisodeInName(string? title) => title is null ? null : EpisodeName.Match(title) is { Success: true } match
        ? $"S{int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value):00}E{int.Parse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value):00}" : null;

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
                    types.OfType<string>().Distinct().ToArray(), manifest.Command.Trim(), manifest.Arguments ?? [], folder, Math.Clamp(manifest.TimeoutSeconds ?? 120, 5, 3600), manifest.Interactive, manifest.SettingsSchema));
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

    // Runs the plugin with the request on standard input, also saved to the file named by BLACKPEARL_REQUEST_FILE.
    // Success is exit code 0 unless the last line printed is JSON with success or status; its message is shown to the user.
    public static async Task<AcquisitionResult> RunAsync(AcquisitionPlugin plugin, AcquisitionRequest request, CancellationToken ct)
    {
        var json = Wire.Serialize(request);
        var requestFile = Path.Combine(Path.GetTempPath(), $"blackpearl-acquire-{Guid.NewGuid():N}.json");
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
            // Plugins written while the app was called Watchroom read the WATCHROOM_ names, so both are set.
            foreach (var prefix in new[] { AppEnvironment.Prefix, AppEnvironment.FormerPrefix })
            {
                start.Environment[prefix + "REQUEST_FILE"] = requestFile;
                start.Environment[prefix + "PLUGIN_DIR"] = plugin.Directory;
            }
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
                return new(success, string.IsNullOrWhiteSpace(response.Message) ? Default(plugin, success, exitCode) : response.Message.Trim(), response.Stage, response.Choices);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        }
        var ok = exitCode == 0;
        return new(ok, (ok ? last : Last(errors) ?? last) ?? Default(plugin, ok, exitCode));
    }
    private static string Default(AcquisitionPlugin plugin, bool success, int exitCode) => success ? $"{plugin.Name} is getting it." : $"{plugin.Name} failed (exit code {exitCode}).";
}
