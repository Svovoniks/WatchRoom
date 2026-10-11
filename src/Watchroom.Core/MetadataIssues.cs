using System.Text.Json;

namespace Watchroom.Core;

// Outstanding problems survive short startup runs and retry cooldowns. They are
// keyed to local identity so edits, removals and locked records discard stale issues.
internal static class MetadataIssues
{
    internal sealed record Issue(string Key, string Stage, string Title, string Code, string[] MediaIds, string Fingerprint,
        long At, string? Detail = null, object? Error = null, bool PosterMissing = false, bool OverviewMissing = false, long? RetryAt = null,
        string? ArtworkPath = null, bool ArtworkWasUsable = false);
    private static readonly AsyncLocal<State?> current = new();
    internal sealed class Scope(State state, State? previous, bool owned) : IDisposable
    {
        internal State State => state;
        public void Dispose() { try { if (owned) state.Save(); } finally { current.Value = previous; } }
    }
    internal static Scope Begin(LibraryStore library)
    {
        var previous = current.Value;
        var state = previous is not null && previous.Library.MetadataCacheDirectory == library.MetadataCacheDirectory ? previous : new State(library);
        current.Value = state; return new(state, previous, state != previous);
    }
    internal sealed class State
    {
        internal LibraryStore Library { get; }
        private readonly Dictionary<string, Issue> issues;
        internal State(LibraryStore library)
        {
            Library = library;
            try { issues = (library.Setting("metadata-outstanding-v2") is { } json ? Wire.Read<Issue[]>(json) : [])
                .Where(i => i.At > Wire.Now - (long)TmdbRetention.MaximumAge.TotalMilliseconds).ToDictionary(i => i.Key); }
            catch (JsonException) { issues = []; }
        }
        private static string Key(string stage, MediaItem item) => stage + ":" + (stage.EndsWith("artwork") ? item.Id : LibraryIdentity.ShowKey(item));
        private static string Fingerprint(MediaItem item) => LibraryIdentity.Hash(Wire.Serialize(new { item.Id, item.Title, item.Series, item.Year, item.MetadataProvider,
            item.MetadataId, item.MetadataType, item.Matched, item.MetadataLocked, item.ShowId, item.LockedFields, ProviderIds = (item.ProviderIds ?? []).OrderBy(p => p.Key) }));
        private static string? ArtworkPath(string stage, MediaItem item) => stage == "episode-artwork" ? item.EpisodePoster : stage == "season-artwork" ? item.SeasonPoster : item.SeriesPoster ?? item.Poster;
        internal void Clear(string stage, MediaItem item) => issues.Remove(Key(stage, item));
        internal void Set(string stage, MediaItem item, string code, string? detail = null, Exception? error = null, bool posterMissing = false, bool overviewMissing = false)
        {
            var currentItem = Library.ByIds([item.Id]).FirstOrDefault() ?? item;
            var key = Key(stage, currentItem);
            issues[key] = new(key, stage, currentItem.DisplayTitle, code, [currentItem.Id], Fingerprint(currentItem), Wire.Now,
                detail, error is null ? null : MetadataDiagnostics.Error(error), posterMissing, overviewMissing,
                ArtworkPath: ArtworkPath(stage, currentItem), ArtworkWasUsable: ArtworkCache.IsUsable(ArtworkPath(stage, currentItem)));
        }
        internal Issue[] Snapshot()
        {
            var items = Library.ByIds(issues.Values.SelectMany(i => i.MediaIds)).ToDictionary(i => i.Id);
            foreach (var issue in issues.Values.ToArray())
                if (!items.TryGetValue(issue.MediaIds[0], out var item) || !item.Available || item.IsExtra || item.IsBonus || item.MetadataLocked || issue.Fingerprint != Fingerprint(item) ||
                    issue.Code == "provider-missing-fields" && (!issue.OverviewMissing || !string.IsNullOrWhiteSpace(item.Overview)) && (!issue.PosterMissing || ArtworkCache.IsUsable(item.SeriesPoster) || ArtworkCache.IsUsable(item.Poster)) ||
                    issue.Stage.EndsWith("artwork") && ArtworkCache.IsUsable(ArtworkPath(issue.Stage, item)) && (!issue.ArtworkWasUsable || ArtworkPath(issue.Stage, item) != issue.ArtworkPath))
                    issues.Remove(issue.Key);
            return issues.Values.Select(i => i with { RetryAt = MetadataFetchPolicy.RetryAt(Library, i.Stage is "season-artwork" or "episode-artwork" or "episodes" ? "episodes" : "title", i.MediaIds[0]) })
                .OrderBy(i => i.Title).ThenBy(i => i.Stage).ToArray();
        }
        internal void Save() => Library.Setting("metadata-outstanding-v2", Wire.Serialize(Snapshot()));
    }
}
