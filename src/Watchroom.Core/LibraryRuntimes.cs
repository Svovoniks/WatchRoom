namespace Watchroom.Core;

// A video's length comes from its own file header. Provider runtimes can belong to the wrong title or edition,
// and a matched show can list its episodes at a tenth of their real length; the file is what plays.
public static class LibraryRuntimes
{
    public static bool NeedsRuntime(MediaItem item) => !item.IsMusic && !item.IsVirtual && item.Available && item.FileRuntimeMinutes is null;
    // Only a length the user set themselves outranks the file's; a sidecar .nfo repeats provider data.
    public static bool Overrides(MediaItem item) => !item.MetadataLocked && !(item.LockedFields ?? []).Contains("RuntimeMinutes", StringComparer.OrdinalIgnoreCase);
    public static MediaItem Apply(MediaItem item)
    {
        if (item.FileRuntimeMinutes is not > 0 || !Overrides(item)) return item;
        // The length is no longer provider data, so TMDB expiry must not clear it.
        var tracked = item.TmdbFields?.ContainsKey("runtimeMinutes") == true;
        if (item.RuntimeMinutes == item.FileRuntimeMinutes && !tracked) return item;
        var fields = tracked ? item.TmdbFields!.Where(x => x.Key != "runtimeMinutes").ToDictionary() : item.TmdbFields;
        return item with { RuntimeMinutes = item.FileRuntimeMinutes, TmdbFields = fields?.Count > 0 ? fields : null };
    }
    public static int Backfill(LibraryStore store, IEnumerable<MediaItem> items, CancellationToken ct = default)
    {
        var filled = 0;
        foreach (var item in items.Where(NeedsRuntime))
        {
            ct.ThrowIfCancellationRequested();
            // A file that can't be read now (drive offline, file locked) is tried again on the next pass.
            if (!MediaDuration.Read(item.Path, out var length, ct)) continue;
            // A clip under half a minute still has a length.
            var minutes = length is > 0 ? Math.Max(1, (int)Math.Round(length.Value)) : 0;
            if (store.FillFileRuntime(item.Id, minutes) is not null && minutes > 0) filled++;
        }
        return filled;
    }
}
