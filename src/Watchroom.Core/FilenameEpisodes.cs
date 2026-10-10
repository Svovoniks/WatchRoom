using System.Globalization;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

// One extraction path for library numbering and independent metadata aliases.
internal static class FilenameEpisodes
{
    internal const string CrossSeasonRange = "This file contains episodes from multiple seasons; split it or correct its numbering.";
    internal const string InvalidRange = "This file has non-contiguous or invalid episode numbering; correct its numbering before matching.";
    internal sealed record Result(string Series, int? Season, int? Episode, int? End = null,
        bool ExplicitSeason = false, string? Subtitle = null, string? AirDate = null, string? Conflict = null);

    private const string Number = @"(?<n>\d{1,4})(?:v\d+)?(?!\d)";
    private const string Label = @"(?:episode|épisode|episodio|cap[ií]tulo|folge|ep|e|серия|эпизод)";
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new();
    private static Regex Pattern(string pattern) => Patterns.GetOrAdd(pattern, p => new Regex(p,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
    private static Match Match(string text, string pattern) => Pattern(pattern).Match(text);
    private static string Replace(string text, string pattern, string replacement) => Pattern(pattern).Replace(text, replacement);
    private static bool Year(int number) => number is >= 1900 and <= 2099;
    private static bool Resolution(int number) => number is 480 or 720 or 1080 or 2160 or 4320;
    private static string Trim(string text) => text.Trim(' ', '-', '[', ']', '(', ')', ':');

    internal static string Clean(string path, bool seriesContext)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // Bracketed episode markers carry identity; group, checksum and codec tags don't.
        name = Pattern(@"\[([^\]]*)\]").Replace(name, match =>
        {
            var tag = match.Groups[1].Value;
            if (Match(tag, @"^(?:s\d{1,2}[ ._-]*e\d{1,4}(?:[ ._-]*e?\d{1,4})?|\d{1,2}x\d{1,4}|(?:e|ep|episode)\s*\d{1,4})$").Success)
                return " " + tag + " ";
            if (seriesContext && match.Index > 0 && Match(tag, @"^\d{1,4}(?:-\d{1,4})?(?:v\d+)?$").Success &&
                int.TryParse(Replace(tag.Split('-')[0], @"v\d+$", ""), out var number) && !Year(number) && !Resolution(number))
                return " - " + tag + " - ";
            return " ";
        });
        name = Replace(name, @"[._]+", " ");
        name = Replace(name, @"[–—−]", "-");
        if (seriesContext)
            name = Pattern(@"\((\d{2,4}(?:v\d+)?)\)").Replace(name, match =>
                int.TryParse(Replace(match.Groups[1].Value, @"v\d+$", ""), out var n) && !Year(n) && !Resolution(n)
                    ? " - " + match.Groups[1].Value + " - " : match.Value);
        // Stop before technical release details so audio layouts cannot become 1x02 tokens.
        var release = Match(name, @"\b(?:BDRip|BRRip|DVDRip|DVDRemux|HDRip|HDTV(?:Rip)?|2160p|1080[pi]|720p|480p|\d{3,4}x\d{3,4}p?|WEB[ -]?DL|WEBRip|Blu[ -]?Ray|x264|x265|H[ -]?26[45]|HEVC|AV1|AAC|AC3|DDP|DTS|FLAC)\b");
        if (release.Success) name = name[..release.Index].TrimEnd(' ', '(', '-');
        name = Replace(name, @"\((?:dual[ -]?audio|multi[ -]?sub|uncensored|dubbed|subbed)[^)]*\)", " ");
        return Replace(name, @"\s+", " ").Trim();
    }

    internal static Result? Read(string path, bool seriesContext, int? folderSeason = null, string? cleanedName = null)
    {
        var name = cleanedName ?? Clean(path, seriesContext);
        var season = Match(name, @"\bS(?<s>\d{1,2})[ -]*(?:x[ -]*)?E(?:P(?:ISODE)?)?[ -]*" + Number + @"(?=\b|E\d)");
        if (!season.Success) season = Match(name, @"\b(?<s>\d{1,2})x" + Number + @"(?=\b|x\d)");
        if (!season.Success) season = Match(name, @"\b(?:season|сезон|saison|temporada|staffel|stagione)[ -]*(?<s>\d{1,2})[ -]*" + Label + @"[ #:-]*" + Number + @"\b");
        if (!season.Success) season = Match(name, @"\b(?<s>\d{1,2})\s*(?:сезон|sezon)\s*" + Number + @"\s*сери(?:я|и|й)\b");
        if (!season.Success) season = Match(name, @"\b[сc](?<s>\d{1,2})[ -]*[еэ]" + Number + @"\b");
        if (season.Success) return Finish(name, season, int.Parse(season.Groups["s"].Value), true);

        // Daily-show dates are only meaningful in a TV/anime folder, not movie names.
        if (seriesContext)
        {
            var date = Match(name, @"\b(?<y>19\d{2}|20\d{2})[ -](?<m>\d{2})[ -](?<d>\d{2})\b");
            if (!date.Success) date = Match(name, @"\b(?<d>\d{2})[ -](?<m>\d{2})[ -](?<y>19\d{2}|20\d{2})\b");
            if (date.Success && DateOnly.TryParseExact($"{date.Groups["y"]}-{date.Groups["m"]}-{date.Groups["d"]}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired))
                return new(Trim(name[..date.Index]), null, null, Subtitle: Subtitle(name[(date.Index + date.Length)..]), AirDate: aired.ToString("yyyy-MM-dd"));
        }
        var labelled = Match(name, @"(?:\b" + Label + @"[ #:-]*|#\s*)" + Number + @"\b");
        var acronymYear = Match(Path.GetFileNameWithoutExtension(path), @"(?:[a-z]\.){2,}E[. _-]*(?:19|20)\d{2}\b").Success;
        if (labelled.Success && !acronymYear) return Finish(name, labelled, 1, false);
        labelled = Match(name, @"\b" + Number + @"\s*(?:серия|эпизод)\b");
        if (labelled.Success) return Finish(name, labelled, 1, false);

        var numbered = Match(name, @"(?:^|\s)-\s*" + Number + @"(?=\s*(?:$|-|\(|END\b|FINAL\b))");
        if (!numbered.Success && seriesContext)
            numbered = Match(name, @"-(?<n>0\d{1,3}|\d{3,4})(?:v\d+)?(?=\s*(?:$|-|\(|END\b|FINAL\b))");
        if (numbered.Success)
        {
            var number = int.Parse(numbered.Groups["n"].Value);
            var tail = name[(numbered.Index + numbered.Length)..];
            // A bare release year or a one-digit film sequel with a subtitle isn't an episode.
            if (!Year(number) && !Resolution(number) && (seriesContext || numbered.Groups["n"].Value.Length >= 2 || Trim(tail).Length == 0))
                return Finish(name, numbered, 1, false);
        }
        if (!seriesContext) return null;
        var middle = Match(name, @"\s+(?<n>0\d{1,3}|\d{2})(?:v\d+)?\s+(?=\p{L})");
        if (middle.Success) return Finish(name, middle, 1, false);
        var prefix = Match(name, @"^" + Number + @"(?=\s|$)");
        if (prefix.Success && !Year(int.Parse(prefix.Groups["n"].Value)) && !Resolution(int.Parse(prefix.Groups["n"].Value)))
            return Finish(name, prefix, 1, false) with { Series = "" };
        var suffix = Match(name, @"\s+" + Number + @"\s*$");
        if (!suffix.Success || Year(int.Parse(suffix.Groups["n"].Value)) || Resolution(int.Parse(suffix.Groups["n"].Value))) return null;
        var digits = suffix.Groups["n"].Value;
        // 102/0102 is ambiguous with absolute anime numbering. Decode compact
        // season+episode only when a season folder independently confirms it.
        if (folderSeason is not null && digits.Length is 3 or 4 && int.Parse(digits[..^2]) == folderSeason && int.Parse(digits[^2..]) > 0)
            return new(Trim(name[..suffix.Index]), folderSeason, int.Parse(digits[^2..]), ExplicitSeason: true);
        return Finish(name, suffix, 1, false);
    }

    private static string? Subtitle(string text)
    {
        text = Trim(Replace(text, @"\s+", " "));
        return text.Length == 0 || Match(text, @"^(?:END|FINAL|COMPLETE|v\d+)$").Success ? null : text;
    }

    private static Result Finish(string name, Match token, int season, bool explicitSeason)
    {
        var start = int.Parse(token.Groups["n"].Value);
        var position = token.Index + token.Length;
        int? end = null; string? conflict = null;
        while (position < name.Length)
        {
            var tail = name[position..];
            var next = Match(tail, @"^\s*(?<dash>-)?\s*(?:S(?<s>\d{1,2})[ -]*E|(?<s>\d{1,2})x|" + Label + @"|x)?\s*" + Number + @"(?=\b|E\d|x\d)");
            if (!next.Success || !next.Groups["dash"].Success && !Match(tail, @"^\s*(?:e(?:p(?:isode)?)?|x)\d").Success) break;
            var finish = int.Parse(next.Groups["n"].Value);
            if (Year(finish) || Resolution(finish)) break;
            if (next.Groups["s"].Success && int.Parse(next.Groups["s"].Value) != season)
                conflict = CrossSeasonRange;
            else if (finish <= (end ?? start) || finish - start >= 100 || !next.Groups["dash"].Success && finish != (end ?? start) + 1)
                conflict = InvalidRange;
            else end = finish;
            position += next.Length;
        }
        return new(Trim(name[..token.Index]), season, start, conflict is null ? end : null, explicitSeason,
            Subtitle(name[position..]), Conflict: conflict);
    }
}
