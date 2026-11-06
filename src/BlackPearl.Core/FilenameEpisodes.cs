using System.Globalization;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// One reading path for library numbering and independent metadata aliases. A name is read by listing everything its
// numbers could mean, scoring those readings on the shape of the name (FilenameModel), and keeping the best one.
// Nothing here knows any title: scores come only from positions, separators, folders and sibling files.
internal static class FilenameEpisodes
{
    internal const string CrossSeasonRange = "This file contains episodes from multiple seasons; split it or correct its numbering.";
    internal const string InvalidRange = "This file has non-contiguous or invalid episode numbering; correct its numbering before matching.";
    internal sealed record Result(string Series, int? Season, int? Episode, int? End = null,
        // Subtitle is every word after the number, which says how a name is built. Name is what of it names the episode.
        bool ExplicitSeason = false, string? Subtitle = null, string? AirDate = null, string? Conflict = null, string? Name = null);

    // What the whole name says, whether or not it holds an episode number.
    internal sealed record Reading(string Title, string[] Alternates, int? Year, Result? Numbering, string? Clip, bool Film, string Cleaned, bool LeadingGroup, string Kind);

    internal enum Form { None, SeasonEpisode, CrossForm, SeasonWords, Label, Date, Bare, Compact, Pair, Special, Clip, Film }

    internal sealed class Hypothesis
    {
        public Form Form; public int Start; public int End; public int SeasonStart = -1; public int? Season; public int? Episode; public int? EpisodeEnd;
        public bool Explicit; public string? AirDate; public bool Half; public string? Clip; public bool ShortClip; public bool LeadingTag; public string? Digits; public bool Versioned; public bool Ranged; public bool Glued; public int SeasonAfterEnd; public bool DottedPair; public string? Conflict;
        public double Score; public List<KeyValuePair<string, float>> Features = [];
    }

    private const string Number = @"(?<n>\d{1,4})(?:v\d+)?(?!\d)";
    // Words for an episode and for a season, in the languages names show up in.
    private const string EpisodeWord = @"episode|épisode|episodio|episódio|cap[ií]tulo|cap|folge|ep|серия|эпизод|aflevering|afl|odcinek|jakso|avsnitt|afsnit|episodul|episod|epizoda|epizód|rész|tập|bölüm|bolum|επεισόδιο|επεισοδιο|פרק|الحلقة|एपिसोड|화|회";
    private const string SeasonWord = @"season|saison|temporada|temp|tem|staffel|stagione|сезон|sezon|sezonul|seizoen|säsong|sæson|sesong|kausi|évad|musim|phần|mùa|시즌|κύκλος|κυκλος|σεζόν|עונה|الموسم|सीज़न";
    private const string Label = @"(?:" + EpisodeWord + @"|e)";
    // A lone "e" is a word in most languages, so it counts only when it touches its number: "E03", not "Kimi e - 24".
    private const string SpacedLabel = @"(?:" + EpisodeWord + @")";
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private static Regex Rx(string pattern) => new(pattern, Options, Limit);

    private static readonly Regex DashNumberFollows = Rx(@"^\s+-\s+\d{1,4}(?:v\d+)?(?:\s*$|\s+-)");
    private static readonly Regex EpisodeLabelFollows = Rx(@"^[\s._-]*(?:episode|ep|e)[ ._]*\d{1,4}(?![\p{L}\p{N}])");
    private static readonly Regex EndsWithYear = Rx(@"(?:^|[\s(])(?:19|20)\d{2}\)?$");
    private static readonly Regex[] SeasonEpisodeForms =
    [
        Rx(@"(?<![\p{L}\p{N}])S(?<s>\d{1,4})[ -]*(?:x[ -]*)?E(?:P(?:ISODE)?)?[ -]*" + Number),
        Rx(@"(?<![\p{L}\p{N}])[сc](?<s>\d{1,2})[ -]*[еэ]" + Number),
        Rx(@"(?<![\p{L}\p{N}])T(?<s>\d{1,2})[ -]*E" + Number),
        Rx(@"(?<![\p{L}\p{N}])S(?<s>\d{1,4})x" + Number),
        Rx(@"(?<![\p{L}\p{N}])s[ ](?<s>\d{1,2})[ ,-]*ep?[ .#-]*" + Number),
        Rx(@"(?<![\p{L}\p{N}])(?:episode|ep)[ .#]*(?<s>\d{1,2})[ .](?<n>\d{2})(?![\p{L}\p{N}])"),
        // "Cap.203": the Spanish release form for season 2, episode 3.
        Rx(@"(?<![\p{L}\p{N}])cap[ .]*(?<s>\d{1,2})(?<n>\d{2})(?![\p{L}\p{N}])"),
    ];
    private static readonly Regex CrossForm = Rx(@"(?:(?<![\p{L}\p{N}.])|(?<=\p{L}{3}))(?<s>\d{1,4})x" + Number + @"(?=$|[^\p{L}]|x\d)(?!\.\d)");
    private const string Ordinal = @"(?:\s?(?:st|nd|rd|th)|ª|º|a\.?|-?[йя])?";
    private static readonly Regex[] SeasonWordForms =
    [
        Rx(@"(?<![\p{L}\p{N}])(?:" + SeasonWord + @"|series|book|vol|volume)[ .#-]*(?<s>\d{1,2})[ ,.-]*(?:" + EpisodeWord + @"|e|chapter)[ .#:-]*" + Number),
        Rx(@"(?<![\p{L}\p{N}])" + Label + @"[ #:-]*" + Number + @"[ ,-]*(?:" + SeasonWord + @")[ -]*(?<s>\d{1,2})(?![\p{L}\p{N}])"),
        Rx(@"(?<![\p{L}\p{N}])(?<s>\d{1,2})" + Ordinal + @"[ .]*(?:" + SeasonWord + @")[ ,.-]*" + Number + Ordinal + @"[ .]*(?:сери[яий]|seriya|" + EpisodeWord + @")(?![\p{L}])"),
        Rx(@"(?<![\p{L}\p{N}])(?<s>\d{1,2})(?:st|nd|rd|th)?[ -]*season[ ,-]*" + Label + @"[ #:-]*" + Number),
    ];
    private static readonly Regex[] LabelForms =
    [
        Rx(@"(?<![\p{L}\p{N}])" + Number + @"\s*(?:серия|серии|сер\.?|эпизод|seriya|rész|화|회|bölüm)(?![\p{L}])"),
        // "Ep3of12", "03 of 12", "01 из 08".
        Rx(@"(?<![\p{L}\p{N}])(?:" + Label + @"[ .]*)?" + Number + @"\s*(?:of|из|iz)\s*\d{1,4}(?![\p{L}\p{N}])"),
        Rx(@"(?<![\p{L}\p{N}])" + SpacedLabel + @"[ #:.-]*" + Number + @"(?![\p{L}\p{N}]|\.\d)"),
        Rx(@"(?<![\p{L}\p{N}])e" + Number + @"(?![\p{L}\p{N}]|\.\d)"),
        Rx(@"(?<![\p{N}#])#\s*" + Number + @"(?![\p{L}\p{N}])"),
        Rx(@"第\s*" + Number + @"\s*[話话集回巻]"),
        Rx(@"(?<![\p{L}\p{N}])" + Number + @"\s*[話话]"),
    ];
    private static readonly Regex SeasonOnly = Rx(@"(?<![\p{L}\p{N}])(?:S(?<s>\d{1,2})(?:v\d+)?(?![\p{L}\p{N}])|(?:" + SeasonWord + @")(?!\s+-\s+\d{1,2}(?![\p{L}\p{N}])(?!\s+-\s+\d))[ .#-]*(?<s>\d{1,2})(?![\p{L}\p{N}])|(?!\d{1,2}[ .-]*(?:" + SeasonWord + @")[ .#-]*\d)(?<s>\d{1,2})" + Ordinal + @"[ .-]*(?:" + SeasonWord + @")(?![\p{L}])|TV-?(?<s>\d{1,2})(?![\p{L}\p{N}]))");
    private static readonly Regex Special = Rx(@"(?<![\p{L}\p{N}])(?:S\d{1,2})?(?:SP|OVA|OAV|OAD|ONA|Specials?)\)?[ #-]*(?:episode[ #-]*|ep[ .#-]*)?(?<n>\d{1,3})(?:v\d+)?(?:[+-](?<e>\d{1,3}))?(?![\p{L}\p{N}])");
    private static readonly Regex Film = Rx(@"(?<![\p{L}\p{N}])(?:(?:the[ -]+)?(?:movie|film)(?:[ #-]*\d{1,2})?|gekijou-?ban|劇場版)(?![\p{L}\p{N}])");
    private static readonly Regex ShortClip = new(@"(?<![\p{L}\p{N}])(?:NC[ -]?(?:OP|ED)|(?:S\d{1,2})?NC(?:OP|ED)|OP|ED|PV|CM|SPOT)[ -]?\d{0,2}(?:v\d+|[a-z])?(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex WordClip = Rx(@"(?<![\p{L}\p{N}])(?:(?:creditless|clean|textless|non-?credit)[ -]*(?:op|ed|opening|ending)?[ -]*\d{0,2}|opening(?:[ -]?(?:sequence|theme))?[ -]*\d{0,2}|ending(?:[ -]?(?:sequence|theme))?[ -]*\d{0,2}|closing[ -]?sequence|ova|oav|oad|ona|sp|specials?|preview[ -]*\d{0,2}|trailers?[ -]*\d{0,2}|teasers?[ -]*\d{0,2}|promos?[ -]*\d{0,2}|(?:bd |dvd |usa bd )?menu[ -]*\d{0,2}|commercials?|interviews?|art[ -]?gallery|gallery|storyboards?|outtakes|bloopers|featurette|music[ -]video|mv|extras?[ -]*\d{0,2}|bonus|recaps?[ -]*(?:\d{1,3}(?:-\d{1,3})?)?|summary|résumé|cm[ -]collection|pilot[ -]film|ncop|nced)(?![\p{L}\p{N}])");
    private static readonly Regex TitleEndingWord = Rx(@"^(?:ending|opening|gallery|specials?|interviews?|commercials?|summary|menu)$");
    private static readonly Regex TitleNumberedWord = Rx(@"^(?:gallery|interviews?|commercials?|summary)$");
    private static readonly Regex CjkClip = new(@"ノンクレジット|テロップ無し|映像特典|告知|特報|番宣|予告|メニュー|アイキャッチ|キャラクター設定|オープニング|エンディング|(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])PV|CM集", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex DateForward = new(@"(?<!\d)(?<y>19\d{2}|20\d{2})[ .\-](?<m>0[1-9]|1[0-2])[ .\-](?<d>0[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex DateBackward = new(@"(?<!\d)(?<d>0[1-9]|[12]\d|3[01])[ .\-](?<m>0[1-9]|1[0-2])[ .\-](?<y>19\d{2}|20\d{2})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex BareNumber = new(@"(?<![\p{L}\p{N}.,:'’])(?<!\d-)(?<n>\d{1,4})(?<half>\.\d)?(?<v>\s?v\d+)?(?:(?:[-+]|\s?~\s?)(?:e|ep)?(?<e>\d{1,4})(?<v2>v\d+)?)?(?![\p{L}\p{N}])(?!\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private const string MonthName = @"jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";
    private static readonly Regex DateWords = Rx(@"(?<![\p{L}\p{N}])(?:(?<d>\d{1,2})(?:st|nd|rd|th)?[ -](?<mon>" + MonthName + @")\.?,?[ -](?<y>(?:19|20)\d{2})|(?<mon>" + MonthName + @")\.?[ -](?<d>\d{1,2})(?:st|nd|rd|th)?,?[ -](?<y>(?:19|20)\d{2}))(?![\p{L}\p{N}])");
    private static readonly Regex DateCompact = new(@"(?<![\p{L}\p{N}])(?<y>19\d{2}|20\d{2})(?<m>0[1-9]|1[0-2])(?<d>0[1-9]|[12]\d|3[01])(?!\d{1,3}(?:[^\d]|$))(?:\d{4,8})?(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex DateAmerican = new(@"(?<![\p{L}\p{N}.])(?<m>0?[1-9]|1[0-2])[/.-](?<d>0?[1-9]|[12]\d|3[01])[/.-](?<y>\d{2})(?![\p{L}\p{N}.-])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex SeasonBeforeBracket = new(@"(?<=\s)(?<s>\d{1,2})\s*$", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex SeasonDash = Rx(@"(?<![\p{L}\p{N}])S(?<s>\d{1,2})-(?<n>\d{1,4})(?![\p{L}\p{N}])");
    private static readonly Regex GluedNumber = new(@"(?<=\p{L}{3})(?<n>\d{2,4})(?:-(?<e>\d{2,4}))?(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex YearToken = new(@"(?<![\p{L}\p{N}.])(?:19\d{2}|20\d{2})(?=г(?![\p{L}\p{N}])|(?![\p{L}\p{N}]))", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex PartWord = Rx(@"(?:^|[\s(\[-])(?:part|pt|vol|volume|chapter|ch|disc|disk|cd|book|file|act|arc|no|number|phase|level|round|часть|том|глава|выпуск|version|ver)\s*[.:#-]*$");
    private static readonly Regex GenreTail = Rx(@"\s-\s(?:comedy|action|drama|horror|thriller|romance|sci-?fi|fantasy|adventure|animation|crime|mystery|documentary|family)\b.*$");
    private static readonly Regex GenreWords = Rx(@"^[\s,]*(?:боевик|фантастика|ужасы|фэнтези|комедия|криминал|детектив[аы]?|драма|триллер|мелодрама|приключения|мультфильм|семейный|исторический|военный|биография|вестерн|спорт|документальный|год|г)(?:[\s,]+(?:боевик|фантастика|ужасы|фэнтези|комедия|криминал|детектив[аы]?|драма|триллер|мелодрама|приключения|мультфильм|семейный|исторический|военный|биография|вестерн|спорт|документальный|год|г))*[\s,]*$");

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    private static bool IsYear(int number) => number is >= 1900 and <= 2099;
    private static bool IsResolution(int number) => number is 480 or 576 or 720 or 1080 or 2160 or 4320;
    private static string Trim(string text) => text.Trim(' ', '-', '[', ']', '(', ')', ':', '~', ',');

    private static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) && !extension.Skip(1).All(char.IsDigit) ? name[..^extension.Length] : name;
    }

    internal static Result? Read(string path, bool seriesContext, int? folderSeason = null, bool specials = false) =>
        Analyze(path, seriesContext, folderSeason, specials).Numbering;

    // Whether a file in a folder called "2" or "Vol 2" has a number of its own, without that folder's help: a dated name needs
    // an explicit season or numbered files beside it, since "02 - Aliens (1986)" can be a place in a film series.
    internal static bool HasOwnNumber(string path)
    {
        var reading = Analyze(path, false);
        return reading.Numbering is { Episode: not null } number && (number.ExplicitSeason || reading.Year is null || FilenameFolders.Evidence(path, false, null, null).Ordered >= 2);
    }

    // One parse reads a name several times with the same evidence (extras checks, sibling lookups), so readings are kept until the folder changes.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, bool, int?, bool, string?, bool), (FilenameFolders.FolderEvidence Evidence, Reading Reading)> Readings = new();
    internal static Reading Analyze(string path, bool seriesContext, int? folderSeason = null, bool specials = false, string? libraryRoot = null, bool movieLibrary = false)
    {
        var evidence = FilenameFolders.Evidence(path, seriesContext, folderSeason, libraryRoot, movieLibrary);
        var key = (path, seriesContext, folderSeason, specials, libraryRoot, movieLibrary);
        if (Readings.TryGetValue(key, out var known) && ReferenceEquals(known.Evidence.Slots, evidence.Slots) && known.Evidence.Ordered == evidence.Ordered) return known.Reading;
        var reading = AnalyzeName(Stem(path), evidence, seriesContext || specials);
        // A failed reading may be down to a busy machine, so the next scan tries again.
        if (reading.Kind == UnreadKind) return reading;
        if (Readings.Count > 32768) Readings.Clear();
        Readings[key] = (evidence, reading);
        return reading;
    }

    // Disc structure names hold stream numbers, never episode numbers: 00002.m2ts, VTS_07_1.VOB.
    private static readonly Regex DiscFile = new(@"^(?:\d{5}|VTS[ _]\d{1,2}[ _]\d{1,2}|VIDEO[ _]TS)(?:\.vob)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    internal static bool IsDiscFile(string stem) => DiscFile.IsMatch(stem);

    internal static Reading AnalyzeName(string stem, FilenameFolders.FolderEvidence evidence, bool allowSpecials)
    {
        if (IsDiscFile(stem)) return new(stem, [], null, null, null, false, stem, false, "Disc");
        // The patterns have a time limit, which a busy machine can reach on any name.
        for (var attempt = 0; ; attempt++)
            try { return ReadName(stem, evidence, allowSpecials); }
            catch (RegexMatchTimeoutException) when (attempt < 2) { }
            // One name the reader can't make sense of must not stop a whole scan: the file is listed under its own name.
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException or OverflowException or InvalidOperationException or RegexMatchTimeoutException)
            {
                PlaybackDiagnostics.Record("filename-read-failed", new { error = ex.GetType().Name });
                return new(stem, [], null, null, null, false, stem, false, UnreadKind);
            }
    }
    private const string UnreadKind = "Unread";
    private static Reading ReadName(string stem, FilenameFolders.FolderEvidence evidence, bool allowSpecials)
    {
        var scan = new FilenameScan(stem);
        var hypotheses = Hypotheses(scan, evidence, allowSpecials);
        foreach (var hypothesis in hypotheses)
        {
            FilenameFeatures.Add(hypothesis, scan, evidence, hypotheses);
            hypothesis.Score = FilenameModel.Score(hypothesis.Features);
        }
        return Interpret(hypotheses.MaxBy(h => h.Score)!, scan, evidence);
    }

    // For model training: every reading the name allows, with its features.
    internal static IReadOnlyList<Hypothesis> Enumerate(string stem, FilenameFolders.FolderEvidence evidence, bool allowSpecials)
    {
        var scan = new FilenameScan(stem);
        var hypotheses = Hypotheses(scan, evidence, allowSpecials);
        foreach (var hypothesis in hypotheses) { FilenameFeatures.Add(hypothesis, scan, evidence, hypotheses); hypothesis.Score = FilenameModel.Score(hypothesis.Features); }
        return hypotheses;
    }

    private static List<Hypothesis> Hypotheses(FilenameScan scan, FilenameFolders.FolderEvidence evidence, bool allowSpecials)
    {
        var text = scan.Text; var results = new List<Hypothesis> { new() { Form = Form.None, Start = scan.CoreEnd, End = scan.CoreEnd } };
        var taken = new bool[text.Length + 1];
        void Take(int start, int end) { for (var i = start; i < end && i < taken.Length; i++) taken[i] = true; }
        bool Free(int start, int end) { for (var i = start; i < end && i < taken.Length; i++) if (taken[i]) return false; return true; }
        bool Clear(int index) => index >= text.Length || !scan.Technical[index];

        void Compound(Match match, Form form)
        {
            if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length)) return;
            var season = match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value) : (int?)null;
            var chain = Continue(text, match.Index + match.Length, int.Parse(match.Groups["n"].Value), season ?? 1);
            results.Add(new()
            {
                Form = form, Start = match.Index, End = chain.Position, Season = season, Episode = int.Parse(match.Groups["n"].Value), EpisodeEnd = chain.End,
                Explicit = season is not null, Digits = match.Groups["n"].Value, Conflict = chain.Conflict
            });
            Take(match.Index, chain.Position);
        }
        foreach (var pattern in SeasonEpisodeForms) foreach (Match match in pattern.Matches(text)) Compound(match, Form.SeasonEpisode);
        foreach (var pattern in SeasonWordForms) foreach (Match match in pattern.Matches(text)) Compound(match, Form.SeasonWords);
        foreach (Match match in CrossForm.Matches(text))
        {
            // "3x3 Eyes" opens a title; it isn't season 3, episode 3.
            if (match.Index <= scan.CoreStart && match.Groups["n"].Length == 1 && scan.Slice(match.Index + match.Length, scan.CoreEnd).Length > 0) continue;
            Compound(match, Form.CrossForm);
        }

        // "Special 26 (2013)", "Special.26.1080p": a name opening with the word has no episode number, so it's a film, or a clip beside a show's episodes.
        var titleNumbers = new HashSet<int>();
        if (!evidence.Series)
            foreach (Match match in Special.Matches(text))
                if (match.Index == scan.CoreStart && match.Value.StartsWith("special", StringComparison.OrdinalIgnoreCase)) titleNumbers.Add(match.Groups["n"].Index);
        if (allowSpecials)
            foreach (Match match in Special.Matches(text))
            {
                if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length) || match.Index < scan.CoreStart || match.Index >= scan.CoreEnd) continue;
                if (titleNumbers.Contains(match.Groups["n"].Index)) continue;
                var first = int.Parse(match.Groups["n"].Value);
                var last = match.Groups["e"].Success ? int.Parse(match.Groups["e"].Value) : (int?)null;
                results.Add(new() { Form = Form.Special, Start = match.Index, End = match.Index + match.Length, Season = 0, Episode = first, EpisodeEnd = last > first ? last : null, Explicit = true, Digits = match.Groups["n"].Value });
                Take(match.Index, match.Index + match.Length);
            }

        // A clip word names the rest of the file: "Show - NCOP1", "Show - Preview 02", "[Art Gallery] Show", "Show - BD-Box Promo (BDRip)".
        var clipLimit = scan.CoreEnd;
        while (clipLimit < text.Length && clipLimit - scan.CoreEnd < 14 && text[clipLimit] != '[' && text[clipLimit] != '(') clipLimit++;
        void AddClip(Match match, bool isShort)
        {
            var bracket = scan.BracketAt(match.Index);
            var leadingTag = bracket is { } b && b.End <= scan.CoreStart;
            if (bracket is { } tag && scan.IsSeriesTag(tag)) return;
            if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length) || match.Index >= clipLimit && !leadingTag || match.Index < scan.CoreStart && !leadingTag) return;
            // "Night Gallery E05": an episode label after the word says it's part of the show's name.
            if (!isShort && !leadingTag && EpisodeLabelFollows.IsMatch(text[(match.Index + match.Length)..Math.Max(match.Index + match.Length, Math.Min(scan.CoreEnd, text.Length))])) return;
            // "Night Gallery - 3", "Bonus Family 3": a word in the show's name isn't a clip, when the words up to the next number name the
            // folder, or that number runs across the files and the word opens a longer name, or the word comes later in the name and a dash
            // sets the number apart. "Bonus 3" beside a show's episodes, "Show - Art Gallery 3" and "Show Movie Teaser 3" are numbered clips.
            // "Night Gallery - S01E01": a season and episode marker after the word says it's part of the show's name.
            if (!isShort && !leadingTag && results.Any(r => r.Form is Form.SeasonEpisode or Form.CrossForm or Form.SeasonWords && r.Start > match.Index)) return;
            // The pattern can take the number along: "Extras - 01" in a folder called Extras.
            if (!isShort && !leadingTag && bracket is null && match.Index >= scan.CoreStart && match.Value.IndexOfAny("0123456789".ToCharArray()) is > 0 and var own && evidence.NamesFolder(scan.Slice(scan.CoreStart, match.Index + own))) return;
            if (!isShort && !leadingTag && bracket is null && match.Index >= scan.CoreStart && !match.Value.Any(char.IsDigit))
            {
                var from = match.Index + match.Length;
                var nextNumber = Regex.Match(text[from..Math.Max(from, Math.Min(scan.CoreEnd, text.Length))], @"(?<![\p{L}\p{N}])\d{1,4}(?![\p{L}\p{N}])", RegexOptions.None, Limit);
                var at = from + nextNumber.Index;
                // "Night Gallery 3": a word that doesn't usually number clips, straight before a number, unless the words before it name the folder.
                if (nextNumber.Success && match.Index > scan.CoreStart && TitleNumberedWord.IsMatch(match.Value.Trim()) && text[from..at].Trim().Length == 0 && !SetApartByDash(text, match.Index) && scan.Slice(scan.CoreStart, match.Index) is var words && !SeasonOnly.IsMatch(words) && !evidence.NamesFolder(words)) return;
                if (nextNumber.Success && (evidence.NamesFolder(scan.Slice(scan.CoreStart, at)) || evidence.Varies(at) && !SetApartByDash(text, match.Index) && match.Index == scan.CoreStart && text[from..at].Any(char.IsLetter) ||
                    !SetApartByDash(text, match.Index) && match.Index > scan.CoreStart && text[from..at].TrimEnd().EndsWith('-'))) return;
            }
            // "Opening Night", "Interview with the Vampire", "The Menu (2022)": outside a show's location, a clip word that opens a
            // plain name is part of its title. Beside a show's episodes the file still joins them as a clip.
            if (!isShort && !evidence.Series && !leadingTag && bracket is null && match.Index >= scan.CoreStart)
            {
                var before = scan.Slice(scan.CoreStart, match.Index); var rest = scan.Slice(match.Index + match.Length, scan.CoreEnd);
                var article = Regex.IsMatch(before, @"(?i)^(?:the|a|an)$", RegexOptions.None, Limit);
                var joined = match.Index + match.Length < text.Length && text[match.Index + match.Length] == '-';
                // "Opening 01 - Song" numbers a clip.
                var numbered = match.Value.Any(char.IsDigit) || rest.Length > 0 && char.IsDigit(rest[0]) && !EndsWithYear.IsMatch(rest);
                if (!joined && (article || !numbered && before.Length == 0 && (EndsWithYear.IsMatch(rest) || rest.Any(char.IsLetter)))) return;
                // "Hollywood Ending (2002)", "National Gallery": one closing a plain name is part of its title, unless the words before it name the folder.
                var plain = before.Length > 0 && !joined && !SetApartByDash(text, match.Index) && !SeasonOnly.IsMatch(before) && !evidence.NamesFolder(before);
                if (plain && TitleEndingWord.IsMatch(match.Value.Trim()) && OnlyYear(rest)) return;
            }
            results.Add(new() { Form = Form.Clip, Start = match.Index, End = match.Index + match.Length, Clip = Trim(match.Value), ShortClip = isShort, LeadingTag = leadingTag });
            Take(match.Index, match.Index + match.Length);
        }
        // Only the first clip word in a name names the clip: "Show - PV and CM collection 01".
        var clips = ShortClip.Matches(text).Select(m => (Match: m, Short: true)).Concat(WordClip.Matches(text).Concat(CjkClip.Matches(text)).Select(m => (Match: m, Short: false)))
            .OrderBy(c => c.Match.Index).ToArray();
        foreach (var (match, isShort) in clips)
        {
            var before = results.Count;
            AddClip(match, isShort);
            if (results.Count > before) break;
        }

        foreach (Match match in Film.Matches(text))
        {
            if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length) || match.Index < scan.CoreStart) continue;
            if (scan.BracketAt(match.Index) is { } holder && text[(holder.Start + 1)..(holder.End - 1)].Trim().Length > match.Length) continue;
            // "Film Socialisme", "The Last Movie Star": the plain word with more words straight after it is part of the title.
            // "Show the Movie Subtitle" and "Show - Movie Box" name a film of the show, and "Movie 01 Two Heroes" numbers one.
            var afterFilm = scan.Slice(match.Index + match.Length, scan.CoreEnd);
            var plainWord = match.Index == scan.CoreStart || !match.Value.StartsWith("the", StringComparison.OrdinalIgnoreCase) && !SetApartByDash(text, match.Index);
            if (plainWord && scan.BracketAt(match.Index) is null && !match.Value.StartsWith("geki", StringComparison.OrdinalIgnoreCase) && !match.Value.StartsWith('劇') && !match.Value.Any(char.IsDigit) && afterFilm.Length > 0 && char.IsLetter(afterFilm[0]) && !text[(match.Index + match.Length)..].TrimStart().StartsWith('-')) continue;
            results.Add(new() { Form = Form.Film, Start = match.Index, End = match.Index + match.Length });
            Take(match.Index, match.Index + match.Length);
        }
        if (!evidence.Movies) foreach (var pattern in new[] { DateForward, DateBackward, DateWords, DateCompact, DateAmerican })
            foreach (Match match in pattern.Matches(text))
            {
                if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length) || match.Index < scan.CoreStart || match.Index >= scan.CoreEnd) continue;
                var month = match.Groups["mon"].Success ? Array.IndexOf(Months, match.Groups["mon"].Value[..3].ToLowerInvariant()) + 1 : int.Parse(match.Groups["m"].Value);
                var century = match.Groups["y"].Length == 2 ? (int.Parse(match.Groups["y"].Value) > 40 ? "19" : "20") : "";
                if (!DateOnly.TryParseExact($"{century}{match.Groups["y"]}-{month:00}-{int.Parse(match.Groups["d"].Value):00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                results.Add(new() { Form = Form.Date, Start = match.Index, End = match.Index + match.Length, AirDate = date.ToString("yyyy-MM-dd"), Digits = pattern == DateWords ? "words" : pattern == DateCompact ? "compact" : pattern == DateAmerican ? "short" : null });
                Take(match.Index, match.Index + match.Length);
            }

        foreach (var pattern in LabelForms)
            foreach (Match match in pattern.Matches(text))
            {
                if (!Clear(match.Index) || !Free(match.Index, match.Index + match.Length)) continue;
                var value = int.Parse(match.Groups["n"].Value);
                var chain = Continue(text, match.Index + match.Length, value, 1);
                results.Add(new() { Form = Form.Label, Start = match.Index, End = chain.Position, Episode = value, EpisodeEnd = chain.End, Digits = match.Groups["n"].Value, Conflict = chain.Conflict });
                Take(match.Index, chain.Position);
            }

        var seasons = new List<(int Start, int End, int Season)>();
        foreach (Match match in SeasonOnly.Matches(text))
            if (Clear(match.Index) && Free(match.Index, match.Index + match.Length)) { seasons.Add((match.Index, match.Index + match.Length, int.Parse(match.Groups["s"].Value))); Take(match.Index, match.Index + match.Length); }
        // "Ranma S02 (2025) - E05": a season marker before a labelled episode belongs to it.
        foreach (var labelled in results.Where(r => r.Form == Form.Label))
            if (seasons.LastOrDefault(s => s.End <= labelled.Start) is { End: > 0 } season) { labelled.Season = season.Season; labelled.SeasonStart = season.Start; labelled.Explicit = true; }
            // "Show Ep03 (Season 2)", "Show Episode 03 [S02]".
            else if (seasons.FirstOrDefault(s => s.Start >= labelled.End && s.Start - labelled.End <= 3) is { End: > 0 } later) { labelled.Season = later.Season; labelled.Explicit = true; labelled.SeasonAfterEnd = later.End; }
        // "Show.03.(01.seriya.iz.08)": the number right before a bracketed episode label is the season.
        foreach (var labelled in results.Where(r => r.Form == Form.Label && r.Season is null))
            if (scan.BracketAt(labelled.Start) is { Open: '(' } holder && holder.Start > scan.CoreStart && SeasonBeforeBracket.Match(text[scan.CoreStart..holder.Start]) is { Success: true } before && !IsYear(int.Parse(before.Groups["s"].Value)))
            { labelled.Season = int.Parse(before.Groups["s"].Value); labelled.SeasonStart = scan.CoreStart + before.Index; labelled.Explicit = true; }
        // "Show S3 - OVA1" and "Show S6 NCOP" keep the season marker out of the title.
        foreach (var named in results.Where(r => r.Form is Form.Special or Form.Clip))
            if (seasons.LastOrDefault(s => s.End <= named.Start) is { End: > 0 } season) named.SeasonStart = season.Start;

        // Every other number could be the episode. Season markers are linked to a number that comes after them.
        foreach (Match match in BareNumber.Matches(text))
        {
            if (match.Index < scan.CoreStart || match.Index >= scan.CoreEnd || !Clear(match.Index) || !Free(match.Index, match.Index + match.Length)) continue;
            if (titleNumbers.Contains(match.Groups["n"].Index)) continue;
            var digits = match.Groups["n"].Value; var value = int.Parse(digits);
            // A bare year is never an episode number.
            if (IsYear(value) && !match.Groups["e"].Success) continue;
            // Anything in a film location stays a film unless a dash sets its number apart.
            if (evidence.Movies && !text[..match.Index].TrimEnd().EndsWith('-')) continue;
            // "Apollo 13", "Area 51 (2015)", "The 39 Steps", "12 Monkeys", "300": outside a TV location, a number in a plain name belongs
            // to the title, unless sibling files or the folder's name say it's running or a dash sets it apart.
            if (!evidence.Series && !match.Groups["e"].Success && !match.Groups["v"].Success && !scan.LeadingGroup && scan.BracketAt(match.Index) is null
                && !SetApartByDash(text, match.Index) && !seasons.Any(s => s.End <= match.Index) && !evidence.NamesFolder(scan.Slice(scan.CoreStart, match.Index)) && TitleNumber(scan, match, digits, evidence)) continue;
            // "Brooklyn 99 - 5", "86 - 5": an unpadded number before one that a dash sets apart is the show's own.
            if (digits[0] != '0' && !match.Groups["e"].Success && !evidence.Varies(match.Index) && !SetApartByDash(text, match.Index) && DashNumberFollows.IsMatch(text[(match.Index + match.Length)..Math.Max(match.Index + match.Length, Math.Min(scan.CoreEnd, text.Length))])) continue;
            // "Brooklyn 99 1" to "Brooklyn 99 9": a number too big for a season, the same in every file, while a later one runs.
            if (value > 40 && evidence.Constant(match.Index) && evidence.Slots!.Any(slot => slot.Value && slot.Key > match.Index && slot.Key <= text.Length && text[(match.Index + match.Length)..slot.Key].All(c => c is ' ' or '.' or '_'))) continue;
            // "Room 104 5" in a folder called Room 104. A padded number, or one opening the name or set apart, is an episode with a part in its title: "04 - Part 2" in "Lost 4".
            if (digits[0] != '0' && match.Index > scan.CoreStart && !SetApartByDash(text, match.Index) && value != evidence.FolderSeason && !evidence.Varies(match.Index) && evidence.TitleHas(digits) && Regex.IsMatch(scan.Slice(match.Index + match.Length, scan.CoreEnd), @"(?<![\p{L}\p{N}])\d{1,4}(?![\p{L}\p{N}])", RegexOptions.None, Limit)) continue;
            var half = match.Groups["half"].Success && match.Groups["half"].Value != ".0";
            int? last = match.Groups["e"].Success ? int.Parse(match.Groups["e"].Value) : null;
            var season = seasons.LastOrDefault(s => s.End <= match.Index);
            var bare = new Hypothesis
            {
                Form = Form.Bare, Start = match.Index, End = match.Index + match.Length, Episode = value, Digits = digits, Half = half, Versioned = match.Groups["v"].Success,
                Ranged = last is not null, EpisodeEnd = last > value && last - value < 100 ? last : null, Season = seasons.Count > 0 ? season.Season : null,
                SeasonStart = seasons.Count > 0 && season != default ? season.Start : -1, Explicit = seasons.Count > 0 && season != default
            };
            if (bare.SeasonStart < 0) { bare.Season = null; }
            // A second number that can't end the range is a number of its own: "afo-rage-0204-720", "Show 2-01". An explicit tilde stays a bad range.
            if (last is not null && (last <= value || last - value >= 100))
            {
                if (last > value && !IsResolution(last.Value)) bare.Conflict = InvalidRange;
                else { bare.End = match.Index + digits.Length + match.Groups["v"].Length; bare.Ranged = false; last = null; }
            }
            // "Show - 01 - 12 [1080p]": a spaced range with nothing after it.
            if (last is null && Regex.Match(text[bare.End..Math.Max(bare.End, Math.Min(text.Length, scan.CoreEnd))], @"^\s-\s(?<e>\d{2,4})\s*$", RegexOptions.None, Limit) is { Success: true } spaced && int.Parse(spaced.Groups["e"].Value) is var batchEnd && batchEnd > value && batchEnd - value < 100)
                results.Add(new() { Form = Form.Bare, Start = bare.Start, End = bare.End + spaced.Length, Episode = value, EpisodeEnd = batchEnd, Digits = digits, Ranged = true, Season = bare.Season, SeasonStart = bare.SeasonStart, Explicit = bare.Explicit });
            results.Add(bare);
            // 0104 and 203 can also be season and episode.
            if (digits.Length is 3 or 4 && !IsYear(value) && last is null && int.Parse(digits[^2..]) > 0 && int.Parse(digits[..^2]) is >= 0 and <= 40
                && !evidence.PlainRun(match.Index) && !evidence.TitleHas(digits) && (evidence.FolderSeason is not { } holder || holder == int.Parse(digits[..^2]))
                // Three digits need a title before them and siblings that say so, or the folder for that season: "Show 203" in season 2.
                // "Show - 203" and a bare "203" count up.
                && (digits.Length == 4 || match.Index > scan.CoreStart && !SetApartByDash(text, match.Index) && (evidence.PackedRun(match.Index) || evidence.FolderSeason == int.Parse(digits[..^2]))))
                results.Add(new() { Form = Form.Compact, Start = match.Index, End = bare.End, Season = int.Parse(digits[..^2]), Episode = int.Parse(digits[^2..]), Explicit = true, Digits = digits });
        }
        // "4-13 Title", "Show 03-20": a span too long for one file reads as season and episode.
        foreach (var ranged in results.Where(r => r.Form == Form.Bare && r.Ranged && r.Episode is <= 40 && r.EpisodeEnd is not null).ToArray())
            if (Regex.IsMatch(text[ranged.Start..ranged.End], @"^\d{1,2}-\d{2,3}$", RegexOptions.None, Limit))
                results.Add(new() { Form = Form.Pair, Start = ranged.Start, End = ranged.End, Season = ranged.Episode, Episode = ranged.EpisodeEnd, Explicit = true, Digits = ranged.Digits, Ranged = true });
        // "(S4-24)", "[S2-07]": a season marker joined to a number by a hyphen.
        foreach (Match match in SeasonDash.Matches(text))
            if (Clear(match.Index) && Free(match.Index, match.Index + match.Length))
                results.Add(new() { Form = Form.Pair, Start = match.Index, End = match.Index + match.Length, Season = int.Parse(match.Groups["s"].Value), Episode = int.Parse(match.Groups["n"].Value), Explicit = true, Digits = match.Groups["n"].Value, Glued = true });
        // "Show 2.03", "Show [2][03]", "Show (2).03": two numbers side by side can be season and episode.
        var numbers = results.Where(r => r.Form == Form.Bare && !r.Half && !r.Ranged).OrderBy(r => r.Start).ToArray();
        for (var i = 0; i + 1 < numbers.Length; i++)
        {
            var first = numbers[i]; var second = numbers[i + 1];
            // "Kaiju No. 8 05" in a folder called Kaiju No. 8: the show's own number isn't its season.
            if (first.Digits is { } own && first.Episode != evidence.FolderSeason && evidence.TitleHas(own)) continue;
            // "Lost 4 10" beside "Lost 4 1": the second number counts up, so the first isn't its season.
            if (evidence.PlainRun(second.Start)) continue;
            if (second.Start - first.End > 3 || first.Episode is null or > 40 || first.Episode == 0 && (first.Digits?.Length ?? 0) > 2 || second.Digits?.Length is not (2 or 3) || !Regex.IsMatch(text[first.End..second.Start], @"^[\])\s.\[(-]+$", RegexOptions.None, Limit) || Regex.IsMatch(text[first.End..second.Start], @"\s-\s", RegexOptions.None, Limit)) continue;
            results.Add(new() { Form = Form.Pair, Start = first.Start, End = second.End, Season = first.Episode, Episode = second.Episode, Explicit = true, Digits = second.Digits, DottedPair = scan.Spaced && second.Start - first.End == 1 && scan.Original[first.End] == '.' });
        }
        // "Bleach73-74": a number glued to a word, for names with no other number to go on.
        if (!results.Any(r => r.Form is Form.Bare or Form.SeasonEpisode or Form.CrossForm or Form.SeasonWords or Form.Label))
            foreach (Match match in GluedNumber.Matches(text))
            {
                if (match.Index < scan.CoreStart || match.Index >= scan.CoreEnd || !Clear(match.Index) || !Free(match.Index, match.Index + match.Length)) continue;
                var value = int.Parse(match.Groups["n"].Value); int? last = match.Groups["e"].Success ? int.Parse(match.Groups["e"].Value) : null;
                results.Add(new() { Form = Form.Bare, Start = match.Index, End = match.Index + match.Length, Episode = value, Digits = match.Groups["n"].Value, Glued = true, Ranged = last is not null, EpisodeEnd = last > value ? last : null });
            }
        // A season marker with nothing numbered after it still shapes the title of a season pack.
        // "Calm Season 2 (2008)": outside a show's location, a season's word with only a year after it is part of a plain title.
        if (seasons.Count > 0 && !(!evidence.Series && SeasonWordInTitle(scan, seasons[0].Start, seasons[0].End)))
        {
            var none = results[0];
            none.Season = seasons[0].Season; none.SeasonStart = seasons[0].Start;
        }
        return results;
    }

    private static (int? End, int Position, string? Conflict) Continue(string name, int position, int start, int season)
    {
        int? end = null; string? conflict = null;
        while (position < name.Length)
        {
            var tail = name[position..];
            var next = Pattern(@"^\s*(?<dash>-)?(?<gap>\s*)(?<marker>S(?<s>\d{1,2})[ -]*E|(?<s>\d{1,2})x|" + Label + @"|x)?\s*" + Number + @"(?=\b|E\d|x\d)").Match(tail);
            if (!next.Success || !next.Groups["dash"].Success && !Pattern(@"^\s*(?:e(?:p(?:isode)?)?|x|s\d{1,2}[ -]*e|\d{1,2}x)\d").IsMatch(tail)) break;
            // "S03E07 - 42" and "S01E05 - 24 Hours" are titles; a bare range end joins its dash ("E07-08", "- 02-04").
            if (next.Groups["dash"].Success && next.Groups["gap"].Length > 0 && !next.Groups["marker"].Success) break;
            var finish = int.Parse(next.Groups["n"].Value);
            if (IsYear(finish) || IsResolution(finish)) break;
            if (next.Groups["s"].Success && int.Parse(next.Groups["s"].Value) != season) conflict = CrossSeasonRange;
            else if (finish <= (end ?? start) || finish - start >= 100 || !next.Groups["dash"].Success && finish != (end ?? start) + 1) conflict = InvalidRange;
            else end = finish;
            position += next.Length;
        }
        return (conflict is null ? end : null, position, conflict);
    }

    private static bool TitleNumber(FilenameScan scan, Match match, string digits, FilenameFolders.FolderEvidence evidence)
    {
        var after = match.Index + match.Length;
        var rest = scan.Slice(after, scan.CoreEnd);
        var opens = match.Index == scan.CoreStart;
        // "12 Monkeys" and "13 Hours" side by side are two films; a show's episodes named this way take more than two files.
        // "Toy Story 2" to "Toy Story 4" are sequels; episodes numbered like this run longer. "Part 2" has no title for the number to belong to.
        if (evidence.Varies(match.Index) && !(opens && rest.Length > 0 && digits[0] != '0' && evidence.Ordered < 3) && !(!opens && rest.Length == 0 && digits[0] != '0' && evidence.Ordered < 6 && !FilenameExtras.IsPart(scan.Slice(scan.CoreStart, match.Index)))) return false;
        // "02 - Aliens (1986)": a padded number opening a dated name is a place in a collection. "01.Show.2026.WEBRip" is an episode.
        if (digits[0] == '0') return opens && !scan.HasTail && EndsWithYear.IsMatch(rest);
        // "1. Name" numbers a list.
        if (opens && scan.Spaced && after < scan.Original.Length && scan.Original[after] == '.') return false;
        // "Show.2.03" is a pair of numbers, not a title ending in one.
        if (rest.Length > 0 && char.IsDigit(rest[0]) && !EndsWithYear.IsMatch(rest)) return false;
        // "Show 5 - Name" sets its name apart, so the number before it isn't part of it.
        if (scan.Text[after..Math.Min(scan.CoreEnd, scan.Text.Length)].TrimStart(' ', '.', '_').StartsWith('-')) return false;
        // A file that's only a number can be an episode of the show its folder names, up to the numbers titles rarely are.
        return rest.Length > 0 || !opens || !evidence.HasFolder || evidence.OwnFolder || digits.Length >= 3;
    }
    private static bool SeasonWordInTitle(FilenameScan scan, int start, int end) =>
        start > scan.CoreStart && scan.Text[start..].TakeWhile(char.IsLetter).Count() > 2 && scan.BracketAt(start) is null && !SetApartByDash(scan.Text, start) &&
        (end >= scan.CoreEnd || OnlyYear(scan.Slice(end, scan.CoreEnd)));
    private static bool OnlyYear(string rest) => rest.Length == 0 || EndsWithYear.IsMatch(rest) && rest.Trim('(', ')', ' ').Length == 4;
    // "Show - 5" sets the number apart. "Catch-22" joins it to the word before.
    private static bool SetApartByDash(string text, int index)
    {
        if (!text[..index].TrimEnd().EndsWith('-')) return false;
        return !(index >= 2 && text[index - 1] == '-' && char.IsLetterOrDigit(text[index - 2]));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns = new();
    private static Regex Pattern(string pattern) => Patterns.GetOrAdd(pattern, p => new Regex(p, Options, Limit));

    private static Reading Interpret(Hypothesis best, FilenameScan scan, FilenameFolders.FolderEvidence evidence)
    {
        var (title, alternates, year) = ReadTitle(scan, best, evidence);
        var subtitle = best.Form is Form.Bare or Form.SeasonEpisode or Form.CrossForm or Form.SeasonWords or Form.Label or Form.Date or Form.Compact or Form.Pair or Form.Special
            ? Subtitle(scan.Slice(Math.Max(best.End, best.SeasonAfterEnd), scan.CoreEnd)) : null;
        Result? numbering = null;
        string? clip = null;
        switch (best.Form)
        {
            case Form.Bare when best.Half:
                // "27.5 Special" sits between episodes: a clip of the show, not a numbered episode.
                clip = Trim(scan.Slice(best.Start, scan.CoreEnd));
                break;
            case Form.Clip:
                clip = Trim(scan.Slice(best.Start, scan.CoreEnd));
                if (clip.Length == 0) clip = best.Clip;
                break;
            case Form.None: case Form.Film:
                break;
            case Form.Date:
                numbering = new(title, null, null, Subtitle: subtitle, AirDate: best.AirDate);
                break;
            default:
                numbering = new(title, best.Season ?? 1, best.Episode, best.EpisodeEnd, best.Explicit, subtitle, Conflict: best.Conflict);
                break;
        }
        // "01.Show Name.2026.WEBRip": with no words before the number, the words after it stop at the year.
        if (numbering is { Subtitle: { } words } && best.Form == Form.Bare && title.Length == 0 && YearToken.Match(words) is { Success: true, Index: > 0 } cut) numbering = numbering with { Subtitle = Subtitle(words[..cut.Index]) };
        // "[S02E03] Harbor of Glass" with nothing else to go on: the words after the marker are all there is to name it.
        if (numbering is not null && best.Form != Form.Bare && title.Length == 0 && subtitle is not null && !evidence.HasFolder) { title = subtitle; numbering = numbering with { Series = title, Subtitle = null }; }
        if (numbering is { Subtitle: { } remaining }) numbering = numbering with { Subtitle = Subtitle(Regex.Replace(remaining, @"\[[^\]]*\]?", " ", RegexOptions.None, Limit)) };
        // "Show - 05 [SubsPlease]": a release group's tag left on its own names nothing.
        var tagFrom = Math.Min(Math.Max(best.End, best.SeasonAfterEnd), scan.Original.Length);
        var tagOnly = Regex.IsMatch(scan.Original[tagFrom..Math.Max(tagFrom, Math.Min(scan.CoreEnd, scan.Original.Length))].Trim(' ', '-', '.', '_'), @"^\[[^\]]*\]$", RegexOptions.None, Limit);
        if (numbering is { Subtitle: { } after }) numbering = numbering with { Name = tagOnly ? null : EpisodeName(after, scan) };
        var cleaned = Regex.Replace(Regex.Replace(scan.Slice(scan.CoreStart, scan.CoreEnd), @"\[[^\]]*\]?", " "), @"\s+", " ").Trim();
        if (cleaned.Length == 0) cleaned = Regex.Replace(scan.Text, @"\s+", " ").Trim();
        return new(title, alternates, year, numbering, clip, best.Form == Form.Film, cleaned, scan.LeadingGroup, best.Form.ToString());
    }

    private static (string Title, string[] Alternates, int? Year) ReadTitle(FilenameScan scan, Hypothesis best, FilenameFolders.FolderEvidence evidence)
    {
        var start = scan.CoreStart;
        var numbered = best.Form is not (Form.None or Form.Film);
        var stop = best.Form switch { Form.None => scan.CoreEnd, _ => best.Start };
        if (best.LeadingTag) stop = scan.CoreEnd;
        stop = Math.Min(stop, Math.Max(scan.CoreEnd, start));
        if (best.SeasonStart >= 0 && best.SeasonStart >= start && best.SeasonStart < stop) stop = best.SeasonStart;
        stop = Math.Max(stop, start);
        if (numbered && scan.BracketAt(stop) is { } enclosing && enclosing.Start >= start && enclosing.Start < stop) stop = enclosing.Start;
        var alternates = new List<string>();
        int? year = null;
        // A film with a quoted title: 5ACV13-16 «Futurama Into The Wild Green Yonder» (2008).
        var quoted = Regex.Match(scan.Text[start..Math.Max(start, scan.CoreEnd)], "\"([^\"]{3,})\"", RegexOptions.None, Limit);
        if (quoted.Success && !numbered && Regex.IsMatch(scan.Text[start..(start + quoted.Index)], @"^[\p{Lu}\d\s-]*$", RegexOptions.None, Limit)) { start += quoted.Groups[1].Index; stop = start + quoted.Groups[1].Length; if (FindYear(scan, stop, scan.CoreEnd) is { } later) year = later.Year; }
        // Years: the release year is the last one that isn't a span continuation; "Blade Runner 2049 2017" keeps 2049 in the title.
        if (year is null && !numbered && best.Form == Form.None && FindYear(scan, start, scan.CoreEnd) is null && scan.HasTail && scan.CoreEnd < scan.Text.Length && YearToken.Match(scan.Text[scan.CoreEnd..]) is { Success: true } past && !scan.Technical[scan.CoreEnd + past.Index] && scan.BracketAt(scan.CoreEnd + past.Index) is null) year = int.Parse(past.Value);
        // "Show S02 (2025) - E05": a year between the season marker and the episode still dates the show.
        if (year is null && numbered && best.SeasonStart >= 0 && FindYear(scan, start, Math.Min(best.Start, scan.CoreEnd)) is { } between) year = between.Year;
        // "OVA-01.2007", "Show OVA (1990)", "01.Show.2026.WEBRip": the year follows a marker that opens or closes the name.
        if (year is null && numbered && best.Form is not Form.Date && (best.Form is Form.Special or Form.Clip || string.IsNullOrWhiteSpace(scan.Slice(start, best.Start))) && best.End < scan.CoreEnd && YearToken.Match(scan.Text[best.End..scan.CoreEnd]) is { Success: true } after && !scan.Technical[best.End + after.Index] && (best.Form is Form.Special or Form.Clip || scan.BracketAt(best.End + after.Index) is null)) year = int.Parse(after.Value);
        if (year is null && FindYear(scan, start, Math.Max(stop, numbered ? stop : scan.CoreEnd)) is { } found)
        {
            year = found.Year;
            if (!numbered && found.Start < stop) { stop = found.Start; if (found.Alternate is { } other) alternates.Add(other); }
            else if (found.Start < stop && found.Start > start) stop = TrimYearTail(scan, start, stop, found);
        }
        if (year is null) foreach (var bracket in scan.Brackets.Where(b => !numbered || b.Start < stop))
            if (bracket.Start >= scan.CoreStart && YearToken.Match(scan.Text[bracket.Start..bracket.End]) is { Success: true } inside) { year = int.Parse(inside.Value); break; }
        var title = FormatTitle(scan, start, stop, alternates);
        // "Show - 107a - Subtitle": half an episode ends the title too.
        if (!numbered) title = Regex.Replace(title, @"\s+-\s+\d{1,4}[a-zA-Z](?:\s+-.*)?$", "", RegexOptions.None, Limit);
        // "X-Men Complete 13 Movie Collection": the set's description isn't part of the title.
        if (!numbered) title = Regex.Replace(title, @"(?i)\s+complete\s+\d.*$", "", RegexOptions.None, Limit);
        // "Show - XX - Original Opening Sequence": XX stands in for a number.
        if (!numbered) title = Regex.Replace(title, @"\s+-\s+XX(?:\s+-.*)?$", "", RegexOptions.None, Limit);
        // "afo-rage-0204-720-web": scene names can open with their group.
        if (!scan.Spaced && Regex.Match(title, @"^[a-z0-9]{2,5}-(?<rest>[a-z].{7,})$", RegexOptions.None, Limit) is { Success: true } scene) alternates.Add(scene.Groups["rest"].Value);
        // "[Group]MM_Title(2025)": a short capital tag glued to the title by an underscore.
        if (Regex.Match(scan.Original[Math.Min(start, scan.Original.Length)..], @"^(?<tag>\p{Lu}{2,3})_", RegexOptions.None, Limit).Success && title.IndexOf(' ') is > 0 and var tagEnd) alternates.Add(title[(tagEnd + 1)..]);
        // "[Show_Name][Part_Name][DVDRip]": the group in front can be the show itself.
        if (scan.AllBrackets && scan.LeadingGroup && scan.Brackets.FirstOrDefault(b => !scan.Technical[b.Start]) is { End: > 0 } leading && scan.Text[(leading.Start + 1)..(leading.End - 1)].Trim() is var first && first.Contains(' ')) alternates.Add(first);
        // "Show Origins File 3", "Show - Vol.1": the word belongs to the number, not the title.
        if (best.Form == Form.Bare && best.Start > 0 && !scan.Text[..best.Start].TrimEnd().EndsWith('-')) { var bareTitle = PartWord.Replace(title, ""); if (bareTitle.Length > 0) title = bareTitle.Trim(' ', '-', ':'); }
        // "Show - 05.5 - S00E01": the marker gives the numbering; the dashed number before it is the release's own.
        if (best.Form is Form.SeasonEpisode or Form.CrossForm or Form.SeasonWords) title = Regex.Replace(title, @"\s+-\s*\d{1,4}(?:\.\d)?(?:v\d+)?$", "", RegexOptions.None, Limit).Trim(' ', '-');
        // "Show (2025) - 13v2 (S01E13)": the bracketed marker repeats the number just before it.
        // "The 100 (S01E05)", "Babylon 5 [01]": with no dash, version or matching padded number, it's the title's own.
        if (numbered && scan.BracketAt(best.Start) is not null && best.Start >= scan.CoreStart && Regex.Match(title, @"(?<dash>\s+-)?\s+(?<n>\d{1,4})(?<v>v\d+)?$", RegexOptions.None, Limit) is { Success: true } closing &&
            (closing.Groups["dash"].Success || closing.Groups["v"].Success || closing.Groups["n"].Value[0] == '0' && int.Parse(closing.Groups["n"].Value) == best.Episode)) title = title[..closing.Index].Trim(' ', '-');
        // "[TV Series] Mazinger Z - Song Name": the tag says the part before the dash is the show.
        if (scan.LeadingKindTag && !numbered && title.IndexOf(" - ", StringComparison.Ordinal) is > 0 and var dash) { alternates.Add(title); title = title[..dash]; }
        if (best.Form == Form.Clip && !best.LeadingTag) title = Regex.Replace(title, @"(?:\s+-)?\s+\d{1,3}(?:\.\d)?$", "", RegexOptions.None, Limit).Trim(' ', '-');
        // "1975 - Title": the year comes first in some collections.
        if (!numbered && year is null && Regex.Match(title, @"^(?<y>(?:19|20)\d{2})\s+-\s+(?<t>\S.*)$", RegexOptions.None, Limit) is { Success: true } dated) { year = int.Parse(dated.Groups["y"].Value); title = dated.Groups["t"].Value; }
        // A sorting number in front of a film's name: "01. Baltic Sea.2026.WEB-DLRip".
        if (!numbered && year is not null) title = Regex.Replace(title, @"^0\d{0,2}[.\s]+(?=\S)", "", RegexOptions.None, Limit);
        title = GenreTail.Replace(title, "");
        if (best.Form == Form.Film) title = FilmTitle(scan, best, title, stop, alternates);
        return (Trim(title), alternates.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), year);
    }

    private static string FilmTitle(FilenameScan scan, Hypothesis film, string title, int stop, List<string> alternates)
    {
        // "Show Movie 01 - Two Heroes" is the film "Show - Two Heroes"; providers list "Hyakuemu - Movie" as "Hyakuemu".
        var before = FormatTitle(scan, scan.CoreStart, film.Start, alternates);
        var afterEnd = scan.Text.IndexOf('[', Math.Min(film.End, scan.Text.Length)) is >= 0 and var square ? Math.Min(square, scan.CoreEnd) : scan.CoreEnd;
        var after = scan.Piece(film.End, afterEnd);
        var yearCut = YearToken.Match(after);
        if (yearCut.Success) after = after[..yearCut.Index].Trim(' ', '(');
        // "Scary Movie 3 - Comedy 2003": a genre isn't a subtitle.
        after = GenreTail.Replace(" " + after, "").Trim(' ', '-');
        if (film.Start > stop) return title;
        before = Regex.Replace(before, @"^0\d{0,2}[.\s]+(?=\S)", "", RegexOptions.None, Limit);
        // "The Lego Movie 2014", "Scary Movie 3": with no dash before it, "Movie" can belong to the title itself.
        if (!scan.Text[..film.Start].TrimEnd().EndsWith('-') && !scan.Text[..film.Start].TrimEnd().EndsWith('(') && Regex.IsMatch(scan.Text[film.Start..film.End], @"(?i)movie|film") && (after.Length == 0 || Regex.IsMatch(after, @"^\d{1,2}$", RegexOptions.None, Limit)) && !Regex.IsMatch(scan.Text[..film.Start].TrimEnd(), @"(?i)(?:^|\s)complete$", RegexOptions.None, Limit))
        {
            // "F1 The Movie" is also listed as "F1"; "Scary Movie" is never just "Scary".
            if (Regex.IsMatch(scan.Text[film.Start..film.End], @"(?i)^the[ -]", RegexOptions.None, Limit)) alternates.Add(before);
            return (before + " " + scan.Text[film.Start..film.End].Trim() + (after.Length > 0 ? " " + after : "")).Trim();
        }
        var franchise = Regex.Replace(before, @"\s*\((?:19|20)\d{2}\)\s*$", "", RegexOptions.None, Limit).Trim();
        // "Ai City - 1 - Complete Movie" is the film Ai City.
        franchise = Regex.Replace(franchise, @"(?:\s*-\s*\d{1,3})?(?:\s*-\s*|\s+)?(?:complete|full)?\s*$", "", RegexOptions.IgnoreCase, Limit).Trim(' ', '-');
        var whole = Trim(scan.Slice(scan.CoreStart, yearCut.Success ? film.End + yearCut.Index : scan.CoreEnd));
        var result = after.Length > 0 ? franchise + " - " + after : franchise;
        if (whole.Length > 0 && !whole.Equals(result, StringComparison.OrdinalIgnoreCase)) alternates.Add(whole);
        if (franchise.Length > 0 && after.Length == 0) alternates.Add(franchise);
        return result.Length > 0 ? result : whole;
    }

    private static int TrimYearTail(FilenameScan scan, int start, int stop, (int Start, int End, int Year, string? Alternate) found)
    {
        // "Show (2005) - S01E02" and "Show 2005 S01E02": the year closes the title. A year inside the title stays only without a following tag.
        var rest = scan.Slice(found.End, stop);
        return Regex.IsMatch(rest, @"^[\s\-)\]]*$", RegexOptions.None, Limit) ? found.Start : stop;
    }

    private static (int Start, int End, int Year, string? Alternate)? FindYear(FilenameScan scan, int from, int to)
    {
        // "[S02][E05] Name": the title can start after the number, so there may be nothing between them.
        if (from >= Math.Min(to, scan.Text.Length)) return null;
        var matches = YearToken.Matches(scan.Text[from..Math.Min(to, scan.Text.Length)]).Where(m => !scan.Technical[from + m.Index]).ToArray();
        // The first token of a name is its title: "2001 A Space Odyssey", "1917 (2019)".
        matches = matches.Where(m => !string.IsNullOrWhiteSpace(scan.Slice(scan.CoreStart, from + m.Index))).ToArray();
        // Years in the future are titles: "Blade Runner 2049".
        matches = matches.Where(m => int.Parse(m.Value) <= DateTime.UtcNow.Year + 1).ToArray();
        if (matches.Length == 0) return null;
        var last = matches[^1];
        if (matches.Length > 1)
        {
            var previous = matches[^2];
            var between = scan.Text[(from + previous.Index + previous.Length)..(from + last.Index)];
            if (Regex.IsMatch(between, @"^[\s\-_,)(]*$", RegexOptions.None, Limit))
            {
                var span = int.Parse(last.Value) - int.Parse(previous.Value);
                // "1991 1992" and "2018-2025" span a run; "Wonder Woman 1984 2020" holds a title year then a release year.
                if (span is >= 0 and <= 10 || between.Contains('-')) return (from + previous.Index, from + previous.Index + previous.Length, int.Parse(previous.Value), null);
                return (from + last.Index, from + last.Index + last.Length, int.Parse(last.Value), Trim(scan.Slice(scan.CoreStart, from + previous.Index)));
            }
        }
        return (from + last.Index, from + last.Index + last.Length, int.Parse(last.Value), null);
    }

    // Parentheses and mixed scripts can hold a second title; both are kept as search aliases.
    private static string FormatTitle(FilenameScan scan, int start, int stop, List<string> alternates)
    {
        var builder = new System.Text.StringBuilder();
        var position = start;
        foreach (var bracket in scan.Brackets.Where(b => b.Start >= start && b.Start < stop))
        {
            builder.Append(scan.Piece(position, bracket.Start)).Append(' ');
            position = Math.Min(bracket.End, stop);
            if (scan.Technical[bracket.Start]) continue;
            var inner = scan.Text[(bracket.Start + 1)..(bracket.End - 1)].Trim();
            if (bracket.Open == '[' && bracket.Start == start) { builder.Append(inner).Append(' '); continue; }
            // "(Neben der Spur, 2022, ru_DVO)": the alias is the text before the year.
            var year = YearToken.Match(inner);
            var alias = year.Success ? inner[..year.Index].Trim(' ', ',', '-') : inner;
            alias = alias.Split(',')[0].Trim();
            if (alias.Length >= 2 && !GenreWords.IsMatch(alias) && !Regex.IsMatch(alias, @"^\d+$", RegexOptions.None, Limit)) alternates.Add(alias);
        }
        if (position < stop) builder.Append(scan.Piece(position, stop));
        var title = Regex.Replace(Regex.Replace(builder.ToString().Replace("\"", " "), @"[\p{So}\p{Sk}]", " ", RegexOptions.None, Limit), @"\s+", " ", RegexOptions.None, Limit).Trim(' ', '-', ':', ',', '+', '~');
        title = TrimReleaseWords(title);
        // "Les amants du Pont Neuf AKA The Lovers on the Bridge".
        var aka = Regex.Match(title, @"^(?<a>.+?)\s+(?:aka|a\.k\.a\.?)\s+(?<b>.+)$", RegexOptions.IgnoreCase, Limit);
        if (aka.Success) { alternates.Add(aka.Groups["b"].Value.Trim()); title = aka.Groups["a"].Value.Trim(); }
        // "Show OVA 1" is also listed under Show.
        if (Regex.Match(title, @"^(?<a>.+?)\s+(?:OVA|OAV|OAD|ONA)(?:\s+\d{1,2})?$", RegexOptions.None, Limit) is { Success: true } side) alternates.Add(side.Groups["a"].Value.Trim());
        // "来自深渊 / Series Title": two titles side by side.
        var slash = title.IndexOf(" / ", StringComparison.Ordinal);
        if (slash > 0) { alternates.Add(title[(slash + 3)..].Trim()); title = title[..slash].Trim(); }
        var mixed = Regex.Match(title, @"^(?<a>[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}！、，]+)\s+(?<b>[A-Za-z].*)$", RegexOptions.None, Limit);
        if (mixed.Success) { alternates.Add(mixed.Groups["b"].Value.Trim()); title = mixed.Groups["a"].Value.Trim(); }
        mixed = Regex.Match(title, @"^(?<a>[A-Za-z].*?)\s+(?<b>[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}！、，][\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}！、，\d]*)$", RegexOptions.None, Limit);
        if (mixed.Success) { alternates.Add(mixed.Groups["b"].Value.Trim()); title = mixed.Groups["a"].Value.Trim(); }
        // Two titles in different scripts: "Столкновение лбами  Headbutt".
        var scripts = Regex.Match(title, @"^(?<a>[\p{IsCyrillic}][\p{IsCyrillic}\s,:!?'-]*?)\s+(?<b>[A-Za-z][A-Za-z\s,:!?'-]*)$", RegexOptions.None, Limit);
        if (scripts.Success) { alternates.Add(scripts.Groups["b"].Value.Trim()); title = scripts.Groups["a"].Value.Trim(); }
        return TrimReleaseWords(title);
    }

    // A trailing release word on a title: "Mobile Suit Gundam BD", "Show TV". Capitals only, so "Madame Web" keeps its name.
    private static string TrimReleaseWords(string title)
    {
        title = Regex.Replace(title, @"(?:[\s-]+(?:BD|DVD|TV|OVA|OAV|LD|WEB|BDMV|HD|FHD|MOVIES?))+$", "", RegexOptions.None, Limit);
        // An all-lowercase name says nothing by case, so only words that can't end a title go: "zx.show.tv.07".
        if (!title.Any(char.IsUpper)) title = Regex.Replace(title, @"(?:[\s-]+(?:bd|dvd|tv|ova|oav))+$", "", RegexOptions.None, Limit);
        // "Game of Thrones - The Complete Season 3", "The Sopranos: The Complete Series".
        return Regex.Replace(title, @"(?i)(?:[\s-]+(?:batch|(?:the\s+)?complete(?:\s+(?:series|collection|edition))?))+$", "", RegexOptions.None, Limit).Trim(' ', '-', ':', ',');
    }

    // Other titles the name and its folder hold, for provider search: "Show (Alias)", "Native  English", "Main Title - Part Title".
    internal static IEnumerable<string> SearchAliases(string path, bool series, bool folderOwnsTitle)
    {
        var (context, _, folderSeason) = FileNames.NameContext(path, "Mixed");
        var reading = Analyze(path, context || series || folderSeason is not null, folderSeason, true);
        foreach (var alternate in reading.Alternates) yield return alternate;
        if (!folderOwnsTitle && reading.Title.Length > 0) yield return reading.Title;
        // An obfuscated file in a release folder: the folder holds the only real name.
        if (Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } folder && !FilenameFolders.IsGeneric(folder))
        {
            var scan = new FilenameScan(folder);
            if (scan.HasTail || scan.Brackets.Count > 0)
            {
                var named = AnalyzeFolder(folder);
                if (named.Title.Length > 0) yield return named.Title;
                foreach (var alternate in named.Alternates) yield return alternate;
            }
        }
    }

    // A dashed title can be listed under either side: "Mushoku Tensei - Jobless Reincarnation". The side after the dash has to
    // be a title of its own, not "Chapter 2".
    internal static IEnumerable<string> PartAliases(string path, string displayTitle, bool series)
    {
        var (context, _, folderSeason) = FileNames.NameContext(path, "Mixed");
        var reading = Analyze(path, context || series || folderSeason is not null, folderSeason, true);
        foreach (var title in new[] { displayTitle, reading.Title }.Concat(reading.Alternates))
        {
            if (title.IndexOf(" - ", StringComparison.Ordinal) is not (> 0 and var dash)) continue;
            var first = title[..dash].Trim(); var second = title[(dash + 3)..].Trim();
            if (first.Length >= 3) yield return first;
            if (series && second.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2 && !second.Any(char.IsDigit)) yield return second;
        }
    }

    // The numbering a release folder's own name spells out with a season and episode marker.
    internal static Result? ReleaseNumbering(string folder)
    {
        var reading = AnalyzeName(folder, FilenameFolders.FolderEvidence.None, false);
        return reading.Kind is nameof(Form.SeasonEpisode) or nameof(Form.CrossForm) && reading.Title.Length > 0 ? reading.Numbering : null;
    }
    internal static bool SameTitle(string a, string b) => MetadataTitles.Normalize(a) == MetadataTitles.Normalize(b);

    internal sealed record FolderReading(string Title, string[] Alternates, int? Year, int? Season);
    private static readonly Regex FolderStop = new(@"(?<![\p{L}\p{N}])(?:(?:tv|ep?|episodes?|ova|oav|vol|volume)[ .]*)?(?<a>[0-9]{1,4})\s*[-~+]\s*(?:ep?[ .]*)?(?<b>[0-9]{1,4})(?![\p{L}\p{N}])|(?<![\p{L}\p{N}])(?:the\s+)?complete(?:\s+(?:series|collection|batch))?(?![\p{L}\p{N}])|(?<![\p{L}\p{N}])(?:batch|box[ -]?set|seasons?'?s?)(?![\p{L}\p{N}])|\+\s*(?:ova|oav|movies?|specials?|extras?)|(?<![\p{L}\p{N}])(?:vol(?:ume)?|disc|disk|box|bd|dvd)[ .]*[0-9]{1,2}(?![\p{L}\p{N}])", Options, Limit);

    // A folder name is read like a release title: what comes before its season, range or release terms is its title.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FolderReading> FolderReadings = new();
    internal static FolderReading AnalyzeFolder(string folder)
    {
        if (FolderReadings.TryGetValue(folder, out var known)) return known;
        FolderReading reading;
        try { reading = ReadFolderName(folder); }
        // A folder the reader can't make sense of is just a name, like a file it can't.
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or FormatException or OverflowException or InvalidOperationException or RegexMatchTimeoutException)
        {
            PlaybackDiagnostics.Record("folder-read-failed", new { error = ex.GetType().Name });
            return new(folder, [], null, null);
        }
        if (FolderReadings.Count > 8192) FolderReadings.Clear();
        FolderReadings[folder] = reading;
        return reading;
    }

    private static FolderReading ReadFolderName(string folder)
    {
        var scan = new FilenameScan(folder);
        var start = scan.CoreStart; var stop = Math.Max(start, scan.CoreEnd);
        int? season = null;
        foreach (var pattern in SeasonEpisodeForms.Append(SeasonOnly))
            foreach (Match match in pattern.Matches(scan.Text))
                if (match.Index >= start && match.Index < stop && !scan.Technical[match.Index])
                {
                    season ??= match.Groups["s"].Success ? int.Parse(match.Groups["s"].Value) : null;
                    stop = match.Index; break;
                }
        // Matches can overlap: "Edgerunners 2 - 01 ~ 10" holds a title number and then a range.
        for (var match = FolderStop.Match(scan.Text, Math.Min(start, scan.Text.Length)); match.Success && match.Index < stop; match = FolderStop.Match(scan.Text, match.Index + 1))
        {
            if (scan.Technical[match.Index]) continue;
            if (match.Groups["a"].Success)
            {
                var low = int.Parse(match.Groups["a"].Value); var high = int.Parse(match.Groups["b"].Value);
                if (low is >= 1900 and <= 2099 && high is >= 1900 and <= 2099 || low >= high) continue;
            }
            stop = match.Index; break;
        }
        // Inside a bracket, the whole bracket goes: "Show (S01-S07+Movies)".
        if (scan.BracketAt(stop) is { } open && open.Start >= start && stop < scan.Text.Length) stop = open.Start;
        var alternates = new List<string>();
        var year = FindYear(scan, start, Math.Max(stop, scan.CoreEnd));
        int? yearValue = year?.Year;
        if (year is { } y && y.Start < stop && y.Start > start) stop = y.Start;
        if (yearValue is null)
            foreach (var bracket in scan.Brackets)
                if (bracket.Start >= start && YearToken.Match(scan.Text[bracket.Start..bracket.End]) is { Success: true } inside && int.Parse(inside.Value) <= DateTime.UtcNow.Year + 1) { yearValue = int.Parse(inside.Value); break; }
        var title = Trim(Regex.Replace(FormatTitle(scan, start, stop, alternates), @"(?:\s+(?:TV|MOVIE|MOVIES|OVA|OAV|BD|DVD|WEB|Series))+$", "", RegexOptions.IgnoreCase, Limit));
        // "01. Season 1" numbers a folder; it doesn't name one.
        // "86 - 5" names its show 86.
        var numberedFrom = Math.Min(scan.CoreStart + title.Length, scan.Text.Length);
        var numberedShow = title[..Math.Min(1, title.Length)] != "0" && DashNumberFollows.IsMatch(scan.Text[numberedFrom..Math.Max(numberedFrom, Math.Min(scan.CoreEnd, scan.Text.Length))]);
        if (!numberedShow && Regex.IsMatch(title, @"^\d{1,3}\.?$", RegexOptions.None, Limit) && (season is not null || stop < scan.CoreEnd || scan.Text.TrimStart().StartsWith(title + ".", StringComparison.Ordinal))) title = "";
        title = Regex.Replace(title, @"^0\d{0,2}(?:\.|\s+-)\s*(?=\S)", "", RegexOptions.None, Limit);
        // A single episode's release: "Clione no Akari 01 Vostfr", "Mai-ZHiME 02v2 vostfr".
        title = Regex.Replace(title, @"\s+0\d(?:v\d+)?$", "", RegexOptions.None, Limit);
        return new(title, alternates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), yearValue, season);
    }

    private static string? Subtitle(string text)
    {
        var spaced = Regex.Replace(text, @"\s+", " ", RegexOptions.None, Limit);
        text = Trim(spaced);
        // Trimming takes a closing bracket the name needs, as in "Who Shot Mr Burns (1)". One it never had means the name
        // was cut short, as in "The Pink Phink (Dec 18, 1964)" stopped at its year, so that part goes.
        if (text.Count(c => c == '(') > text.Count(c => c == ')'))
            text = spaced.TrimEnd(' ', '-', '[', ']', ':', '~', ',').EndsWith(')') ? text + ")" : Trim(text[..text.LastIndexOf('(')]);
        return text.Length == 0 || Regex.IsMatch(text, @"(?i)^(?:END|FINAL|COMPLETE|v\d+)$", RegexOptions.None, Limit) ? null : text;
    }
    // What can follow an episode's number without naming it: the rest of a bracket opened before it, as in "S02E01 (14) - Name" and
    // "(01.seriya.iz.08)", how many episodes there are, and a release's year. A year stays when it's set apart as the name, as in "Show - S01E05 - 1984.mkv".
    private static string? EpisodeName(string words, FilenameScan scan)
    {
        // "[SubsPlease] (Grp)" and "[Grp] Name" have lost the opening bracket by here.
        var tagged = Regex.Match(words, @"^[^\[\]]*\]\s*", RegexOptions.None, Limit);
        if (tagged.Success) { words = words[tagged.Length..]; if (Regex.IsMatch(words, @"^\([^()]*\)$", RegexOptions.None, Limit)) return null; }
        words = Regex.Replace(words, @"^[^()]*\)[\s\-:]*", "", RegexOptions.None, Limit);
        words = Regex.Replace(words, @"(?i)^(?:of|iz|из)\s+\d{1,4}(?=\s*$|\s+(?:19|20)\d{2}\s*$)", "", RegexOptions.None, Limit).Trim();
        var releaseYear = Regex.IsMatch(words, @"^(?:19|20)\d{2}$", RegexOptions.None, Limit) && (!scan.Spaced || scan.CoreEnd < scan.Text.Length);
        return words.Length == 0 || releaseYear ? null : words;
    }
}
