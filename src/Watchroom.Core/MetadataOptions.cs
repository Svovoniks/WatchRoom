namespace Watchroom.Core;

public record MetadataOptions(string[]? Providers = null, string Language = "en-US", string Country = "US", int RefreshDays = 7,
    bool ImportMissing = false, bool ImportUpcoming = false, bool SaveNfo = false, bool DisplaySpecialsWithinSeasons = false, bool GroupShowsByProvider = false)
{
    public string[] ProviderOrder => Providers ?? ["tmdb", "tvmaze", "wikipedia"];
    public static MetadataOptions Load(LibraryStore library)
    {
        try { return library.Setting("metadata-options") is { } json ? Wire.Read<MetadataOptions>(json) : new(); }
        catch (System.Text.Json.JsonException) { return new(); }
    }
    public void Save(LibraryStore library) => library.Setting("metadata-options", Wire.Serialize(this));
}
public enum MetadataRefresh { FillMissing, RefreshText, ReplaceArtwork }

public static class MetadataMatching
{
    public static bool Compatible(MediaItem item, MetadataMatch match) => item.Matched || item.MetadataLocked ||
        !(MetadataTitles.IsAnimeSource(item) && match.Animated == false);
    public static bool HasMovieEvidence(MediaItem item) => item.Episode is null && item.AbsoluteEpisode is null && item.SourcePartEpisode is null &&
        (item.MetadataType == "movie" || item.SourceLibraryKind == "Movie" || item.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => System.Text.RegularExpressions.Regex.IsMatch(part, @"(?i)^(?:Movies?|Films?|Фильмы)$")));
    public static MetadataMatch? Choose(IEnumerable<MetadataMatch> results, MediaItem item) =>
        Choose(results.Where(match => Compatible(item, match) && (item.Matched || item.MetadataLocked ||
            (item.Episode is not null || item.SourcePartEpisode is not null ? match.Type == "tv" : !HasMovieEvidence(item) || match.Type == "movie"))), item.DisplayTitle, item.Year,
            MetadataTitles.IsAnimeSource(item) ? "Anime" : null);
    public static int Score(string title, int? year, string candidate, string? original, int? candidateYear)
    {
        static int Title(string a, string b)
        {
            var left = MetadataTitles.Normalize(a); var right = MetadataTitles.Normalize(b);
            if (left.Length == 0) return 0;
            if (left == right) return 8;
            if (System.Text.RegularExpressions.Regex.IsMatch(a + b, @"\p{IsCyrillic}") && MetadataTitles.Romanized(a) == MetadataTitles.Romanized(b)) return 8;
            // Prefix matching only at word boundaries; never "Wall" against "Wallstreet".
            var clean = System.Text.RegularExpressions.Regex.Replace(b, @"[^\p{L}\p{N}\p{M}]+", " ").Trim();
            return clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)
                .Select((_, index) => string.Join(" ", clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(index + 1)))
                .Any(prefix => MetadataTitles.Normalize(prefix) == left) ?
                    (a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4 && left.Length >= 20 ? 6 : 4) : 0;
        }
        var score = Math.Max(Title(title, candidate), Title(title, original ?? ""));
        if (year is { } expected && candidateYear is { } actual) score += Math.Abs(expected - actual) switch { 0 => 2, 1 => 1, _ => -4 };
        return score;
    }
    public static MetadataMatch? Choose(IEnumerable<MetadataMatch> results, string title, int? year, string? preferredKind = null)
    {
        var ranked = results.DistinctBy(x => (x.Type, x.Id)).Select(x => (Match: x,
            Score: Score(title, year, x.Title, x.OriginalTitle, int.TryParse(x.Year, out var y) ? y : null)))
            .Select(x => (x.Match, x.Score, Rank: x.Score + (preferredKind is not null && (x.Match.Kind == preferredKind || preferredKind == "Anime" && x.Match.Animated == true) ? 2 : 0))).OrderByDescending(x => x.Rank).ToArray();
        // Keep conservative automatic matching: weak or tied results require Identify.
        return ranked.Length > 0 && ranked[0].Score >= 6 && (ranked.Length == 1 || ranked[0].Rank > ranked[1].Rank) ? ranked[0].Match : null;
    }
}
