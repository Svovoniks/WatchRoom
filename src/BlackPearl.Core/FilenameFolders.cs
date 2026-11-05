using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// What a file's surroundings say about its name: which numbers change between sibling files, and what the folders above it
// name or number. Whole-folder evidence works for any naming scheme: the running number is the one that varies.
internal static class FilenameFolders
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private const string RootNames = @"Anime|Shows?|TV(?: Shows)?|Series|Сериалы|Аниме|Movies?|Films?|Фильмы|Кино|Kino|Videos?|Video|Видео|Downloads?|Загрузки|Torrents?|My Torrents|Desktop|Media|Library|Раздачи|Фильмы на трекер|zRELIZESz|UHD|\+UHD|HD|Cartoons|Мультфильмы|Советские|New|Temp|Tmp|\d{1,3}|(?:19|20)\d{2}|(?:winter|spring|summer|fall|autumn)\s*(?:19|20)\d{2}|.*(?:торрент|раздач|torrents?|download).*";
    private static readonly Regex GenericFolder = new(@"(?i)^(?:" + RootNames + @")$", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex ContainerFolder = new(@"(?i)^(?:mpg|mp4|mkv|avi|mpeg-?2?|ogm|wmv|video_ts|bdmv|stream|\d{3,4}p|bd|dvd|web|web-?dl|bluray|raw|raws|hevc|x264|x265|ova|oad|ona|tv|movies?|films?|specials?|(?:disc|disk|cd|vol\.?|volume)\s*\d{1,2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex MovieFolder = new(@"(?i)^(?:Movies?|Films?|Фильмы|Кино|Kino)$", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex Range = new(@"(?<![\p{L}\p{N}.])(?:EP?|Ep\.?|E)?\s?(?<a>\d{1,4})\s?[-~–—]\s?(?:EP?\s?)?(?<b>\d{1,4})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);

    internal sealed record FolderEvidence(bool Series, bool Movies, int? FolderSeason, bool SeasonFolder, bool HasFolder, (int Low, int High)[] Ranges, string[] TitleNumbers,
        IReadOnlyDictionary<int, bool>? Slots, int Ordered, bool OwnFolder = false, string[]? FolderTitles = null, IReadOnlySet<int>? PlainRuns = null)
    {
        // A number in a run that can't hold seasons and episodes together: 001 to 220 counts up, so 111 isn't season 1, episode 11.
        internal bool PlainRun(int offset) => PlainRuns?.Contains(offset) == true;
        internal bool NamesFolder(string title) => FolderTitles is not null && FolderTitles.Contains(MetadataTitles.Normalize(title));
        internal static readonly FolderEvidence None = new(false, false, null, false, false, [], [], null, 0);
        internal bool Varies(int offset) => Slots is not null && Slots.TryGetValue(offset, out var state) && state;
        internal bool Constant(int offset) => Slots is not null && Slots.TryGetValue(offset, out var state) && !state;
        internal bool InRange(int value) => Ranges.Any(r => value >= r.Low && value <= r.High);
        internal bool TitleHas(string digits) => TitleNumbers.Contains(digits.TrimStart('0'));
    }

    private sealed record FolderSlots(DateTime Written, Dictionary<string, Dictionary<int, bool>> Names, int Ordered, Dictionary<string, HashSet<int>>? PlainRuns = null);
    private static readonly ConcurrentDictionary<string, FolderSlots> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    // What a folder's own name says never changes, so each directory path is read once.
    private sealed record FolderNames((int Low, int High)[] Ranges, string[] Numbers, string[] Titles, bool Movies, bool HasFolder, string Last);
    private static readonly ConcurrentDictionary<string, FolderNames> Names = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static bool IsGeneric(string folder) => GenericFolder.IsMatch(folder.Trim());
    // "DVDRip.MKV.686x480.RAW", "SourceUnknown.AVI.480x360.enDub": a folder that only describes its files.
    private static readonly Regex FormatFolder = new(@"(?i)(?:^|[. _])(?:avi|mkv|mp4|mpg|ogm|wmv)[. _](?:\d{3,4}x\d{3,4}|\d{3,4}p)(?:$|[. _])|(?:^|[. _])\d{3,4}x\d{3,4}[. _](?:dual|en|jp|raw)", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    internal static bool IsContainer(string folder) => ContainerFolder.IsMatch(folder.Trim()) || FormatFolder.IsMatch(folder) || new FilenameScan(folder).Slice(0, folder.Length).Length == 0;

    internal static FolderEvidence Evidence(string path, bool seriesContext, int? folderSeason, string? libraryRoot, bool movieLibrary = false)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return FolderEvidence.None with { Series = seriesContext, Movies = movieLibrary && !seriesContext, FolderSeason = folderSeason, SeasonFolder = folderSeason is not null };
        var names = Names.TryGetValue(directory, out var known) ? known : ReadNames(directory);
        var isRoot = libraryRoot is not null && string.Equals(Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar), directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var slots = Siblings(directory);
        slots.Names.TryGetValue(Path.GetFileName(path), out var own);
        HashSet<int>? plain = null;
        slots.PlainRuns?.TryGetValue(Path.GetFileName(path), out plain);
        // A file inside a folder of its own name: one release, not a run of episodes.
        var ownFolder = string.Equals(names.Last, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
        return new(seriesContext, (movieLibrary || names.Movies) && !seriesContext, folderSeason, folderSeason is not null, !isRoot && names.HasFolder, names.Ranges, names.Numbers, own, slots.Ordered, ownFolder, names.Titles, plain);
    }

    private static FolderNames ReadNames(string directory)
    {
        var parts = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var ranges = new List<(int, int)>();
        var numbers = new List<string>();
        var titles = new List<string>();
        foreach (var part in parts.TakeLast(3))
        {
            foreach (Match match in Range.Matches(part))
            {
                var low = int.Parse(match.Groups["a"].Value); var high = int.Parse(match.Groups["b"].Value);
                if (low < high && !(low is >= 1900 and <= 2099 && high is >= 1900 and <= 2099)) ranges.Add((low, high));
            }
            if (!IsGeneric(part) && FilenameEpisodes.AnalyzeFolder(part).Title is { Length: > 0 } named) titles.Add(MetadataTitles.Normalize(named));
            numbers.AddRange(Regex.Matches(new FilenameScan(part).Slice(0, part.Length), @"(?<![\p{L}\p{N}])\d{1,4}(?![\p{L}\p{N}])", RegexOptions.None, Limit).Select(m => m.Value.TrimStart('0')));
        }
        var result = new FolderNames(ranges.ToArray(), numbers.Distinct().ToArray(), titles.ToArray(), parts.Any(MovieFolder.IsMatch), parts.Length > 0 && !IsGeneric(parts[^1]), parts.Length > 0 ? parts[^1] : "");
        if (Names.Count > 8192) Names.Clear();
        Names[directory] = result;
        return result;
    }

    private static FolderSlots Siblings(string directory)
    {
        DateTime written;
        try { written = Directory.GetLastWriteTimeUtc(directory); }
        catch (IOException) { return new(default, [], 0); }
        catch (UnauthorizedAccessException) { return new(default, [], 0); }
        if (Cache.TryGetValue(directory, out var cached) && cached.Written == written) return cached;
        Dictionary<string, Dictionary<int, bool>> names = [];
        Dictionary<string, HashSet<int>> plainRuns = [];
        var ordered = 0;
        try
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.EnumerateFiles(directory).Select(file => Path.GetFileName(file)!).Where(file => FileNames.Extensions.Contains(Path.GetExtension(file))).ToArray();
                (names, ordered, plainRuns) = Align(files);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var result = new FolderSlots(written, names, ordered, plainRuns);
        if (Cache.Count > 4096) Cache.Clear();
        Cache[directory] = result;
        return result;
    }

    private static readonly Regex HashTag = new(@"[\[(]\s*[0-9A-Fa-f]{8}\s*[\])]", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);

    // Walk the names left to right. Names that read the same up to a number form a group; if the number differs across the
    // group it's the running number. If it's the same, it's part of the title or the release, and the walk continues.
    internal static (Dictionary<string, Dictionary<int, bool>> Names, int Ordered, Dictionary<string, HashSet<int>> PlainRuns) Align(IReadOnlyCollection<string> files)
    {
        var names = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);
        var plainRuns = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        if (files.Count < 2) return (names, 0, plainRuns);
        var rows = files.Select(file =>
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            stem = FilenameScan.Composed(stem);
            var masked = HashTag.Replace(stem, match => new string('\0', match.Length));
            return (File: file, Runs: DigitRun.Matches(masked).Select(m => (Start: m.Index, Value: m.Value)).ToArray(), Text: masked.ToLowerInvariant());
        }).ToArray();
        var maxRun = 0;
        void Walk(IReadOnlyList<(string File, (int Start, string Value)[] Runs, string Text)> group, int run)
        {
            // Group by the text between the previous number and this one.
            var byGap = group.GroupBy(row =>
            {
                if (run >= row.Runs.Length) return "\u0001end";
                var from = run == 0 ? 0 : row.Runs[run - 1].Start + row.Runs[run - 1].Value.Length;
                return row.Text[from..row.Runs[run].Start];
            });
            foreach (var gap in byGap)
            {
                var members = gap.ToArray();
                if (gap.Key == "\u0001end" || members.Length < 2) continue;
                var values = members.Select(row => int.Parse(row.Runs[run].Value.Length > 9 ? "0" : row.Runs[run].Value)).ToArray();
                var distinct = values.Distinct().Count();
                // Each file with a year of its own is a set of releases, not a run: "Ocean's 11 (2001)", "Ocean's 12 (2004)".
                if (distinct >= 2 && OwnYears(members, run))
                {
                    foreach (var row in members)
                    {
                        if (!names.TryGetValue(row.File, out var slots)) names[row.File] = slots = [];
                        slots[row.Runs[run].Start] = false;
                    }
                }
                else if (distinct >= 2 && distinct * 2 >= members.Length && IsRun(values, distinct) && !(gap.Key.Trim(' ', '.', '_').Length == 0 && TitledFilms(members, run, values, distinct)))
                {
                    if (distinct >= 100 || members.Any(row => row.Runs[run].Value[0] == '0'))
                        foreach (var row in members)
                        {
                            if (!plainRuns.TryGetValue(row.File, out var plain)) plainRuns[row.File] = plain = [];
                            plain.Add(row.Runs[run].Start);
                        }
                    foreach (var row in members)
                    {
                        if (!names.TryGetValue(row.File, out var slots)) names[row.File] = slots = [];
                        slots[row.Runs[run].Start] = true;
                    }
                    maxRun = Math.Max(maxRun, distinct);
                }
                else if (distinct == 1)
                {
                    foreach (var row in members)
                    {
                        if (!names.TryGetValue(row.File, out var slots)) names[row.File] = slots = [];
                        slots[row.Runs[run].Start] = false;
                    }
                    Walk(members, run + 1);
                }
                else Walk(members, run + 1);
            }
        }
        Walk(rows, 0);
        return (names, maxRun, plainRuns);
    }
    // "12 Monkeys", "13 Hours", "21 Grams": unpadded numbers opening names with words after them are titles, unless they count up almost without a gap.
    private static bool TitledFilms(IReadOnlyList<(string File, (int Start, string Value)[] Runs, string Text)> members, int run, int[] values, int distinct)
    {
        if (members.Any(row => row.Runs[run].Value[0] == '0')) return false;
        if (!members.All(row => row.Text[(row.Runs[run].Start + row.Runs[run].Value.Length)..].Any(char.IsLetter))) return false;
        return distinct < 3 || (values.Max() - values.Min() + 1) * 2 > distinct * 3;
    }
    // A running number has few gaps for how many values it takes: 1 and 12 can be the episodes someone kept, 12 and 300 aren't.
    // Seasons written into the number jump by hundreds: 101, 102, 201.
    private static bool IsRun(int[] values, int distinct) => values.Max() - values.Min() < 3 * distinct + 20 ||
        distinct >= 3 && values.All(value => value is > 100 and < 4100 && value % 100 is > 0 and <= 60);
    private static bool OwnYears(IReadOnlyList<(string File, (int Start, string Value)[] Runs, string Text)> members, int run)
    {
        var years = new HashSet<string>();
        foreach (var row in members)
        {
            if (run + 1 >= row.Runs.Length || row.Runs[run + 1].Value is not { Length: 4 } year || !(year.StartsWith("19") || year.StartsWith("20"))) return false;
            var gap = row.Text[(row.Runs[run].Start + row.Runs[run].Value.Length)..row.Runs[run + 1].Start];
            if (gap.Trim(' ', '.', '_', '(', '[', '-').Length > 0) return false;
            years.Add(year);
        }
        return years.Count >= 2;
    }
}
