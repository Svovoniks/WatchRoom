using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// Describes each reading of a name by its shape. Features never mention title words, so the model that weighs them
// can't learn any particular show; what it learns carries over to names it has never seen.
internal static class FilenameFeatures
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private static readonly Regex PartWord = new(@"(?:^|[\s(\[-])(?:part|pt|vol|volume|chapter|ch|disc|disk|cd|book|file|stage|act|arc|no|number|phase|level|round|jukebox|fight|часть|том|глава|выпуск|day|night|version|ver|jump)\s*[.:#-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex YearInText = new(@"(?<![\p{L}\p{N}.])(?:19|20)\d{2}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);

    internal static void Add(FilenameEpisodes.Hypothesis h, FilenameScan scan, FilenameFolders.FolderEvidence context, IReadOnlyList<FilenameEpisodes.Hypothesis> all)
    {
        var features = h.Features; features.Clear();
        void F(string name, float value = 1) { if (value != 0) features.Add(new(name, value)); }
        // The same shape can mean different things in a show folder, a film folder and with no folder at all.
        var scope = context.Series ? "series:" : context.Movies ? "movies:" : "open:";
        void S(string name, float value = 1) { F(name, value); F(scope + name, value); }

        S("form:" + h.Form);
        var text = scan.Text;
        var before = text[..Math.Min(h.Start, text.Length)].TrimEnd();
        var after = h.End < text.Length ? text[h.End..] : "";
        var afterTrim = after.TrimStart();
        var coreRest = h.End < scan.CoreEnd ? scan.Slice(h.End, scan.CoreEnd) : "";
        var titleText = scan.Slice(scan.CoreStart, h.SeasonStart >= scan.CoreStart && h.SeasonStart < h.Start ? h.SeasonStart : h.Start);
        var titleWords = titleText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var bareCount = all.Count(x => x.Form == FilenameEpisodes.Form.Bare);
        var hasYear = YearInText.IsMatch(scan.Slice(scan.CoreStart, scan.CoreEnd));
        if (scan.LeadingGroup) S("group");
        if (context.SeasonFolder) S("seasonFolder");
        if (context.HasFolder) F("hasFolder");
        if (scan.AllBrackets) F("allBrackets");
        if (!scan.Spaced) F("scene");
        if (scan.HasTail) F("tail");
        if (scan.LeadingKindTag) S("kindTag");

        switch (h.Form)
        {
            case FilenameEpisodes.Form.None:
            case FilenameEpisodes.Form.Film:
                if (hasYear) S("none:year");
                if (bareCount == 0) S("none:noNumbers");
                if (bareCount >= 1) S("none:numbers");
                if (all.Any(x => x.Form == FilenameEpisodes.Form.Bare && context.Varies(x.Start))) S("none:varyingNumber");
                if (titleWords == 0) F("none:noTitle");
                if (context.Ordered > 0) S("none:orderedFolder");
                if (h.Form == FilenameEpisodes.Form.Film)
                {
                    if (coreRest.Length == 0) F("film:last");
                    if (titleWords == 0) F("film:first");
                    if (all.Any(x => x.Form is FilenameEpisodes.Form.SeasonEpisode or FilenameEpisodes.Form.SeasonWords)) F("film:marked");
                }
                break;
            case FilenameEpisodes.Form.Clip:
                if (h.ShortClip) S("clip:short"); else S("clip:word");
                if (System.Text.RegularExpressions.Regex.IsMatch(h.Clip ?? "", @"(?i)^(?:ova|oav|oad|ona|sp|specials?)$", RegexOptions.None, Limit)) S("clip:specialWord");
                if (h.LeadingTag) S("clip:leadingTag");
                if (before.EndsWith('-') || before.Length == 0) S("clip:slot");
                if (coreRest.Length == 0) S("clip:last");
                else if (coreRest.Length < 40 && afterTrim.StartsWith('-')) S("clip:thenDash");
                else S("clip:thenText");
                if (titleWords == 0 && !h.LeadingTag) F("clip:noTitle");
                if (h.Start >= scan.CoreEnd) F("clip:afterCore");
                if (scan.BracketAt(h.Start) is not null) F("clip:bracket");
                if (bareCount > 0) F("clip:numbers");
                if (System.Text.RegularExpressions.Regex.IsMatch(h.Clip ?? "", @"(?i)^(?:ova|oav|oad|ona|sp|specials?)$", RegexOptions.None, Limit) && all.Any(x => x.Form == FilenameEpisodes.Form.Bare && context.Varies(x.Start))) F("clip:specialVarying");
                if (all.Any(x => x.Form == FilenameEpisodes.Form.Bare && x.End <= h.Start && h.Start - x.End <= 3)) S("clip:afterNumber");
                if (all.Any(x => x.Form == FilenameEpisodes.Form.Bare && context.Varies(x.Start))) S("clip:varyingNumber");
                break;
            case FilenameEpisodes.Form.Date:
                if (titleWords == 0) F("date:noTitle");
                if (scan.BracketAt(h.Start) is not null) F("date:bracket");
                if (h.Digits is not null) F("date:" + h.Digits);
                if (all.Any(x => x.Form == FilenameEpisodes.Form.Bare && x.End <= h.Start)) F("date:afterNumber");
                if (coreRest.Length > 0) F("date:subtitle");
                break;
            default:
                Number(h, scan, context, S, F, before, after, afterTrim, coreRest, titleWords, titleText, all);
                break;
        }
    }

    private static void Number(FilenameEpisodes.Hypothesis h, FilenameScan scan, FilenameFolders.FolderEvidence context, Action<string, float> S, Action<string, float> F,
        string before, string after, string afterTrim, string coreRest, int titleWords, string titleText, IReadOnlyList<FilenameEpisodes.Hypothesis> all)
    {
        var value = h.Episode ?? 0;
        var digits = h.Digits ?? "";
        var bracket = scan.BracketAt(h.Start);
        // "[TV Series] Show - Message #9": a tag in front says this is a collection's entry, not a numbered episode file.
        if (scan.LeadingKindTag) F("num:kindTag", 1);
        if (bracket is not null && h.Form != FilenameEpisodes.Form.Bare) F("num:inBracket", 1);
        if (titleWords == 0) S("num:noTitle", 1);
        else if (titleWords == 1) F("num:oneWord", 1);
        if (h.Start >= scan.CoreEnd) F("num:afterCore", 1);
        if (h.Season is not null && h.Form == FilenameEpisodes.Form.Bare) S("bare:season", 1);
        if (h.Conflict is not null) F("num:conflict", 1);
        if (h.Form is FilenameEpisodes.Form.Label) { if (before.EndsWith('-')) F("label:dash", 1); if (value is >= 1900 and <= 2099) F("label:year", 1); }
        if (h.Form == FilenameEpisodes.Form.Pair)
        {
            if (context.InRange(value)) F("pair:inRange", 1);
            if (context.FolderSeason == h.Season) F("pair:folderSeason", 1);
            if (bracket is not null) F("pair:bracket", 1);
            if (context.Varies(h.Start)) F("pair:firstVaries", 1);
            if (context.Series) F("pair:series", 1);
            if (h.DottedPair) S("pair:dotInSpacedName", 1);
            if (h.Ranged) { S("pair:hyphen", 1); if ((h.Episode ?? 0) - (h.Season ?? 0) > 6) S("pair:longSpan", 1); }
            if (h.Glued) S("pair:seasonMark", 1);
            if (titleWords == 0) S("pair:leading", 1);
            if (scan.LeadingGroup) F("pair:group", 1);
            return;
        }
        if (h.Form is not (FilenameEpisodes.Form.Bare or FilenameEpisodes.Form.Compact))
        {
            if (context.InRange(value)) F("num:inFolderRange", 1);
            return;
        }
        // Where the number sits.
        if (bracket is { } b)
        {
            var only = scan.Text[(b.Start + 1)..(b.End - 1)].Trim().Length <= h.End - h.Start;
            S(b.Open == '[' ? only ? "bare:square" : "bare:inSquare" : only ? "bare:round" : "bare:inRound", 1);
        }
        if (before.EndsWith(" -") || before.EndsWith("]-") || before.EndsWith(") -")) S("bare:dash", 1);
        else if (before.EndsWith('-')) S("bare:tightDash", 1);
        else if (before.Length == 0 || titleWords == 0) S("bare:leading", 1);
        else if (bracket is null) S("bare:space", 1);
        // What comes after it.
        if (coreRest.Length == 0) S("bare:last", 1);
        else if (afterTrim.StartsWith('-') && after.StartsWith(' ')) S("bare:thenDash", 1);
        else if (scan.Original.Length > h.End && scan.Original[h.End] == '.') S("bare:thenDot", 1);
        else if (afterTrim.StartsWith('(')) S("bare:thenParen", 1);
        else S("bare:thenText", 1);
        if (Regex.IsMatch(afterTrim, @"^[(\[]?\s*(?:19|20)\d{2}(?![\p{L}\p{N}])", RegexOptions.None, Limit)) S("bare:thenYear", 1);
        if (Regex.IsMatch(scan.Slice(scan.CoreStart, h.Start), @"(?<![\p{L}\p{N}.])(?:19|20)\d{2}(?![\p{L}\p{N}])\W*$", RegexOptions.None, Limit)) S("bare:afterYear", 1);
        // How it looks.
        if (digits.Length > 1 && digits[0] == '0') S("bare:zero", 1);
        else S("bare:noZero", 1);
        if (h.Glued) S("bare:glued", 1);
        // "Police Python 357": an unpadded number after plain words, with no release group or tags, reads as part of a title.
        if (digits.Length > 0 && digits[0] != '0' && !scan.LeadingGroup && !scan.HasTail && bracket is null && !before.EndsWith('-'))
        {
            S("bare:plain", 1);
            if (digits.Length >= 3) S("bare:plainLong", 1);
        }
        // "1. Title", "023. Title": a number closed by a dot and a space opens a list entry.
        if ((before.Length == 0 || titleWords == 0) && scan.Spaced && scan.Original.Length > h.End + 1 && scan.Original[h.End] == '.' && scan.Original[h.End + 1] == ' ') S("bare:listEntry", 1);
        if (context.OwnFolder) S("bare:ownFolder", 1);
        // "14 Peaks 2021", "12 Monkeys (1995)": an unpadded number in front of a dated name opens a film's title.
        if ((before.Length == 0 || titleWords == 0) && digits.Length > 0 && digits[0] != '0' && YearInText.IsMatch(coreRest)) S("bare:leadingDated", 1);
        if ((before.Length == 0 || titleWords == 0) && after.TrimStart().StartsWith("- ")) S("bare:leadingDash", 1);
        // "The Simpsons/The Simpsons 112": a title that repeats its folder's name, then a number.
        if (titleWords > 0 && context.NamesFolder(titleText)) S("bare:folderTitle", 1);
        if (context.HasFolder)
        {
            if (before.Length == 0 || titleWords == 0) F("named:bare:leading", 1);
            if ((before.Length == 0 || titleWords == 0) && scan.Original.Length > h.End && scan.Original[h.End] == '.') F("named:bare:numberDot", 1);
            if (coreRest.Length == 0) F("named:bare:last", 1);
        }
        S("bare:digits" + digits.Length, 1);
        if (h.Versioned) S("bare:version", 1);
        if (h.Half) S("bare:half", 1);
        if (h.Ranged) S("bare:range", 1);
        if (value is >= 1900 and <= 2099) S("bare:yearLike", 1);
        if (value is 480 or 576 or 720 or 1080 or 2160 or 4320) S("bare:resolutionLike", 1);
        if (value == 0) S("bare:isZero", 1);
        if (value < 10 && digits.Length == 1) S("bare:singleDigit", 1);
        if (PartWord.IsMatch(scan.Slice(scan.CoreStart, h.Start))) S("bare:partWord", 1);
        var bares = all.Where(x => x.Form == FilenameEpisodes.Form.Bare).ToArray();
        if (bares.Length > 1 && bares[^1].Start == h.Start) S("bare:lastNumber", 1);
        if (bares.Length > 1 && bares[0].Start == h.Start) S("bare:firstNumber", 1);
        if (bares.Length == 1) S("bare:onlyNumber", 1);
        if (Regex.IsMatch(scan.Slice(scan.CoreStart, scan.CoreEnd), @"(?<![\p{L}\p{N}.])(?:19|20)\d{2}(?![\p{L}\p{N}])", RegexOptions.None, Limit)) S("bare:yearInName", 1);
        if (all.Any(x => x.Form == FilenameEpisodes.Form.Film && x.Start < h.Start)) F("bare:afterFilm", 1);
        if (all.Any(x => x.Form == FilenameEpisodes.Form.Clip && x.End <= h.Start)) F("bare:afterClip", 1);
        if (all.Any(x => x.Form is FilenameEpisodes.Form.SeasonEpisode or FilenameEpisodes.Form.CrossForm or FilenameEpisodes.Form.SeasonWords)) F("bare:marked", 1);
        // What the folder says about it.
        if (context.Varies(h.Start))
        {
            S("bare:varies", 1);
            if (before.Length == 0 || titleWords == 0) S("bare:variesLeading", 1);
            if (coreRest.Length == 0) S("bare:variesLast", 1);
            if (context.Ordered >= 5) S("bare:variesMany", 1);
        }
        if (context.Constant(h.Start)) S("bare:constant", 1);
        if (context.InRange(value)) S("bare:inRange", 1);
        else if (context.Ranges.Length > 0) S("bare:outOfRange", 1);
        if (context.TitleHas(digits)) S("bare:inFolderTitle", 1);
        if (h.Form == FilenameEpisodes.Form.Compact)
        {
            if (context.FolderSeason == h.Season) S("compact:folderSeason", 1);
            else if (context.FolderSeason is not null) S("compact:otherSeason", 1);
            if (digits[0] == '0') S("compact:zero", 1);
            if (h.Season == 0) S("compact:seasonZero", 1);
        }
    }
}
