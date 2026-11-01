using System.Text.RegularExpressions;

namespace BlackPearl.Core;

public sealed partial class MetadataClient
{
    private static int EvidenceTitleScore(MediaItem source, MetadataMatch match)
    {
        var names = new[] { match.Title, match.OriginalTitle ?? "" }.Concat(match.Aliases ?? []);
        return names.Select(name =>
        {
            var exact = MetadataMatching.Score(source.DisplayTitle, null, name, null, null);
            if (exact >= 6) return exact;
            // A missing letter in informal romanization may also precede an omitted
            // subtitle. This is only search evidence; runtime must confirm it.
            if (MetadataTitles.RussianSearchAlias(source.DisplayTitle) is null && !Regex.IsMatch(source.DisplayTitle, @"\p{IsCyrillic}")) return 0;
            var words = Regex.Split(source.DisplayTitle.Trim(), @"\s+");
            var candidateWords = Regex.Split(Regex.Replace(name, @"[^\p{L}\p{N}]+", " ").Trim(), @"\s+");
            if (words.Length < 2 || candidateWords.Length < words.Length) return 0;
            var left = MetadataTitles.Romanized(source.DisplayTitle);
            var right = MetadataTitles.Romanized(string.Join(" ", candidateWords.Take(words.Length)));
            return left.Length >= 10 && OneEdit(left, right) ? 6 : 0;
        }).DefaultIfEmpty().Max();
    }
    private static bool OneEdit(string left, string right)
    {
        // Numeric differences identify sequels, never spelling corrections.
        if (!Regex.Matches(left, @"\d+").Select(x => x.Value).SequenceEqual(Regex.Matches(right, @"\d+").Select(x => x.Value))) return false;
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < left.Length && j < right.Length)
        {
            if (left[i] == right[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (left.Length >= right.Length) i++;
            if (right.Length >= left.Length) j++;
        }
        return edits + left.Length - i + right.Length - j <= 1;
    }
    private async Task<MetadataMatch?> MatchMovieEvidenceAsync(MediaItem item,
        IEnumerable<(MediaItem Item, MetadataMatch[] Candidates)> searches, CancellationToken ct)
    {
        if (item.Matched || item.MetadataLocked || item.Episode is not null || item.Series is not null || item.MetadataType == "tv") return null;
        var candidates = searches.SelectMany(search => search.Candidates.Where(match => match.Type == "movie" && MetadataMatching.Compatible(item, match) &&
            // Undated local files cannot use an unreleased future adaptation as
            // a runtime rival. Explicit filename years still retain their rivals.
            (item.Year is not null || !int.TryParse(match.Year, out var releaseYear) || releaseYear <= DateTime.UtcNow.Year) &&
            (item.Year is null || !int.TryParse(match.Year, out var year) || Math.Abs(item.Year.Value - year) <= 1) && EvidenceTitleScore(search.Item, match) >= 6))
            .DistinctBy(x => (x.Provider, x.Type, x.Id)).ToArray();
        MetadataDiagnostics.Record("metadata-evidence-candidates", new { Candidates = candidates.Select(x => new { x.Id, x.Title, x.Year }).ToArray() });
        // All plausible rivals must be checked; don't pick from a truncated set.
        if (candidates.Length is 0 or > 8) return null;
        var duration = await Task.Run(() => MediaDuration.Minutes(item.Path, ct), ct);
        if (duration is null) return null;
        var details = await Task.WhenAll(candidates.Select(x => DetailsAsync(x, ct)));
        MetadataDiagnostics.Record("metadata-evidence-runtimes", new { LocalRuntime = duration, Candidates = details.Select(x => new { x.Id, x.RuntimeMinutes }).ToArray() });
        var tolerance = Math.Max(3, duration.Value * .05);
        var close = details.Where(x => x.RuntimeMinutes is > 0 && Math.Abs(x.RuntimeMinutes.Value - duration.Value) <= tolerance).ToArray();
        if (close.Length != 1) return null;
        var winner = close[0];
        if (item.Year is not null && (!int.TryParse(winner.Year, out var winnerYear) || Math.Abs(item.Year.Value - winnerYear) > 1)) return null;
        var others = details.Where(x => x.Id != winner.Id).ToArray();
        var unknown = others.Any(x => x.RuntimeMinutes is null);
        // Small runtime differences and missing information cannot silently break ties.
        if (others.Any(x => x.RuntimeMinutes is { } runtime && Math.Abs(runtime - duration.Value) < tolerance + 2)) return null;
        var netflix = Regex.IsMatch(Path.GetFileName(item.Path), @"(?i)(?:^|[._ -])(?:NF|NETFLIX)(?:[._ -])") &&
            Regex.IsMatch(Path.GetFileName(item.Path), @"(?i)WEB[._ -]?(?:RIP|DL)");
        if (unknown)
        {
            if (!netflix) return null;
            var sources = await Task.WhenAll(details.Select(async x => (x.Id, Available: await NetflixAsync(x, ct))));
            if (sources.Count(x => x.Available) != 1 || !sources.Single(x => x.Id == winner.Id).Available) return null;
        }
        MetadataDiagnostics.Record("metadata-match-evidence", new { winner.Id, LocalRuntime = duration, winner.RuntimeMinutes, Netflix = unknown && netflix });
        MetadataDiagnostics.Outcome("matched");
        return winner with { Evidence = unknown ? "File duration and Netflix release agree" : "File duration distinguishes the title" };
    }
    private async Task<bool> NetflixAsync(MetadataMatch match, CancellationToken ct)
    {
        using var json = await Get($"movie/{match.Id}/watch/providers", ct);
        if (!json.RootElement.TryGetProperty("results", out var countries) || countries.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new System.Text.Json.JsonException("Invalid streaming-provider response.");
        var found = false;
        foreach (var country in countries.EnumerateObject())
        {
            if (country.Value.ValueKind != System.Text.Json.JsonValueKind.Object) throw new System.Text.Json.JsonException("Invalid streaming-provider region.");
            foreach (var type in new[] { "flatrate", "ads", "free" })
            {
                if (!country.Value.TryGetProperty(type, out var providers)) continue;
                if (providers.ValueKind != System.Text.Json.JsonValueKind.Array) throw new System.Text.Json.JsonException("Invalid streaming-provider list.");
                foreach (var provider in providers.EnumerateArray())
                {
                    if (provider.ValueKind != System.Text.Json.JsonValueKind.Object || !provider.TryGetProperty("provider_name", out var name) || name.ValueKind != System.Text.Json.JsonValueKind.String)
                        throw new System.Text.Json.JsonException("Invalid streaming-provider name.");
                    found |= name.GetString()?.StartsWith("Netflix", StringComparison.OrdinalIgnoreCase) == true;
                }
            }
        }
        return found;
    }
    private async Task<MetadataMatch?> MatchNumberedSeriesAsync(MediaItem item, IReadOnlyList<string> queries, IEnumerable<MediaItem>? entries, CancellationToken ct)
    {
        var group = MetadataTitles.NumberedMovieGroup(item, entries);
        if (group.Length == 0 || LibraryIdentity.Locked(item, "Kind") || LibraryIdentity.Locked(item, "Episode") || LibraryIdentity.Locked(item, "Season")) return null;
        foreach (var query in queries)
        {
            var source = item with { Title = query, Series = query, Season = 1, Episode = MetadataTitles.NumberedMoviePart(item), MetadataType = "tv" };
            var candidates = await SearchAsync(query, true, ct, item.Year);
            var match = await MetadataMatching.ChooseWithAliasesAsync(candidates, source, AlternativeTitlesAsync, ct);
            if (match is null) continue;
            var details = await DetailsAsync(match, ct);
            if (details.SeasonCount != 1 || details.EpisodeCount != group.Length) continue;
            // Verify the actual episode numbers too, not just an aggregate count.
            var season = await SeasonAsync(details.Id, 1, ct);
            if (season is null || !season.Episodes.Select(x => x.Number).Order().SequenceEqual(Enumerable.Range(1, group.Length))) continue;
            MetadataDiagnostics.Record("metadata-match-evidence", new { details.Id, NumberedFiles = group.Length, Season = 1 });
            MetadataDiagnostics.Outcome("matched");
            return details with { NumberedSeries = true, Evidence = "Numbered files agree with a complete single-season series" };
        }
        return null;
    }
}
