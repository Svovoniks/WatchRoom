using System.Text.RegularExpressions;

namespace Watchroom.Core;

// What a show's clip is, following AniDB's grouping: specials, credits (openings and endings), trailers, and everything else.
// The kind comes from a clip word such as NCOP, ED, PV or OVA, or else from the words of the clip's title.
internal static class FilenameExtras
{
    internal const string Special = "special", Opening = "opening", Ending = "ending", Trailer = "trailer";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    // A clip word on its own: "NCOP2", "ED", "PV 3", "OVA", "SP1".
    private static readonly Regex OpeningSlot = new(@"^(?:NC\s*OP|OP|(?:creditless\s+|clean\s+)?opening)\s*\d{0,2}[a-z]?$", Options, Timeout);
    private static readonly Regex EndingSlot = new(@"^(?:NC\s*ED|ED|(?:creditless\s+|clean\s+)?ending)\s*\d{0,2}[a-z]?$", Options, Timeout);
    private static readonly Regex TrailerSlot = new(@"^(?:PV|CM|preview|trailer|teaser|promo|spot)\s*\d{0,2}$", Options, Timeout);
    private static readonly Regex SpecialSlot = new(@"^(?:SP|special|OVA|OAD|ONA|recap|pilot)\s*\d{0,3}$", Options, Timeout);
    // Words in a longer title: "Original Closing Sequence", "Movie Character Art Gallery".
    private static readonly Regex OtherWords = new(@"\b(?:gallery|menu|interview|making|storyboards?|featurettes?|behind|bloopers?|deleted|commentary|art|photos?|stills)\b", Options, Timeout);
    private static readonly Regex OpeningWords = new(@"\b(?:opening|intro|NC\s?OP\d{0,2})\b", Options, Timeout);
    private static readonly Regex EndingWords = new(@"\b(?:ending|closing|outro|NC\s?ED\d{0,2})\b", Options, Timeout);
    private static readonly Regex TrailerWords = new(@"\b(?:trailers?|teasers?|promos?|previews?|commercials?|PV|CM)\b", Options, Timeout);
    private static readonly Regex SpecialWords = new(@"\b(?:special|OVA|OAD|recap|movie|pilot)\b", Options, Timeout);

    // The kind of a clip from its title: a clip word on its own, or else the words in the title.
    internal static string? Kind(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = title.Trim();
        if (OpeningSlot.IsMatch(title)) return Opening;
        if (EndingSlot.IsMatch(title)) return Ending;
        if (TrailerSlot.IsMatch(title)) return Trailer;
        if (SpecialSlot.IsMatch(title)) return Special;
        if (OtherWords.IsMatch(title)) return null;
        if (OpeningWords.IsMatch(title)) return Opening;
        if (EndingWords.IsMatch(title)) return Ending;
        if (TrailerWords.IsMatch(title)) return Trailer;
        return SpecialWords.IsMatch(title) ? Special : null;
    }
}
