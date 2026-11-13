namespace BlackPearl.Core;

/// <summary>
/// The languages of a video's audio and subtitles, as far as they're known: tagged in the file, identified earlier while
/// watching or opening its page, or named by subtitle files beside it. HasSubtitles also counts subtitles of unknown
/// language. Read is false when the file's own tracks can't be listed (containers other than Matroska and MP4).
/// </summary>
public sealed record FileLanguages(string[] Audio, string[] Subtitles, bool HasSubtitles, bool Read = true);

public enum SearchLength { Any, UpToHalfHour, UpToHour, UpToTwoHours, OverTwoHours }

/// <summary>
/// What the Search tab narrows the library to besides its text. Null means any. Category uses the library's numbers:
/// 0 all videos, 1 movies, 2 shows, 3 anime and LibraryCatalog.Music. Decade is its first year, as 1990.
/// </summary>
public sealed record LibrarySearchFilters(int Category = 0, string? AudioLanguage = null, string? SubtitleLanguage = null,
    string? Genre = null, int? Decade = null, SearchLength Length = SearchLength.Any, double? MinimumRating = null)
{
    /// <summary>The subtitle choice for videos without any subtitles.</summary>
    public const string NoSubtitles = "none";
    /// <summary>Filters that look at each video file rather than at the title.</summary>
    public bool FileFilters => AudioLanguage is not null || SubtitleLanguage is not null || Length != SearchLength.Any;
}

/// <summary>
/// The Search tab: shows, films and albums whose names or episode titles match the text, narrowed by the filters. A show
/// matches a file filter when any of its episodes does, and its card then says how many do.
/// </summary>
public static class LibrarySearch
{
    public static List<LibraryCard> Search(IEnumerable<MediaItem> source, string query, LibrarySearchFilters filters,
        IReadOnlyDictionary<string, FileLanguages> languages, CancellationToken cancellation = default)
    {
        query = query.Trim();
        var items = source.Where(item => filters.Category switch
        {
            1 => item.Kind == "Movie", 2 => item.Kind == "Show", 3 => item.Kind == "Anime", LibraryCatalog.Music => item.IsMusic, _ => !item.IsMusic
        }).ToArray();
        var titles = items.Where(item => !item.IsExtra).GroupBy(LibraryIdentity.TitleKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var results = new List<LibraryCard>();
        foreach (var card in LibraryCatalog.Browse(items, cancellation: cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            var members = titles.GetValueOrDefault(LibraryIdentity.TitleKey(card.Media)) ?? [card.Media];
            var named = query.Length == 0 || Contains(query, card.DisplayTitle, card.Caption, card.Media.Title, card.Media.Path, card.Media.OriginalTitle, card.Media.ProviderTitle);
            var episodes = query.Length == 0 || named || card.Level != "series" ? 0
                : members.Count(item => !item.IsVirtual && Contains(query, item.IsMusic ? item.Title : item.EpisodeTitle));
            if (!named && episodes == 0 || !TitleMatches(card, members, filters)) continue;
            var caption = card.Caption;
            if (episodes > 0) caption += $" · {Count(episodes, card.Media.IsMusic ? "track" : "episode")} {(episodes == 1 ? "matches" : "match")}";
            if (filters.FileFilters)
            {
                var files = members.Where(item => item.Available && !item.IsVirtual && !item.IsBonus && item.ExtraOf is null).ToArray();
                var matching = files.Count(item => FileMatches(item, filters, languages));
                if (matching == 0) continue;
                if (card.Level == "series" && matching < files.Length) caption += $" · {matching} of {Count(files.Length, "file")} match";
            }
            results.Add(caption == card.Caption ? card : card with { Caption = caption });
        }
        return results;
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static bool Contains(string query, params string?[] values) =>
        values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private static bool TitleMatches(LibraryCard card, MediaItem[] members, LibrarySearchFilters filters)
    {
        if (filters.Genre is { } genre && !members.Any(item => item.Genres?.Contains(genre, StringComparer.OrdinalIgnoreCase) == true)) return false;
        if (filters.Decade is { } start && (Year(card, members) is not { } year || year < start || year >= start + 10)) return false;
        if (filters.MinimumRating is { } minimum && !(members.Select(item => item.Rating).FirstOrDefault(rating => rating > 0) >= minimum)) return false;
        return true;
    }

    /// <summary>The year a title came out: the film's, or the earliest a show's files give.</summary>
    public static int? Year(LibraryCard card, IEnumerable<MediaItem> members) => card.Media.Year ?? members.Min(item => item.Year);

    public static bool FileMatches(MediaItem item, LibrarySearchFilters filters, IReadOnlyDictionary<string, FileLanguages> languages)
    {
        if (filters.Length != SearchLength.Any)
        {
            var minutes = item.FileRuntimeMinutes is > 0 ? item.FileRuntimeMinutes : item.RuntimeMinutes;
            if (!(filters.Length switch
                {
                    SearchLength.UpToHalfHour => minutes is > 0 and <= 30,
                    SearchLength.UpToHour => minutes is > 30 and <= 60,
                    SearchLength.UpToTwoHours => minutes is > 60 and <= 120,
                    _ => minutes > 120
                })) return false;
        }
        if (filters.AudioLanguage is null && filters.SubtitleLanguage is null) return true;
        if (!languages.TryGetValue(item.Path, out var known)) return false;
        if (filters.AudioLanguage is { } audio && !known.Audio.Contains(audio)) return false;
        return filters.SubtitleLanguage switch
        {
            null => true,
            LibrarySearchFilters.NoSubtitles => !known.HasSubtitles,
            var subtitles => known.Subtitles.Contains(subtitles)
        };
    }
}

/// <summary>
/// Reads the languages of every video for Search. A file's track list comes from its Matroska or MP4 header once per
/// version of the file and is cached in the library; untagged tracks take the languages identified for them since, and
/// subtitle files beside a video count by the language their names give. Other containers list subtitle files only.
/// </summary>
public static class FileLanguageIndex
{
    /// <summary>A container's audio and subtitle tracks as cached: "a1=en;a2=;s3=ru", by the player's track number,
    /// with an empty language for untagged tracks. Null when the tracks couldn't be read.</summary>
    public static string? Encode(IReadOnlyList<ContainerTrack> tracks) => tracks.Count == 0 ? null : string.Join(";",
        tracks.Select((track, id) => (track, id)).Where(entry => entry.track.Type is ContainerTrackType.Audio or ContainerTrackType.Subtitle)
            .Select(entry => (entry.track.Type == ContainerTrackType.Audio ? "a" : "s") + entry.id + "=" + (TrackLanguages.Normalize(entry.track.Language) ?? "")));

    public static FileLanguages Resolve(string? tracks, string fileKey, IReadOnlyDictionary<string, string> identified, IEnumerable<string> sidecars)
    {
        var audio = new List<string>(); var subtitles = new List<string>(); var hasSubtitles = false;
        foreach (var entry in (tracks ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals < 2 || entry[0] is not ('a' or 's') || !int.TryParse(entry.AsSpan(1, equals - 1), out var id)) continue;
            var isAudio = entry[0] == 'a';
            var language = entry[(equals + 1)..];
            if (language.Length == 0) language = identified.GetValueOrDefault(LibraryStore.TrackLanguageKey(fileKey, isAudio, id)) ?? "";
            if (!isAudio) hasSubtitles = true;
            if (language.Length > 0) (isAudio ? audio : subtitles).Add(language);
        }
        foreach (var file in sidecars)
        {
            hasSubtitles = true;
            if (SubtitleFiles.Language(file) is { } language) subtitles.Add(language);
        }
        return new(audio.Distinct().ToArray(), subtitles.Distinct().ToArray(), hasSubtitles, tracks is not null);
    }

    /// <param name="progress">Called now and then from the reading thread with the languages read so far and how many files that is.</param>
    public static Dictionary<string, FileLanguages> Read(LibraryStore store, IReadOnlyCollection<string> paths,
        Action<Dictionary<string, FileLanguages>, int>? progress = null, CancellationToken cancellation = default)
    {
        var cached = store.CachedTrackLists();
        var identified = store.CachedTrackLanguages();
        var folders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> ListFolder(string folder)
        {
            if (folders.TryGetValue(folder, out var files)) return files;
            try { files = Directory.Exists(folder) ? Directory.GetFiles(folder) : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { files = []; }
            if (folders.Count > 4096) folders.Clear();
            return folders[folder] = files;
        }
        var result = new Dictionary<string, FileLanguages>(StringComparer.OrdinalIgnoreCase);
        var fresh = new List<(string Path, string FileKey, string Tracks)>();
        var done = 0; var reported = Environment.TickCount64;
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            done++;
            if (LibraryStore.TrackLanguageFile(path) is not { } fileKey) continue;
            try
            {
                if (!cached.TryGetValue(fileKey, out var tracks) && SubtitleText.ListsTracks(path))
                {
                    // Unreadable headers aren't cached, so a file that was busy or half copied is read again next time.
                    tracks = Encode(SubtitleText.Tracks(path, cancellation));
                    if (tracks is not null) fresh.Add((path, fileKey, tracks));
                }
                result[path] = Resolve(tracks, fileKey, identified, SubtitleFiles.VlcSidecars(path, ListFolder));
            }
            // One malformed file is left out rather than ending the pass for the rest of the library.
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            if (fresh.Count >= 200) { store.CacheTrackLists(fresh); fresh.Clear(); }
            if (progress is not null && Environment.TickCount64 - reported > 1500)
            { reported = Environment.TickCount64; progress(new(result, StringComparer.OrdinalIgnoreCase), done); }
        }
        if (fresh.Count > 0) store.CacheTrackLists(fresh);
        return result;
    }
}
