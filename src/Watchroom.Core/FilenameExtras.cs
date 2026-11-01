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
    // A bare title after the show's name is a clip only when it says so; "Batman - Mask of the Phantasm" and "Batman - The Movie" are films.
    private static readonly Regex ClipWords = new(@"\b(?:gallery|menu|interview|making|storyboards?|featurettes?|behind|bloopers?|deleted|commentary|art|photos?|stills|" +
        @"opening|intro|ending|closing|outro|NC\s?(?:OP|ED)\d{0,2}|trailers?|teasers?|promos?|previews?|commercials?|PV|CM|special|OVA|OAD|recap|pilot)\b", Options, Timeout);
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

    // A cleaned file name is a clip of the folder's show when it starts with that name and a dash but is no episode of it.
    // Without a folder show, only a stand-in number ("Show - XX - Title") marks a clip. The folder is only read for a name
    // with a dash in it, so opening an ordinary episode or film never lists its folder.
    internal static Clip? Read(string name, Func<string, Folder?> folderShow)
    {
        if (!name.Contains(" - ", StringComparison.Ordinal)) return null;
        var folderSeries = folderShow(name)?.Series;
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
        // "One Piece - 492A" and "Show - 12.5" carry episode numbers the parser can't place; they stay episodes.
        if (char.IsDigit(rest[0])) return null;
        var dash = rest.IndexOf(" - ", StringComparison.Ordinal);
        var slot = dash < 0 ? rest : rest[..dash];
        if (IsSlot(slot))
        {
            var tail = dash < 0 ? null : rest[(dash + 3)..].Trim();
            // "Batman - Special - 01" is the clip "Special 01"; "Show - XX" has only its stand-in to go by.
            var title = tail is null ? slot.Trim() : Regex.IsMatch(tail, @"^\d{1,3}$") ? $"{slot.Trim()} {tail}" : tail;
            return new(series, title, Kind(slot, title), Kind(slot) is not null);
        }
        // "Show - The Movie (2010)" is a film of its own, not a clip.
        if (Year.IsMatch(rest) || !ClipWords.IsMatch(rest)) return null;
        return new(series, rest.Trim(), Kind(null, rest));
    }

    // The show whose numbered episodes sit beside a file and whose name starts the file's name. Anime: those episodes count as
    // anime in a Mixed folder because a fansub group's tag leads their names and they carry no season. Only files whose
    // names contain the candidate are read, and only until three episodes confirm it, so a large folder costs a listing.
    internal sealed record Folder(string Series, bool Anime);
    private static readonly ConcurrentDictionary<string, (DateTime Written, string[] Videos)> Listings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string File, bool SeriesContext, string Candidate), (DateTime Written, Folder? Show)> Shows = new();
    internal static Folder? FolderShow(string path, bool seriesContext, string name)
    {
        var folder = Path.GetDirectoryName(path)!;
        DateTime written;
        string[] videos;
        try
        {
            written = Directory.GetLastWriteTimeUtc(folder);
            videos = Listings.TryGetValue(folder, out var listed) && listed.Written == written ? listed.Videos
                : Directory.EnumerateFiles(folder).Where(file => FileNames.Extensions.Contains(Path.GetExtension(file))).ToArray();
            Listings[folder] = (written, videos);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        // Shortest first, so "Batman - Special - 01" and its siblings join the episodes of "Batman" rather than confirm a show "Batman - Special";
        // "Show - Part 2 - NCOP" still finds "Show - Part 2 - 01" when no episode is named plain "Show". The file itself confirms nothing.
        for (var cut = name.IndexOf(" - ", StringComparison.Ordinal); cut >= 0; cut = name.IndexOf(" - ", cut + 3, StringComparison.Ordinal))
        {
            var candidate = name[..cut].Trim();
            if (candidate.Length == 0) continue;
            var key = (path.ToUpperInvariant(), seriesContext, candidate.ToUpperInvariant());
            if (!Shows.TryGetValue(key, out var known) || known.Written != written)
            {
                int found = 0, anime = 0;
                foreach (var file in videos)
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(file, path, StringComparison.OrdinalIgnoreCase) || !fileName.Replace('.', ' ').Replace('_', ' ').Contains(candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    if (FileNames.IsExtra(file) || FilenameEpisodes.Read(file, seriesContext) is not { Episode: not null, AirDate: null } numbering ||
                        !string.Equals(numbering.Series, candidate, StringComparison.OrdinalIgnoreCase)) continue;
                    found++;
                    if (!numbering.ExplicitSeason && fileName.StartsWith('[')) anime++;
                    if (found == 3) break;
                }
                known = (written, found > 0 ? new Folder(candidate, anime * 2 > found) : null);
                Shows[key] = known;
            }
            if (known.Show is not null) return known.Show;
        }
        return null;
    }
}
