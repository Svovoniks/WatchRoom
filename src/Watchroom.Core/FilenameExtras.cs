using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

// A show's clips rarely share one naming scheme, but they share the folder's: in a folder of
// "Azumanga Daioh - 01 - Miss Yukari", the files "Azumanga Daioh - XX - Original Opening Sequence",
// "Azumanga Daioh - NCED" and "Azumanga Daioh - OVA 2" are the show's clips because they start the way its episodes do.
// A clip's kind follows AniDB's grouping: specials, credits (openings and endings), trailers, and everything else.
internal static class FilenameExtras
{
    internal const string Special = "special", Opening = "opening", Ending = "ending", Trailer = "trailer";
    // Marked: the clip's slot is a clip word such as NCOP or OVA rather than a stand-in or a bare title.
    internal sealed record Clip(string Series, string Title, string? Kind, bool Marked = false);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    // The word standing where an episode number would: "NCOP2", "ED", "PV 3", "OVA", "SP1", or a stand-in such as "XX".
    private static readonly Regex OpeningSlot = new(@"^(?:NC\s*OP|OP|(?:creditless\s+|clean\s+)?opening)\s*\d{0,2}[a-z]?$", Options, Timeout);
    private static readonly Regex EndingSlot = new(@"^(?:NC\s*ED|ED|(?:creditless\s+|clean\s+)?ending)\s*\d{0,2}[a-z]?$", Options, Timeout);
    private static readonly Regex TrailerSlot = new(@"^(?:PV|CM|preview|trailer|teaser|promo|spot)\s*\d{0,2}$", Options, Timeout);
    private static readonly Regex SpecialSlot = new(@"^(?:SP|special|OVA|OAD|ONA|recap|pilot)\s*\d{0,3}$", Options, Timeout);
    private static readonly Regex Placeholder = new(@"^(?:X{2,3}|#{1,3}|\?{1,3})$", Options, Timeout);
    // Words in a clip's own title, for clips whose slot only says it isn't an episode.
    private static readonly Regex OtherWords = new(@"\b(?:gallery|menu|interview|making|storyboards?|featurettes?|behind|bloopers?|deleted|commentary|art|photos?|stills)\b", Options, Timeout);
    private static readonly Regex OpeningWords = new(@"\b(?:opening|intro|NC\s?OP\d{0,2})\b", Options, Timeout);
    private static readonly Regex EndingWords = new(@"\b(?:ending|closing|outro|NC\s?ED\d{0,2})\b", Options, Timeout);
    private static readonly Regex TrailerWords = new(@"\b(?:trailers?|teasers?|promos?|previews?|commercials?|PV|CM)\b", Options, Timeout);
    private static readonly Regex SpecialWords = new(@"\b(?:special|OVA|OAD|recap|movie|pilot)\b", Options, Timeout);
    private static readonly Regex Year = new(@"\b(?:19|20)\d{2}\b", Options, Timeout);

    // The kind of a clip from its slot word, or from its title when the slot says nothing.
    internal static string? Kind(string? slot, string? title = null)
    {
        if (slot is not null)
        {
            slot = slot.Trim();
            if (OpeningSlot.IsMatch(slot)) return Opening;
            if (EndingSlot.IsMatch(slot)) return Ending;
            if (TrailerSlot.IsMatch(slot)) return Trailer;
            if (SpecialSlot.IsMatch(slot)) return Special;
        }
        if (string.IsNullOrWhiteSpace(title) || OtherWords.IsMatch(title)) return null;
        if (OpeningWords.IsMatch(title)) return Opening;
        if (EndingWords.IsMatch(title)) return Ending;
        if (TrailerWords.IsMatch(title)) return Trailer;
        return SpecialWords.IsMatch(title) ? Special : null;
    }
    internal static bool IsSlot(string slot) => Kind(slot) is not null || Placeholder.IsMatch(slot.Trim());

    // A cleaned file name is a clip of folderSeries when it starts with that name and a dash but is no episode of it.
    // Without a folder series, only a stand-in number ("Show - XX - Title") marks a clip.
    internal static Clip? Read(string name, Folder? folder)
    {
        var folderSeries = folder?.Series;
        string series, rest;
        if (folderSeries is not null && name.Length > folderSeries.Length && name.StartsWith(folderSeries, StringComparison.OrdinalIgnoreCase) &&
            Regex.Match(name[folderSeries.Length..], @"^\s*-\s+(?<rest>\S.*)$", Options, Timeout) is { Success: true } after)
        {
            series = folderSeries; rest = after.Groups["rest"].Value;
        }
        else if (Regex.Match(name, @"^(?<series>.*\S)\s+-\s+(?<rest>(?:X{2,3}|#{1,3}|\?{1,3})(?:\s+-\s+\S.*)?)$", Options, Timeout) is { Success: true } placeholder)
        {
            series = placeholder.Groups["series"].Value; rest = placeholder.Groups["rest"].Value;
        }
        else return null;
        var dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        var slot = dash < 0 ? rest : rest[..dash];
        if (IsSlot(slot))
        {
            var title = dash < 0 ? Placeholder.IsMatch(slot.Trim()) ? rest : slot.Trim() : rest[(dash + 3)..].Trim();
            return new(series, title, Kind(slot, title), Kind(slot) is not null);
        }
        // "Show - The Movie (2010)" is a film of its own, not a clip.
        if (Year.IsMatch(rest)) return null;
        return new(series, rest.Trim(), Kind(null, rest));
    }

    // The show a folder's numbered episodes name, read once per folder until its contents change. Anime: those episodes
    // count as anime in a Mixed folder because a fansub group's tag leads their names and they carry no season.
    internal sealed record Folder(string Series, bool Anime);
    private static readonly ConcurrentDictionary<(string Folder, bool SeriesContext), (DateTime Written, Folder? Show)> Folders = new();
    internal static Folder? FolderSeries(string folder, bool seriesContext)
    {
        DateTime written;
        try { written = Directory.GetLastWriteTimeUtc(folder); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        if (Folders.TryGetValue((folder, seriesContext), out var known) && known.Written == written) return known.Show;
        Folder? show = null;
        try
        {
            show = Directory.EnumerateFiles(folder).Where(file => FileNames.Extensions.Contains(Path.GetExtension(file)) && !FileNames.IsExtra(file))
                .Select(file => (File: file, Numbering: FilenameEpisodes.Read(file, seriesContext)))
                .Where(x => x.Numbering is { Episode: not null, AirDate: null } && !string.IsNullOrWhiteSpace(x.Numbering.Series))
                .GroupBy(x => x.Numbering!.Series, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count())
                .Select(g => new Folder(g.Key, g.Count(x => !x.Numbering!.ExplicitSeason && Path.GetFileName(x.File).StartsWith('[')) * 2 > g.Count())).FirstOrDefault();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Folders[(folder, seriesContext)] = (written, show);
        return show;
    }
}
