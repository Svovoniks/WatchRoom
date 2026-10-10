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
            Score: CandidateScore(x, title, year)))
            .Select(x => (x.Match, x.Score, Rank: x.Score + (preferredKind is not null && (x.Match.Kind == preferredKind || preferredKind == "Anime" && x.Match.Animated == true) ? 2 : 0))).OrderByDescending(x => x.Rank).ToArray();
        // Keep conservative automatic matching: weak or tied results require Identify.
        return ranked.Length > 0 && ranked[0].Score >= 6 && (ranked.Length == 1 || ranked[0].Rank > ranked[1].Rank) ? ranked[0].Match : null;
    }
    private static int CandidateScore(MetadataMatch match, string title, int? year) =>
        new[] { match.Title, match.OriginalTitle ?? "" }.Concat(match.Aliases ?? [])
            .Max(alias => Score(title, year, alias, null, int.TryParse(match.Year, out var y) ? y : null));

    internal static async Task<MetadataMatch?> ChooseWithAliasesAsync(IEnumerable<MetadataMatch> results, MediaItem item,
        Func<MetadataMatch, CancellationToken, Task<string[]>> aliases, CancellationToken ct, Action<MetadataMatch[]?>? unresolved = null)
    {
        ct.ThrowIfCancellationRequested();
        var candidates = results.DistinctBy(x => (x.Type, x.Id)).ToArray();
        if (Choose(candidates, item) is { } exact) return exact;
        // Do not truncate a crowded result set and hide an unchecked competitor.
        var shortlist = candidates.Where(x => Compatible(item, x) &&
            (item.Episode is null && item.Series is null || x.Type == "tv") && (!HasMovieEvidence(item) || x.Type == "movie") &&
            (item.Year is null || !int.TryParse(x.Year, out var year) || Math.Abs(item.Year.Value - year) <= 1)).ToArray();
        if (shortlist.Length == 0) return null;
        if (shortlist.Length > 4) { unresolved?.Invoke(null); return null; }
        try
        {
            var enriched = await Task.WhenAll(shortlist.Select(async x => x with { Aliases = await aliases(x, ct) }));
            if (Choose(enriched, item) is { } aliasMatch) return aliasMatch;
            if (unresolved is not null) { unresolved(enriched); return null; }
            return ChooseTypo(enriched, item);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        { unresolved?.Invoke(null); return null; } // Missing evidence cannot make a partial shortlist look unique.
    }
    internal static MetadataMatch? ChooseTypoFallback(IEnumerable<(MediaItem Item, MetadataMatch[]? Candidates)> searches)
    {
        var attempts = searches.ToArray();
        if (attempts.Any(x => x.Candidates is null || x.Candidates.Any(c => CandidateScore(c, x.Item.DisplayTitle, x.Item.Year) >= 6))) return null;
        var all = attempts.SelectMany(x => x.Candidates!).GroupBy(x => (x.Type, x.Id)).Select(group => group.First() with
        { Aliases = group.SelectMany(x => new[] { x.Title, x.OriginalTitle ?? "" }.Concat(x.Aliases ?? [])).Distinct().ToArray() }).ToArray();
        // Different filename/folder queries must agree on one provider identity.
        var matches = attempts.Select(x => ChooseTypo(all, x.Item)).OfType<MetadataMatch>().DistinctBy(x => (x.Type, x.Id)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    internal static MetadataMatch? ChooseTypo(IEnumerable<MetadataMatch> results, MediaItem item)
    {
        var expectedType = item.MetadataType ?? (item.Series is not null ? "tv" : HasMovieEvidence(item) ? "movie" : null);
        if (item.Year is null || expectedType is null) return null;
        var candidates = results.Where(x => Compatible(item, x)).DistinctBy(x => (x.Type, x.Id)).ToArray();
        if (candidates.Any(x => CandidateScore(x, item.DisplayTitle, item.Year) >= 6)) return null;
        // Include near names with unknown/adjacent years as competitors, even
        // though only an exact-year winner can be accepted.
        var ranked = candidates.Select(x => (Match: x, Distance: new[] { x.Title, x.OriginalTitle ?? "" }.Concat(x.Aliases ?? [])
            .Select(name => TypoDistance(item.DisplayTitle, name)).Min())).OrderBy(x => x.Distance).ToArray();
        if (ranked.Length == 0 || ranked[0].Distance != 1 || ranked.Length > 1 && ranked[1].Distance <= 2) return null;
        var best = ranked[0].Match;
        return best.Type == expectedType && int.TryParse(best.Year, out var year) && year == item.Year &&
            (!MetadataTitles.IsAnimeSource(item) || best.Animated == true || best.Kind == "Anime") ? best : null;
    }
    private static int TypoDistance(string left, string right)
    {
        var a = MetadataTitles.Normalize(left); var b = MetadataTitles.Normalize(right);
        if (Math.Min(a.Length, b.Length) < 5 || Math.Max(a.Length, b.Length) > 160 || Math.Abs(a.Length - b.Length) > 2) return 3;
        // A typo must never change sequel numbers or numeric titles.
        if (!System.Text.RegularExpressions.Regex.Matches(left, @"\d+").Select(x => x.Value)
            .SequenceEqual(System.Text.RegularExpressions.Regex.Matches(right, @"\d+").Select(x => x.Value))) return 3;
        var distance = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) distance[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) distance[0, j] = j;
        for (var i = 1; i <= a.Length; i++) for (var j = 1; j <= b.Length; j++)
        {
            distance[i, j] = Math.Min(distance[i - 1, j] + 1, Math.Min(distance[i, j - 1] + 1, distance[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
        }
        return distance[a.Length, b.Length];
    }
}
