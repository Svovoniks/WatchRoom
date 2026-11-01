using System.Text.RegularExpressions;

namespace Watchroom.Core;

// Version names the release an episode card shows when the library holds several files of that episode.
// Status marks a season with no file at all: "Missing", or "Upcoming" when none of its episodes has aired.
public record LibraryCard(string DisplayTitle, string Caption, string? Poster, MediaItem Media, string Level, int? Season = null, string? Version = null, int Versions = 1, string? Status = null);

public static class LibraryCatalog
{
    // The Music library category; 4 and 5 are the Unavailable and Missing views.
    public const int Music = 6;
    public static Task<List<LibraryCard>> SearchAsync(IEnumerable<MediaItem> source, string query, int filter = 0,
        string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false, string? preferVersion = null) => Task.Run(() =>
    {
        var visible = source.Where(item =>
        {
            cancellation.ThrowIfCancellationRequested();
            return filter switch
            {
                1 => item.Kind == "Movie", 2 => item.Kind == "Show", 3 => item.Kind == "Anime", Music => item.IsMusic,
                4 => !File.Exists(item.Poster) && !File.Exists(item.SeriesPoster) && !File.Exists(item.SeasonPoster),
                5 => !item.Available, _ => true
            };
        });
        return Search(visible, query, series, kind, season, cancellation, displaySpecials, preferVersion);
    }, cancellation);
    public static IEnumerable<MediaItem> CardItems(IEnumerable<MediaItem> source, LibraryCard card, bool displaySpecials = false, string? preferVersion = null) =>
        PreferredFiles(card.Level is "series" or "season"
            ? source.Where(x => LibraryIdentity.SameShow(x, card.Media) && (card.Level != "season" || (x.Season ?? -1) == card.Season || displaySpecials && card.Season > 0 && x.Season == 0 && (x.AirsBeforeSeason == card.Season || x.AirsAfterSeason == card.Season)))
            : source.Where(x => x.Id == card.Media.Id), preferVersion)
        .Where(x => !x.IsVirtual).OrderBy(x => displaySpecials && x.Season == 0 ? x.AirsBeforeSeason ?? x.AirsAfterSeason ?? 0 : x.Season ?? int.MaxValue)
        .ThenBy(x => displaySpecials && x.Season == 0 && (x.AirsBeforeSeason is not null || x.AirsAfterSeason is not null)
            ? x.AirsBeforeSeason is not null ? (x.AirsBeforeEpisode ?? 1) - 0.5 : double.MaxValue : x.Episode ?? int.MaxValue).ThenBy(x => x.Path);
    public static List<LibraryCard> Search(IEnumerable<MediaItem> source, string query, string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false, string? preferVersion = null) =>
        Browse(source, series, kind, season, cancellation, displaySpecials, preferVersion).Where(card =>
        {
            cancellation.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(query) || new[] { card.DisplayTitle, card.Caption, card.Media.Title, card.Media.Path }
                .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        }).ToList();
    private static string? Existing(IEnumerable<string?> paths, CancellationToken cancellation = default) => paths.FirstOrDefault(p =>
    {
        cancellation.ThrowIfCancellationRequested();
        return ArtworkCache.IsUsable(p);
    });
    // A poster shared from the show or season is not episode art, whatever its recorded source.
    public static string? EpisodeArtwork(MediaItem item, CancellationToken cancellation = default) => Existing(item.PosterSource is null or "Local artwork" &&
        !string.Equals(item.Poster, item.SeriesPoster, StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Poster, item.SeasonPoster, StringComparison.OrdinalIgnoreCase)
        ? [item.Poster, item.EpisodePoster, item.GeneratedEpisodePoster, item.SeasonPoster, item.SeriesPoster]
        : [item.EpisodePoster, item.GeneratedEpisodePoster, item.SeasonPoster, item.SeriesPoster, item.Poster], cancellation);
    // One file per episode. A preferred version name, such as "Color", picks that release wherever an episode has several.
    public static IEnumerable<MediaItem> PreferredFiles(IEnumerable<MediaItem> source, string? preferVersion = null)
    {
        var all = source as IReadOnlyCollection<MediaItem> ?? source.ToArray();
        var preferred = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(preferVersion))
            foreach (var group in all.Where(x => !x.IsVirtual && x.Series is not null && x.Season is not null && x.Episode is not null && x.NumberingConflict is null)
                .GroupBy(x => (Show: LibraryIdentity.ShowKey(x), x.Season, x.Episode, x.EpisodeEnd)).Where(g => g.Count() > 1))
            {
                var versions = group.ToArray();
                foreach (var version in versions.Where(v => VersionLabel(v, versions).Equals(preferVersion, StringComparison.OrdinalIgnoreCase))) preferred.Add(version.Id);
            }
        var covered = new HashSet<string>();
        foreach (var item in all.OrderBy(x => x.IsVirtual).ThenByDescending(x => x.Available).ThenByDescending(x => preferred.Contains(x.Id))
            .ThenByDescending(x => (x.EpisodeEnd ?? x.Episode) - x.Episode).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            // An album has no versions: tracks that share a disc and number are different songs.
            var keys = item.IsMusic || item.Season is null || item.NumberingConflict is not null ? [] :
                LibraryIdentity.EpisodeNumbers(item).Select(n => $"{LibraryIdentity.ShowKey(item)}:{item.Season}:{n}").ToArray();
            if (keys.Length > 0 && keys.All(covered.Contains)) continue;
            foreach (var key in keys) covered.Add(key);
            yield return item;
        }
    }
    public static List<LibraryCard> Browse(IEnumerable<MediaItem> source, string? series = null, string? kind = null, int? season = null, CancellationToken cancellation = default, bool displaySpecials = false, string? preferVersion = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var items = source.Where(item => !item.IsExtra).Select(item => { cancellation.ThrowIfCancellationRequested(); return item; }).ToArray();
        if (series is null)
        {
            // A film's clips show on the film's page, not as cards of their own.
            var filmExtras = items.Where(x => x.ExtraOf is not null).ToLookup(x => x.ExtraOf!);
            return items.Where(x => x.ExtraOf is null).GroupBy(LibraryIdentity.ShowKey, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                cancellation.ThrowIfCancellationRequested();
                var item = g.OrderBy(x => x.IsVirtual).ThenByDescending(x => x.Available).First();
                var seasons = g.Select(x => x.Season).Distinct().Count();
                var clips = item.Series is null ? filmExtras[item.Id].Count() : 0;
                var status = TitleStatus(g.ToArray());
                if (item.IsMusic) return AlbumCard(g.ToArray(), item, cancellation);
                return new LibraryCard(item.DisplayTitle, item.Series is null ? item.Caption + (clips > 0 ? $" · {clips} {(clips == 1 ? "extra" : "extras")}" : "") +
                    (item.IsVirtual ? " · " + status + (status == "Upcoming" && DateOnly.TryParse(item.AirDate, out var release) ? " · " + release.ToString("MMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture) : "") : item.Available ? "" : " · Unavailable") :
                    (item.Year is null ? "" : item.Year + " · ") + $"{seasons} {(seasons == 1 ? "season" : "seasons")} · {LibraryIdentity.EpisodeCount(g)} episodes" +
                    (g.Any(x => x.IsVirtual) ? $" · {g.Count(x => x.IsVirtual)} missing/upcoming" : "") + (g.Any(x => x.Available) || g.All(x => x.IsVirtual) ? "" : " · Unavailable"),
                    Existing(g.Select(x => x.SeriesPoster).Concat(g.Select(x => x.Poster)).Concat(g.Select(x => x.SeasonPoster)), cancellation), item, item.Series is null ? "movie" : "series", Status: status);
            }).OrderBy(x => x.DisplayTitle).ToList();
        }
        var episodes = items.Where(x => LibraryIdentity.InShow(x, series, kind));
        // An album lists its tracks straight away, disc by disc.
        if (kind == MusicFiles.Kind || episodes.Any() && episodes.All(x => x.IsMusic))
        {
            var discs = episodes.Select(e => e.Season).Distinct().Count();
            return episodes.OrderBy(x => x.Season ?? int.MaxValue).ThenBy(x => x.Episode ?? int.MaxValue).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => new LibraryCard(x.EpisodeDisplayTitle, string.Join(" · ", new[] { x.Artist, x.DurationSeconds is > 0 ? MusicFiles.Duration(x.DurationSeconds.Value) : null,
                    discs > 1 ? $"Disc {x.Season}" : null, x.Available ? null : "Unavailable" }.Where(t => t is not null)),
                    EpisodeArtwork(x, cancellation), x, "episode", x.Season)).ToList();
        }
        if (season is null)
            // Specials and extras follow the numbered seasons they accompany; a season counts its missing episodes as its show does.
            return episodes.GroupBy(x => x.Season ?? -1).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key == 0 ? int.MaxValue - 1 : g.Key).Select(g =>
            {
                var status = TitleStatus(g.ToArray());
                return new LibraryCard(SeasonName(g.Key, g),
                    SeasonCaption(g) + (g.Key > 0 && episodes.Count(x => SeasonExtra(x, g.Key)) is > 0 and var clips ? $" · {clips} {(clips == 1 ? "extra" : "extras")}" : "") + (status is null ? "" : " · " + status),
                    Existing(g.Select(x => x.SeasonPoster).Concat(g.Select(x => x.SeriesPoster)).Concat(g.Select(x => x.Poster)), cancellation), g.First(), "season", g.Key, Status: status);
            }).ToList();
        var seasonItems = episodes.Where(x => (x.Season ?? -1) == season || SeasonExtra(x, season) || displaySpecials && season > 0 && x.Season == 0 && (x.AirsBeforeSeason == season || x.AirsAfterSeason == season)).ToArray();
        return PreferredFiles(seasonItems, preferVersion).OrderBy(x => x.Season == 0 && season > 0 ? x.AirsBeforeSeason == season ? (x.AirsBeforeEpisode ?? 1) - 0.5 : double.MaxValue : x.Episode ?? int.MaxValue)
            .ThenBy(x => x.IsBonus ? x.BonusOrder : -1).ThenBy(x => x.Path).Select(x =>
            {
                var versions = Versions(seasonItems, x);
                var version = versions.Length > 1 ? VersionLabel(x, versions) : null;
                return new LibraryCard(x.Episode is not null ? x.EpisodeDisplayTitle : x.IsBonus && !string.IsNullOrWhiteSpace(x.EpisodeTitle) ? x.EpisodeTitle : Path.GetFileNameWithoutExtension(x.Path),
                    x.Caption + (x.IsVirtual ? DateOnly.TryParse(x.AirDate, out var date) && date > DateOnly.FromDateTime(DateTime.UtcNow) ? " · Upcoming" : " · Missing" : x.Available ? "" : " · Unavailable") +
                    (version is null ? "" : $" · {version} · {versions.Length} versions"),
                    EpisodeArtwork(x, cancellation), x, "episode", season, version, versions.Length);
            }).ToList();
    }
    private static LibraryCard AlbumCard(MediaItem[] tracks, MediaItem first, CancellationToken cancellation)
    {
        var artists = tracks.Select(x => x.Artist).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var count = tracks.Count(x => !x.IsVirtual);
        var caption = string.Join(" · ", new[] { artists.Length == 1 ? artists[0] : artists.Length > 1 ? "Various artists" : null,
            first.Year?.ToString(), $"{count} {(count == 1 ? "track" : "tracks")}", tracks.Any(x => x.Available) ? null : "Unavailable" }.Where(x => x is not null));
        return new LibraryCard(first.Series!, caption, Existing(tracks.Select(x => x.SeriesPoster).Concat(tracks.Select(x => x.Poster)), cancellation), first, "series");
    }
    // The clips that came with a film, in name order.
    public static MediaItem[] Extras(IEnumerable<MediaItem> source, MediaItem film) => film.Series is not null ? [] :
        source.Where(x => x.ExtraOf == film.Id && !x.IsVirtual).OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    // A bonus clip from a season's own extras folder also lists at the end of that season.
    public static bool SeasonExtra(MediaItem item, int? season) => season > 0 && item.IsBonus && item.Season == 0 && item.AirsAfterSeason == season;
    // A season holding nothing but bonus clips is the show's extras, not a run of numbered specials.
    public static string SeasonName(int season, IEnumerable<MediaItem>? members = null) => season < 0 ? "Season unknown" :
        season == 0 ? members is not null && members.Any() && members.All(x => x.IsBonus) ? "Extras" : "Specials" : $"Season {season}";
    // A season made only of placeholders is missing as a whole, or upcoming when no episode has aired yet.
    public static string? SeasonStatus(IEnumerable<MediaItem> members)
    {
        var items = members as IReadOnlyCollection<MediaItem> ?? members.ToArray();
        if (items.Count == 0 || !items.All(x => x.IsVirtual)) return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return items.All(x => DateOnly.TryParse(x.AirDate, out var date) && date > today) ? "Upcoming" : "Missing";
    }
    // A show or film with no file at all is missing, or upcoming before anything has aired; a show with files is missing
    // the episodes whose placeholders have aired, named by season when they make up one season.
    public static string? TitleStatus(IReadOnlyCollection<MediaItem> members)
    {
        if (members.Count == 0 || !members.Any(x => x.IsVirtual)) return null;
        if (members.All(x => x.IsVirtual)) return SeasonStatus(members);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aired = members.Where(x => x.IsVirtual && DateOnly.TryParse(x.AirDate, out var date) && date <= today).ToArray();
        if (aired.Length == 0) return null;
        var seasons = aired.Select(x => x.Season).Distinct().ToArray();
        return seasons.Length == 1 && seasons[0] is { } season && members.Where(x => x.Season == season).All(x => x.IsVirtual)
            ? (season == 0 ? "Specials" : "S" + season) + " missing" : $"{aired.Length} missing";
    }
    private static string SeasonCaption(IEnumerable<MediaItem> members)
    {
        var episodes = LibraryIdentity.EpisodeCount(members); var bonus = members.Count(x => x.IsBonus);
        return string.Join(" · ", new[] { episodes > 0 || bonus == 0 ? $"{episodes} episodes" : null, bonus > 0 ? $"{bonus} bonus {(bonus == 1 ? "clip" : "clips")}" : null }.Where(x => x is not null));
    }
    // Every file holding the same episode of a show, such as a colour and a black-and-white release.
    public static MediaItem[] Versions(IEnumerable<MediaItem> source, MediaItem item) =>
        item.Series is null || item.Season is null || item.Episode is null || item.NumberingConflict is not null ? [item] :
        source.Where(x => !x.IsVirtual && x.NumberingConflict is null && LibraryIdentity.SameShow(x, item) && x.Season == item.Season && x.Episode == item.Episode && x.EpisodeEnd == item.EpisodeEnd)
            .OrderByDescending(x => x.Available).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    // The words that tell one release apart from the others, read from the folders and file name below the
    // path they share: "BW" against "Color", or "1080p" against "720p". The file name stands in when nothing differs.
    public static string VersionLabel(MediaItem item, IReadOnlyCollection<MediaItem> versions)
    {
        if (versions.Count < 2) return "";
        var paths = versions.ToDictionary(v => v.Id, v => v.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var shared = paths.Values.Min(parts => parts.Length) - 1;
        while (shared > 0 && paths.Values.Any(parts => !parts[shared - 1].Equals(paths[item.Id][shared - 1], StringComparison.OrdinalIgnoreCase))) shared--;
        string[] Tokens(string[] parts) => parts.Skip(shared).SkipLast(1).Append(Path.GetFileNameWithoutExtension(parts[^1]))
            .SelectMany(part => Regex.Split(part, @"[^\p{L}\p{N}]+")).Where(token => token.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var tokens = paths.ToDictionary(p => p.Key, p => Tokens(p.Value));
        var unique = tokens[item.Id].Where(token => tokens.All(other => other.Key == item.Id || !other.Value.Contains(token, StringComparer.OrdinalIgnoreCase))).Take(3).ToArray();
        return unique.Length > 0 ? string.Join(" ", unique) : Path.GetFileNameWithoutExtension(item.Path);
    }
}
