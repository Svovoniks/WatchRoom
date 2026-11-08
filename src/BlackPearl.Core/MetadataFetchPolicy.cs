namespace BlackPearl.Core;

// Retry state is separate from successful metadata timestamps: an empty provider
// result must not pretend that a title was matched, or force another startup lookup.
internal static class MetadataFetchPolicy
{
    // Raised when a matcher change can choose differently: titles it left unmatched are looked up again, and so, once, are automatic matches.
    internal const int MatcherVersion = 6;
    private const string RecheckKey = "metadata-matcher-rechecked";
    private sealed record Attempt(string Fingerprint, long RetryAt);
    private static string Key(string stage, MediaItem item) => "metadata-retry:" + stage + ":" + item.Id;
    private static string Fingerprint(MediaItem item, MetadataOptions options, string? token) => LibraryIdentity.Hash(Wire.Serialize(new
    {
        MatcherVersion,
        item.Path, item.Title, item.Series, item.Kind, item.Year, item.ShowId, item.Season, item.Episode, item.EpisodeEnd,
        item.AbsoluteEpisode, item.NumberingOrder, item.SourcePart, item.SourcePartEpisode, item.SourcePartEpisodeEnd,
        item.AirDate, item.NumberingSource,
        item.MetadataProvider, item.MetadataId, item.MetadataKind, item.MetadataType, item.Matched, item.MetadataLocked,
        LockedFields = (item.LockedFields ?? []).Order(), ProviderIds = (item.ProviderIds ?? []).OrderBy(x => x.Key), item.Overview, item.EpisodeTitle,
        item.Poster, item.SeriesPoster, item.SeasonPoster, item.EpisodePoster,
        PosterUsable = ArtworkCache.IsUsable(item.Poster), SeriesPosterUsable = ArtworkCache.IsUsable(item.SeriesPoster),
        SeasonPosterUsable = ArtworkCache.IsUsable(item.SeasonPoster), EpisodePosterUsable = ArtworkCache.IsUsable(item.EpisodePoster),
        options.ProviderOrder, options.Language, options.Country, options.RefreshDays,
        Credential = LibraryIdentity.Hash(TmdbCredentials.Resolve(token) ?? "")
    }));
    // A match an earlier matcher made by itself is looked up once more with this one; one someone chose, locked, or read from an NFO stays.
    public static bool RecheckPending(LibraryStore library) => library.Setting(RecheckKey) != MatcherVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static void RecordRecheck(LibraryStore library) => library.Setting(RecheckKey, MatcherVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static bool AutomaticMatch(MediaItem item) => !item.Matched && !item.MetadataLocked && item.MetadataProvider == "tmdb" && item.MetadataId is > 0 &&
        item.TmdbFields?.TryGetValue("providerIds.tmdb", out var found) == true && found.Original == "null" && LibraryIdentity.ProviderId(item, "tmdb") == item.MetadataId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
        !new[] { "ProviderIds", "MetadataType", "Title", "Year" }.Any(field => LibraryIdentity.Locked(item, field));
    public static bool Deferred(LibraryStore library, string stage, MediaItem[] entries, MetadataOptions options, string? token)
    {
        return entries.All(item =>
        {
            try
            {
                var value = library.Setting(Key(stage, item));
                var attempt = value is null ? null : Wire.Read<Attempt>(value);
                return attempt is not null && attempt.RetryAt > Wire.Now && attempt.Fingerprint == Fingerprint(item, options, token);
            }
            catch (System.Text.Json.JsonException) { return false; }
        });
    }
    public static void Record(LibraryStore library, string stage, IEnumerable<MediaItem> entries, MetadataOptions options, string? token, bool failed, bool unmatched)
    {
        var delay = failed ? TimeSpan.FromHours(1) : unmatched ? TimeSpan.FromDays(1) : TimeSpan.FromDays(options.RefreshDays > 0 ? options.RefreshDays : 7);
        foreach (var item in entries)
            library.Setting(Key(stage, item), Wire.Serialize(new Attempt(Fingerprint(item, options, token), Wire.Now + (long)delay.TotalMilliseconds)));
    }
    internal static long? RetryAt(LibraryStore library, string stage, string id)
    {
        try { return library.Setting("metadata-retry:" + stage + ":" + id) is { } json ? Wire.Read<Attempt>(json).RetryAt : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
