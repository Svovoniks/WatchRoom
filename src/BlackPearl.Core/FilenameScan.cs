using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// The first step of reading a name: normalise it, then work out which characters describe the release rather than the
// title. Every later step treats those characters as absent. Offsets in Text match the input name one to one.
internal sealed class FilenameScan
{
    internal readonly record struct Span(int Start, int End);
    internal readonly record struct Bracket(int Start, int End, char Open);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private static Regex Rx(string pattern, RegexOptions extra = RegexOptions.None) => new(pattern, Options | extra, Limit);

    // Terms that describe a release wherever they appear.
    private static readonly Regex Strong = Rx(@"(?<![\p{L}\p{N}])(?:
        \d{3,4}[pi] | \d{3,4}[x×]\d{3,4}p? | \d{3,4}\s[x×]\s\d{3,4}p? | [48]kp | [48]k\s?uhd |
        bd-?rip | br-?rip | bd-?remux | bd-?enc | bd-?iso | bd-?box | bd-?mv | blu-?\s?ray | hd-?dvd(?:-?rip)? | hd-?dvd | dvd-?rip | dvd-?remux | dvd-?iso | dvd-?scr | dvd-?box | dvd[59] | \d?x?dvd[59] |
        hd-?tv(?:-?rip)? | pd-?tv | sd-?tv | dsr(?:ip)? | hd-?rip | hdlight | hd-?cam | cam-?rip | hd-?ts | hd-?tc | web-?\s?dl(?:-?rip|\d{3,4}p)? | web-?\s?rip | web-?\s?hd | tv-?rip | sat-?rip | dvb-?rip | vhs-?rip | ld-?rip | hdtvrip | ddvdrip | remux | upscale | bd-?\d{2,3}[pi]? |
        [xh][\s.]?26[3456] | hevc\d? | avc | av1 | vvc | xvid | divx\d? | mpeg-?[24] | wmv\d? | vc-?1 | vp9 | 10-?bits? | 8-?bits? | 10-?b | hi10p? | hi444p? | yuv\d+\w* |
        hdr(?:10(?:\+|plus)?)? | dolby\s?vision | crf\d+ |
        (?:he-?|lc-?)?aac(?:v2)?(?:[\s.]?\d(?:[\s.]\d)?)? | e-?ac-?3(?:[\s.]?\d(?:[\s.]\d)?)? | ac-?3(?:[\s.]?\d(?:[\s.]\d)?)? | ddp?\s?\d(?:[\s.]\d)? | dd\+\d?(?:[\s.]\d)? | ddp | dts(?:-?hd|-?x|-?es)?(?:[\s-]?ma)?(?:[\s.]?\d(?:[\s.]\d)?)? | true-?hd(?:[\s.]?\d(?:[\s.]\d)?)? |
        flac(?:[\s.]?\d(?:[\s.]\d)?)? | opus(?:[\s.]?\d(?:[\s.]?\d)?)? | vorbis | mp3 | lamevbr | lame | atmos | l?pcm | \dch | \d\.\d\s?ch |
        dual[\s-]?audio | multi[\s-]?(?:subs?|subtitles?|audio|lang|language) | multiple[\s-]subs?(?:titles?)? | eng?[\s-]?subs? | english[\s-]subs? | dual[\s-]?subs? | esubs? | msubs? | vostf(?:r)? | vosta | truefrench |
        hardsub | softsub | repack\d? | rerip | proper\d? | hdhub4u | eztvx?(?:\.to)? | yts(?:\.\w{2,3})? | rarbg | ettv | nyaa
        )(?![\p{L}\p{N}])", RegexOptions.IgnorePatternWhitespace);
    // Terms that only describe a release beside other release terms: "WEB" and "IT" are also ordinary words.
    private static readonly Regex Weak = Rx(@"(?<![\p{L}\p{N}])(?:
        4k | 8k | audio | latino | es | web | webm | bd | dvd | tv | hd | fhd | uhd | qhd | sd | ld | vhs | dvb | raw | raws | rip | nf | netflix | amzn | amazon | ma | atvp | atv | cr | crunchyroll | dsnp | disney | hmax | hulu | it | bili | bilibili | iq | viki | abema | tvnz | bnge | u-next |
        dl | ac3d | line | hq | mux | readnfo | stv | dvdr | dv | remaster(?:ed)? | restored | uncut | uncensored | unrated | extended | theatrical | criterion | imax | remastered | real | internal | limited | retail | dubbed | subbed | dub | sub | subs | subtitles? | dual | multi | multilang | multisub |
        rus | russian | eng | english | jap | jpn | japanese | ger | deu | german | fre | fra | french | vf | vff | spa | esp | ita | kor | korean | chi | chinese | cht | chs | gb | big5 | por | pol | pl | ru | en | de | fr | ja | jp | zh | ukr | lat | castellano |
        mvo | dvo | avo | svo | mp4 | mkv | avi | ogm | bdmv | r1 | r2 | r2j | ntsc | pal | sdr | hdr | subtitled | batch | complete | fullhd | full-hd | 1440p | v\d
        )(?![\p{L}\p{N}])", RegexOptions.IgnorePatternWhitespace);
    private static readonly Regex Hash = Rx(@"[\[(]\s*[0-9a-f]{8}\s*[\])]");
    // ".to" and ".tv" are also words in dotted titles ("Dead.to.Me"), so those count only as a whole bracket: "[EZTVx.to]".
    private static readonly Regex Site = Rx(@"(?<![\p{L}\p{N}])(?:www\.[\p{L}\p{N}-]+\.[a-z]{2,4}|[\p{L}\p{N}-]{2,}\.(?:com|net|org|info)|(?<=\[)[\p{L}\p{N}-]{2,}\.[a-z]{2,4}(?=\]))(?![\p{L}\p{N}])");
    private static readonly Regex SitePrefix = Rx(@"^[\[{(]?\s*www[.,\s][^\[\]{}()]*?[\]})]?\s*-\s+(?=\S)");
    // Edition words in capitals, as scene names write them, end a title; in ordinary case they can be title words ("Extended Family").
    private static readonly Regex Edition = new(@"(?<![\p{L}\p{N}])(?:EXTENDED|UNRATED|UNCUT|REMASTERED|DIRECTORS?|CUSTOM|INTEGRAL|COMPLETE|LIMITED|THEATRICAL|REPACK|PROPER)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit);
    private static readonly Regex FullLanguage = Rx(@"(?<![\p{L}\p{N}])(?:french|german|english|russian|japanese|italian|spanish|chinese|korean|polish)(?![\p{L}\p{N}])");
    private static readonly Regex AudioLayout = Rx(@"(?<![\p{L}\p{N}])\d\.\dx\d(?![\p{L}\p{N}])");
    private static readonly Regex Word = Rx(@"[^\s,+&/\[\](){}]+");

    internal string Text { get; }
    internal string Original { get; }
    internal bool[] Technical { get; }
    internal List<Bracket> Brackets { get; } = [];
    // The text that can hold a title: leading tags and everything from the first release term on are outside it.
    internal int CoreStart { get; private set; }
    internal int CoreEnd { get; private set; }
    internal bool HasTail { get; private set; }
    internal bool LeadingGroup { get; private set; }
    // A name with spaces uses them as separators; dotted and underscored scene names don't.
    internal bool Spaced { get; }
    internal bool AllBrackets { get; private set; }
    // "[TV Series]", "[OVA]": a tag in front of the name says its first part names the show.
    internal bool LeadingKindTag { get; private set; }

    internal FilenameScan(string name)
    {
        var raw = Composed(name);
        Spaced = raw.Contains(' ');
        Original = raw;
        Text = Normalize(raw);
        Technical = new bool[Text.Length];
        FindBrackets();
        foreach (Match match in Hash.Matches(raw)) Mark(match.Index, match.Index + match.Length);
        foreach (Match match in Site.Matches(raw)) Mark(match.Index, match.Index + match.Length);
        // "www.Site.com - Title", "www TamilBlasters tel - Title".
        if (SitePrefix.Match(raw) is { Success: true } prefix) Mark(prefix.Index, prefix.Index + prefix.Length);
        // "2.0x3": an audio layout and its track count.
        foreach (Match match in AudioLayout.Matches(raw)) Mark(match.Index, match.Index + match.Length);
        MarkVocabulary();
        MarkTechnicalBrackets();
        // "BluRay 1080p French Cancan 1955": a language name right after leading tags can open the title.
        foreach (Match language in FullLanguage.Matches(Text))
            if (Technical[language.Index] && BracketAt(language.Index) is null && language.Index + language.Length < Text.Length && NextWordIsTitle(language.Index + language.Length) && Slice(0, language.Index).Length == 0)
                for (var i = language.Index; i < language.Index + language.Length; i++) Technical[i] = false;
        // "Title_1.46.avi": a size in gigabytes tagged onto a spaced name.
        if (Spaced && Regex.Match(raw, @"(?<=\p{L})_\d\.\d{2}$", RegexOptions.None, Limit) is { Success: true } size) Mark(size.Index, Text.Length);
        FindCore();
    }

    // Windows allows file names with broken UTF-16, which text normalization refuses, so those characters are replaced first.
    internal static string Composed(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1])) i++;
            else if (char.IsSurrogate(chars[i])) chars[i] = '\uFFFD';
        }
        var text = new string(chars);
        return text.IsNormalized() ? text : text.Normalize(NormalizationForm.FormC);
    }

    private static string Normalize(string raw)
    {
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            chars[i] = c switch
            {
                '_' => ' ',
                '.' when IsDecimalPoint(raw, i) => '.',
                '.' => ' ',
                '【' or '［' or '「' or '『' or '〔' or '〈' or '《' => '[',
                '】' or '］' or '」' or '』' or '〕' or '〉' or '》' => ']',
                '（' => '(',
                '）' => ')',
                '–' or '—' or '−' or '‐' or '‑' or '―' or '－' => '-',
                '：' or '꞉' => ':',
                '～' or '〜' => '~',
                '＃' or '№' => '#',
                'х' or 'Х' when i > 0 && i + 1 < chars.Length && char.IsDigit(raw[i - 1]) && char.IsDigit(raw[i + 1]) => 'x',
                '«' or '»' or '“' or '”' or '„' => '"',
                '　' => ' ',
                >= '０' and <= '９' => (char)('0' + (c - '０')),
                _ => c
            };
        }
        return new string(chars);
    }

    // "2.0", "11.5": a short number with one fractional part. "1.2.2024" and "2026.11.02" are separate numbers.
    private static bool IsDecimalPoint(string raw, int i)
    {
        if (i == 0 || i + 1 >= raw.Length || !char.IsDigit(raw[i - 1]) || !char.IsDigit(raw[i + 1])) return false;
        var left = i - 1; while (left >= 0 && char.IsDigit(raw[left])) left--;
        var right = i + 1; while (right < raw.Length && char.IsDigit(raw[right])) right++;
        if (i - left - 1 > 2 || right - i - 1 != 1) return false;
        if (left >= 0 && raw[left] == '.') return false;
        if (right < raw.Length && char.IsLetter(raw[right])) return false;
        return right >= raw.Length || raw[right] != '.' || right + 1 >= raw.Length || !char.IsDigit(raw[right + 1]);
    }

    private void FindBrackets()
    {
        var open = new Stack<int>();
        for (var i = 0; i < Text.Length; i++)
        {
            var c = Text[i];
            if (c is '[' or '(' or '{') open.Push(i);
            else if (c is ']' or ')' or '}' && open.Count > 0)
            {
                var start = open.Pop();
                // Only the outermost group counts; a stray closer between groups is ignored.
                if (open.Count == 0) Brackets.Add(new(start, i + 1, Text[start]));
            }
        }
    }

    private void Mark(int start, int end)
    {
        for (var i = start; i < end && i < Technical.Length; i++) Technical[i] = true;
    }

    private void MarkVocabulary()
    {
        foreach (Match match in Strong.Matches(Text)) Mark(match.Index, match.Index + match.Length);
        // Only in a mixed-case name does capitalisation say anything.
        if (Text.Any(char.IsLower)) foreach (Match match in Edition.Matches(Text)) Mark(match.Index, match.Index + match.Length);
        // A weak term counts when a release term or another counted weak term sits right beside it.
        var weak = Weak.Matches(Text).Select(m => new Span(m.Index, m.Index + m.Length)).ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var span in weak.ToArray())
                if (!Technical[span.Start] && (TouchesLeft(span.Start) || TouchesRight(span.End)))
                { Mark(span.Start, span.End); weak.Remove(span); changed = true; }
        }
        // A trailing "-GROUP" after release terms is the release group.
        var last = Array.LastIndexOf(Technical, true);
        if (last >= 0 && last + 1 < Text.Length && Text[last + 1] == '-' && Regex.IsMatch(Text[(last + 2)..], @"^[\p{L}\p{N}]+$", RegexOptions.None, Limit)) Mark(last + 1, Text.Length);
    }

    private bool NextWordIsTitle(int from)
    {
        var i = from;
        while (i < Text.Length && Text[i] == ' ') i++;
        return i < Text.Length && !Technical[i] && char.IsLetter(Text[i]);
    }

    private bool TouchesLeft(int start)
    {
        var i = start - 1;
        while (i >= 0 && Text[i] is ' ' or ',' or '+' or '&' or '/' or '-' or '[' or '(') i--;
        return i >= 0 && Technical[i];
    }

    private bool TouchesRight(int end)
    {
        var i = end;
        while (i < Text.Length && Text[i] is ' ' or ',' or '+' or '&' or '/' or '-' or ']' or ')') i++;
        return i < Text.Length && Technical[i];
    }

    private void MarkTechnicalBrackets()
    {
        foreach (var bracket in Brackets)
        {
            var inside = Text[(bracket.Start + 1)..(bracket.End - 1)];
            // "[DVD]", "(BD, 720p)", "[RUS + JAP + ENG]": every word in the group is a release term.
            var words = Word.Matches(inside).ToArray();
            // "(5.1)" gives the audio layout.
            // "[720]" gives the resolution.
            if (words.Length == 0 || Regex.IsMatch(inside.Trim(), @"^(?:\d\.\d|480|576|720|1080|2160)$", RegexOptions.None, Limit) && bracket.Start > 0) { Mark(bracket.Start, bracket.End); continue; }
            var counted = words.Count(w => Technical[bracket.Start + 1 + w.Index] || Weak.Match(w.Value) is { Success: true } weak && weak.Length == w.Length);
            var strong = words.Count(w => Technical[bracket.Start + 1 + w.Index]);
            if (counted == words.Length || strong * 2 >= words.Length && strong > 0) Mark(bracket.Start, bracket.End);
        }
    }

    private bool InBracket(int index, out Bracket found)
    {
        foreach (var bracket in Brackets)
            if (index >= bracket.Start && index < bracket.End) { found = bracket; return true; }
        found = default;
        return false;
    }

    internal bool InBracket(int index) => InBracket(index, out _);
    internal Bracket? BracketAt(int index) => InBracket(index, out var found) ? found : null;

    private void FindCore()
    {
        var position = 0;
        var openText = false;
        // Find the first unbracketed text that isn't a release term.
        for (var i = 0; i < Text.Length && !openText; i++)
        {
            if (Technical[i] || char.IsWhiteSpace(Text[i]) || Text[i] is '-' or ',' or '+' or '~' or ':') continue;
            // "1[Group] Title": a stray digit stuck on a leading group.
            if (i == 0 && char.IsDigit(Text[0]) && Text.Length > 1 && Text[1] == '[') continue;
            if (BracketAt(i) is { } bracket) { i = bracket.End - 1; continue; }
            position = i; openText = true;
        }
        var open = Brackets.Where(b => !Technical[b.Start]).ToArray();
        LeadingKindTag = open.Where(b => b.End <= (openText ? position : Text.Length)).Any(IsSeriesTag);
        if (!openText)
        {
            // "[FLsnow][Ushitora][22][CHT]": with no open text, the group that follows the release group holds the title;
            // "[Assemble_Insert][OVA][01]" has no release group, because the next group is a tag or a number.
            AllBrackets = open.Length > 0;
            var groupFirst = open.Length > 1 && !IsTag(open[1]) && !IsDate(open[1]);
            var title = groupFirst ? open[1] : open.Length >= 1 ? open[0] : default;
            LeadingGroup = groupFirst;
            CoreStart = open.Length > 0 ? title.Start : 0;
            CoreEnd = FirstTechnical(CoreStart, skipBrackets: false);
            HasTail = CoreEnd < Text.Length;
            CoreEnd = open.Length > 0 ? Math.Max(CoreEnd, open[^1].End) : Text.Length;
            return;
        }
        // "《Lantern Bay》03": the bracket before a bare number is the title, not a release group.
        var digits = position; while (digits < Text.Length && char.IsDigit(Text[digits])) digits++;
        if (digits > position && digits - position <= 4 && open.Length == 1 && open[0].End <= position && open[0].Start == 0 && (digits == Text.Length || !char.IsLetter(Text[digits])) && Slice(digits, Text.Length).Length == 0)
        {
            CoreStart = open[0].Start; LeadingGroup = false;
            CoreEnd = FirstTechnical(digits, skipBrackets: true);
            HasTail = CoreEnd < Text.Length;
            return;
        }
        LeadingGroup = open.Any(b => b.End <= position) || position > 0 && Text[0] is '[' or '(';
        CoreStart = position;
        var end = FirstTechnical(CoreStart, skipBrackets: true);
        // "Show [480]Show - 01": the same name again after a release tag; the second copy is the one with the numbering.
        if (end < Text.Length && BracketAt(end) is { } tag && Slice(position, end) is { Length: >= 3 } first)
        {
            var rest = Text[tag.End..].TrimStart();
            if (rest.StartsWith(first + " ", StringComparison.OrdinalIgnoreCase) || rest.StartsWith(first + "-", StringComparison.OrdinalIgnoreCase))
            {
                CoreStart = Text.Length - rest.Length;
                end = FirstTechnical(CoreStart, skipBrackets: true);
            }
        }
        HasTail = end < Text.Length;
        CoreEnd = end;
    }

    private bool IsTag(Bracket bracket) => Regex.IsMatch(Text[(bracket.Start + 1)..(bracket.End - 1)], @"(?i)^(?:tv(?:\s+series)?|ova|oav|ona|movie|film|web|special|sp|アニメ|\d{1,4})$", RegexOptions.None, Limit);
    internal bool IsSeriesTag(Bracket bracket) => Regex.IsMatch(Text[(bracket.Start + 1)..(bracket.End - 1)], @"(?i)^(?:tv\s+series|ova|oav|ona|movie|web)$", RegexOptions.None, Limit);
    private bool IsDate(Bracket bracket) => Regex.IsMatch(Text[(bracket.Start + 1)..(bracket.End - 1)], @"^[\d .\-]+$", RegexOptions.None, Limit);

    private int FirstTechnical(int from, bool skipBrackets)
    {
        for (var i = from; i < Text.Length; i++)
        {
            if (!Technical[i]) continue;
            return skipBrackets && BracketAt(i) is { } bracket ? bracket.Start : i;
        }
        return Text.Length;
    }

    // The same text without trimming, for joining pieces around a bracket.
    internal string Piece(int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = Math.Max(0, start); i < Math.Min(end, Text.Length); i++)
            builder.Append(Technical[i] ? ' ' : Text[i]);
        return builder.ToString();
    }

    // The title text between two offsets, with release terms and tag brackets removed.
    internal string Slice(int start, int end)
    {
        var builder = new StringBuilder();
        for (var i = Math.Max(0, start); i < Math.Min(end, Text.Length); i++)
            builder.Append(Technical[i] ? ' ' : Text[i]);
        return Regex.Replace(builder.ToString(), @"\s+", " ", RegexOptions.None, Limit).Trim(' ', '-', ':', ',', '+', '~', '[', ']');
    }
}
