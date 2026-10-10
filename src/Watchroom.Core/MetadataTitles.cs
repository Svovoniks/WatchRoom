using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

// Search aliases do not change stored show membership or file IDs.
public static class MetadataTitles
{
    public static string Normalize(string value)
    {
        value = value.Replace("×", "x").Replace("&", " and ").Replace("½", "1 2").Replace("⅓", "1 3");
        value = Regex.Replace(value, @"^the\s+", "", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\bneighbours\b", "neighbors", RegexOptions.IgnoreCase);
        value = value.Replace("ь", "", StringComparison.OrdinalIgnoreCase).Replace("ъ", "", StringComparison.OrdinalIgnoreCase);
        value = new string(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(value, @"[^\p{L}\p{N}]", "").ToLowerInvariant();
    }
    public static bool IsAnimeSource(MediaItem item) => item.Kind == "Anime" || item.SourceLibraryKind == "Anime" ||
        item.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part.Equals("Anime", StringComparison.OrdinalIgnoreCase));
    public static bool IsUnverifiedCompilation(MediaItem item) => !item.Matched && item.NumberingOrder != "dvd" &&
        !LibraryIdentity.Locked(item, "Episode") && item.NumberingSource is not ("filename" or "season-folder") &&
        Regex.IsMatch(Path.GetFileNameWithoutExtension(item.Path), @"(?i)\b(?:Collection|Compilation)\b") &&
        !Regex.IsMatch(Path.GetFileNameWithoutExtension(item.Path), @"(?i)\bS\d{1,2}[ ._-]*E\d{1,3}\b|\b\d{1,2}x\d{1,3}\b");
    // Read only the filename: using FileNames.Parse here would apply folder/NFO
    // titles again and erase the independent alias we are trying to recover.
    internal static string? FilenameAlias(string path, bool seriesContext = false)
    {
        var parsed = FilenameEpisodes.Read(path, seriesContext: true);
        var seriesFolder = seriesContext || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => Regex.IsMatch(part, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Сериалы|Season\s*\d+|S\d+)$"));
        if (parsed?.AirDate is not null && !seriesFolder) return null;
        // A one-digit unlabelled sequel with a subtitle needs actual TV folder
        // context; alias extraction alone must not turn it into a show query.
        if (parsed?.Episode is < 10 && parsed.Subtitle is not null && FilenameEpisodes.Read(path, seriesContext: false) is null &&
            !seriesFolder) return null;
        var title = parsed?.Series;
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = Regex.Replace(title, @"\s+\(?(?:19|20)\d{2}\)?\s*$", "").Trim();
        return title.Length >= 3 ? title : null;
    }
    public static string[] Queries(MediaItem item, IEnumerable<MediaItem>? entries = null)
    {
        var titles = new List<string> { item.DisplayTitle };
        void Add(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            title = Regex.Replace(title, @"\[[^\]]*\]", " ");
            title = Regex.Replace(title, @"\([^)]*(?:dual.audio|multi.?sub|ddp|aac|web|1080|720|hevc)[^)]*\)", " ", RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"(?i)\b(?:collection|dvdrip|hddvdrip|hdtvrip|webrip|web.dl|bluray|bdrip)\b.*$", "");
            title = Regex.Replace(title, @"[._]+", " ");
            title = Regex.Replace(title, @"\s+", " ").Trim(' ', '-');
            if (title.Length >= 3 && !titles.Contains(title, StringComparer.OrdinalIgnoreCase)) titles.Add(title);
        }
        Add(item.DisplayTitle);
        Add(Regex.Replace(item.DisplayTitle, @"\b(\d+)\s+1\s+([23])\b", "$1 1/$2"));
        if (item.Series is not null)
        {
            foreach (var alias in new[] { item }.Concat(entries ?? []).Where(x => !x.IsVirtual && !x.IsExtra && LibraryIdentity.SameShow(item, x))
                .Select(x => FilenameAlias(x.Path, seriesContext: true)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(4)) Add(alias);
        }
        if (item.Series is null)
        {
            var clean = Regex.Replace(item.Title, @"^\d{1,2}\s+(.+?)\s+-\s+(?:Comedy|Action|Drama|Horror|Thriller)\b.*$", "$1", RegexOptions.IgnoreCase);
            Add(clean);
            if (clean != item.Title && clean.EndsWith(" 1")) Add(clean[..^2]);
        }
        if (item.SeriesPath is { } folder) Add(Path.GetFileName(folder));
        if (item.SeriesPath is { } nested && Path.GetDirectoryName(nested) is { } parentFolder)
        {
            var parentTitle = Path.GetFileName(parentFolder);
            // A nested spin-off can omit its franchise name; don't borrow root/category names.
            if (!Regex.IsMatch(parentTitle, @"(?i)^(?:Anime|Shows?|TV(?: Shows)?|Movies?|Films?|Сериалы|Фильмы|Season\s*\d+|S\d+|\d+\s*сезон)$") &&
                parentTitle.Length >= 3 && !Normalize(item.DisplayTitle).StartsWith(Normalize(parentTitle)))
                Add(parentTitle + " " + Path.GetFileName(nested));
            if (RussianSearchAlias(Regex.Replace(item.DisplayTitle, @"\[[^\]]*\]", "").Trim()) is { } translated && Normalize(translated) == Normalize(parentTitle))
                Add(parentTitle);
        }
        if (item.Series is null)
        {
            if (item.Title.IndexOf('(') is > 0 and var bracket) Add(item.Title[..bracket]);
            foreach (Match alias in Regex.Matches(item.Title, @"\(([^()]*)\)"))
                if (!Regex.IsMatch(alias.Groups[1].Value, @"(?i)dual.audio|multi.?sub|ddp|aac|web|1080|720")) Add(alias.Groups[1].Value);
            var parent = Path.GetDirectoryName(item.Path);
            if (parent is not null)
            {
                var name = Path.GetFileName(parent);
                // Only a title-bearing dated release folder can identify a split film.
                if (Regex.IsMatch(name, @"\b(?:19|20)\d{2}\b") || Regex.IsMatch(name, @"_(?:19|20)\d{2}"))
                    Add(Regex.Replace(name, @"[_ .-]*(?:19|20)\d{2}.*$", ""));
                // A numbered sequel can borrow a franchise folder spelling, keeping its number.
                var sequel = Regex.Match(item.Title, @"^\S+\s+(\d+)$");
                if (sequel.Success && Regex.IsMatch(name, @"^[\p{L}]{3,30}$")) Add(name + " " + sequel.Groups[1].Value);
            }
        }
        foreach (var title in titles.ToArray())
            if (RussianSearchAlias(title) is { } alias) Add(alias);
        return titles.Take(12).ToArray();
    }
    public static string? RussianSearchAlias(string value)
    {
        if (Regex.IsMatch(value, @"\p{IsCyrillic}") || !Regex.IsMatch(value, @"(?i)zh|sh|kh|ya|yu|ch|ts|skaia")) return null;
        value = value.ToLowerInvariant();
        value = Regex.Replace(value, @"(?<=[aeiou])y|y(?=\b)", "й");
        value = value.Replace("skaia", "ская").Replace("liant", "лиант");
        foreach (var (latin, russian) in new[] { ("shch", "щ"), ("sch", "щ"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"), ("ch", "ч"), ("sh", "ш"), ("ya", "я"), ("yu", "ю"), ("ia", "я"), ("iu", "ию") })
            value = value.Replace(latin, russian);
        const string latinLetters = "abvgdezijklmnoprstufhcy";
        const string russianLetters = "абвгдезийклмнопрстуфхцы";
        return new string(value.Select(c => latinLetters.IndexOf(c) is var index && index >= 0 ? russianLetters[index] : c).ToArray());
    }
    public static string Romanized(string value)
    {
        const string cyrillic = "абвгдеёжзийклмнопрстуфхцчшщыэюяьъ";
        string[] latin = ["a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","y","e","yu","ya","",""];
        var result = string.Concat(value.ToLowerInvariant().Select(c => cyrillic.IndexOf(c) is var i && i >= 0 ? latin[i] : c.ToString()));
        result = result.Replace("iyu","iu").Replace("ia","ya").Replace("lie","le").Replace("ts","c").Replace("kh","h").Replace("y","i");
        return Normalize(result);
    }
    public static int? FolderYear(MediaItem item)
    {
        var name = Path.GetFileName(Path.GetDirectoryName(item.Path));
        var year = Regex.Match(name ?? "", @"(?:^|[ _.(-])((?:19|20)\d{2})(?:$|[ _.)-])");
        return year.Success ? int.Parse(year.Groups[1].Value) : null;
    }
    public static MediaItem WithSplitIdentity(MediaItem item, IEnumerable<MediaItem> library)
    {
        if (item.Matched || item.MetadataLocked || item.MetadataId is not null || !Regex.IsMatch(Path.GetFileNameWithoutExtension(item.Path), @"\((?:19|20)\d{2}\)\s+\d{1,2}$")) return item;
        var parsed = FileNames.Parse(item.Path);
        var siblings = library.Where(x => x.Matched && x.MetadataId is not null && Path.GetDirectoryName(x.Path) == Path.GetDirectoryName(item.Path))
            .Where(x => { var other = FileNames.Parse(x.Path); return Normalize(other.Title) == Normalize(parsed.Title) && other.Year == parsed.Year; })
            .DistinctBy(x => (x.MetadataProvider, x.MetadataId, x.MetadataType)).ToArray();
        return siblings.Length == 1 ? item with { ProviderIds = siblings[0].ProviderIds, MetadataProvider = siblings[0].MetadataProvider,
            MetadataId = siblings[0].MetadataId, MetadataType = siblings[0].MetadataType } : item;
    }
}
