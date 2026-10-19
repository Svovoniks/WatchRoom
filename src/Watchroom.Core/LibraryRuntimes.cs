namespace Watchroom.Core;

// Movies often have no runtime until (or unless) online metadata supplies one, which leaves
// Home's length filters with nothing to match. The file header already records the length.
public static class LibraryRuntimes
{
    public static bool NeedsRuntime(MediaItem item) => item.Series is null && item.ShowId is null && !item.IsExtra && !item.IsVirtual &&
        item.Available && item.RuntimeMinutes is not > 0 && !LibraryIdentity.Locked(item, "RuntimeMinutes");
    public static int Backfill(LibraryStore store, IEnumerable<MediaItem> items, CancellationToken ct = default)
    {
        var filled = 0;
        foreach (var item in items.Where(NeedsRuntime))
        {
            ct.ThrowIfCancellationRequested();
            if (MediaDuration.Minutes(item.Path, ct) is not { } minutes || minutes < 1) continue;
            if (store.FillMovieRuntime(item.Id, (int)Math.Round(minutes)) is not null) filled++;
        }
        return filled;
    }
}
