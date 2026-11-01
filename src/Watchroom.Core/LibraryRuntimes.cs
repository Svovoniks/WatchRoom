namespace Watchroom.Core;

// A video's length comes from its own file header. Provider runtimes can belong to the wrong title or edition,
// and a matched show can list its episodes at a tenth of their real length; the file is what plays.
public static class LibraryRuntimes
{
    public static bool NeedsRuntime(MediaItem item) => !item.IsMusic && !item.IsVirtual && item.Available && item.FileRuntimeMinutes is null;
    // Only a length the user set themselves outranks the file's; a sidecar .nfo repeats provider data.
    public static MediaItem Apply(MediaItem item) => item.FileRuntimeMinutes is > 0 and var minutes && item.RuntimeMinutes != minutes &&
        !item.MetadataLocked && !(item.LockedFields ?? []).Contains("RuntimeMinutes", StringComparer.OrdinalIgnoreCase) ? item with { RuntimeMinutes = minutes } : item;
    public static int Backfill(LibraryStore store, IEnumerable<MediaItem> items, CancellationToken ct = default)
    {
        var filled = 0;
        foreach (var item in items.Where(NeedsRuntime))
        {
            ct.ThrowIfCancellationRequested();
            var minutes = MediaDuration.Minutes(item.Path, ct) is { } length ? (int)Math.Round(length) : 0;
            if (store.FillFileRuntime(item.Id, minutes) is not null && minutes > 0) filled++;
        }
        return filled;
    }
}
