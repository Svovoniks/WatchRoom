using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// A folder of separately released shorts, like "002 Pink Pajamas (Dec 25, 1964)", reads as a show's numbered episodes, but no provider
// lists a show of them: each short is a film of its own. Each file is looked up by its own title, and accepted only when the year its
// name gives matches or its length does, since episode titles ("Pilot", "Money") also name unrelated films. The folder stays one title,
// and its files take their own films' details.
internal static class ShortFilms
{
    public const int MinimumFiles = 5;

    public static async Task<int> IdentifyAsync(LibraryStore library, MetadataClient tmdb, string directory, MediaItem[] entries, bool replaceText, CancellationToken ct)
    {
        var titled = entries.Where(x => !x.IsBonus && !x.IsVirtual && !x.IsExtra && x.Series is not null)
            .Select(x => (Item: x, Title: EpisodeOrder.OwnTitle(x))).Where(x => x.Title is { } title && EpisodeOrder.Words(title).Length > 0).ToArray();
        if (titled.Length < MinimumFiles) return 0;
        // A folder is searched once for its files as they are. Later passes keep that answer until the files change or a refresh replaces text.
        var key = "short-films-checked:" + LibraryIdentity.ShowKey(titled[0].Item);
        var fingerprint = LibraryIdentity.Hash(Wire.Serialize(new { MetadataFetchPolicy.MatcherVersion,
            Files = titled.Select(x => new { x.Item.Id, x.Title, Year = Year(x.Item), x.Item.FileRuntimeMinutes, x.Item.DurationSeconds }).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray() }));
        if (!replaceText && library.Setting(key) == fingerprint)
            return entries.Count(x => x.EpisodeSource?.StartsWith("https://www.themoviedb.org/movie/", StringComparison.Ordinal) == true);
        var found = new List<(MediaItem Item, MetadataMatch Film)>();
        foreach (var (item, title) in titled)
        {
            ct.ThrowIfCancellationRequested();
            var year = Year(item);
            int? minutes = item.FileRuntimeMinutes is > 0 ? item.FileRuntimeMinutes : item.DurationSeconds is > 0 ? (int)Math.Round(item.DurationSeconds.Value / 60.0) : null;
            // Without a year or a length, a title alone can't tell the file's film from another of that name.
            if (year is null && minutes is null) continue;
            var named = (await tmdb.SearchAsync(title!, false, ct, year)).Where(x => Same(title!, x.Title) || x.OriginalTitle is { } original && Same(title!, original)).ToArray();
            if (named.Length is 0 or > 4) continue;
            var fitting = new List<MetadataMatch>();
            foreach (var film in named)
            {
                var details = await tmdb.DetailsAsync(film, ct);
                // A length the provider gives has to agree with the file's; a known year can stand in for an unknown length.
                if (details.RuntimeMinutes is { } runtime ? minutes is null || Math.Abs(runtime - minutes.Value) <= Math.Max(2, runtime * 15 / 100) : year is not null)
                    fitting.Add(details);
            }
            if (fitting.Count == 1) found.Add((item, fitting[0]));
        }
        MetadataDiagnostics.Record("metadata-short-films", new { Files = titled.Length, Found = found.Count });
        // Most of the folder has to be films found this way, or its titles were episodes after all.
        if (found.Count < MinimumFiles || found.Count * 10 < titled.Length * 6) { library.Setting(key, fingerprint); return 0; }
        var posters = Path.Combine(directory, "posters");
        string? cardPoster = null;
        foreach (var (item, film) in found)
        {
            var current = library.ByIds([item.Id]).FirstOrDefault();
            if (current is null || !AutomaticArtwork.SameFetchIdentity(item, current)) continue;
            string? image = null;
            if (film.Poster is { } poster && (!LibraryIdentity.Locked(current, "EpisodePoster") || cardPoster is null))
                try { image = await tmdb.CacheImageAsync(poster, $"movie-{film.Id}", posters, ct); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested) { MetadataDiagnostics.Failure("short-film-artwork", current.DisplayTitle, "tmdb", ex); }
            cardPoster ??= image;
            string? Keep(string field, string? value, string? old) => LibraryIdentity.Locked(current, field) || string.IsNullOrWhiteSpace(value) ||
                !replaceText && !string.IsNullOrWhiteSpace(old) && current.EpisodeSource is not null ? old : value;
            var updated = current with
            {
                EpisodeOverview = Keep("EpisodeOverview", film.Overview, current.EpisodeOverview),
                AirDate = Keep("AirDate", film.ReleaseDate, current.AirDate),
                RuntimeMinutes = LibraryIdentity.Locked(current, "RuntimeMinutes") ? current.RuntimeMinutes : film.RuntimeMinutes ?? current.RuntimeMinutes,
                EpisodePoster = LibraryIdentity.Locked(current, "EpisodePoster") ? current.EpisodePoster : image ?? current.EpisodePoster,
                EpisodeSource = $"https://www.themoviedb.org/movie/{film.Id}",
                MetadataFetchedAt = Wire.Now
            };
            library.Save(TmdbRetention.Record(current, updated, "tmdb", "episodeOverview", "airDate", "runtimeMinutes", "episodePoster", "episodeSource"));
        }
        // The card shows the first short's image unless it already has a poster. It names no source: a lookup reads a poster's provider
        // page as the title's own identity, and the title is the folder, not that one short.
        if (cardPoster is not null)
            foreach (var entry in library.ByIds(entries.Select(x => x.Id)).Where(x => !LibraryIdentity.Locked(x, "Poster") && !ArtworkCache.IsUsable(x.SeriesPoster) && !ArtworkCache.IsUsable(x.Poster)))
                library.Save(TmdbRetention.Record(entry, entry with { Poster = cardPoster, SeriesPoster = cardPoster, PosterSource = null }, "tmdb", TmdbRetention.PosterFields));
        library.Setting(key, fingerprint);
        return found.Count;
    }

    private static bool Same(string left, string right) => MetadataTitles.Normalize(left) is { Length: > 0 } normalized && normalized == MetadataTitles.Normalize(right);

    // The release year the file's own name gives: "(Dec 25, 1964)", "1964-12-25" or "(1964)". A show's year or a provider's date says nothing of one short.
    private static int? Year(MediaItem item)
    {
        var years = Regex.Matches(Path.GetFileNameWithoutExtension(item.Path), @"(?<![\d])(?:19|20)\d{2}(?![\d])").Select(x => int.Parse(x.Value)).Where(x => x <= DateTime.UtcNow.Year + 1).ToArray();
        return years.Length > 0 ? years[^1] : null;
    }
}
