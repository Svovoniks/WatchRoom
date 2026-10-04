using System.Windows;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private IEnumerable<MediaItem> UnavailableCardItems(LibraryCard card) => items.Where(item => !item.Available &&
        (card.Level is "series" or "season"
            ? LibraryIdentity.SameShow(item, card.Media) && (card.Level == "series" || (item.Season ?? -1) == card.Season)
            : item.Id == card.Media.Id));

    private void RemoveUnavailableVideo(object sender, RoutedEventArgs e)
    {
        if (selected is { Available: false } item) RemoveUnavailableItems([item]);
    }

    private void RemoveUnavailableItems(IEnumerable<MediaItem> source)
    {
        if (GuestLibrary || scanning) return;
        var ids = source.Where(item => !item.Available).Select(item => item.Id).ToArray();
        var removed = library.RemoveUnavailable(ids);
        foreach (var id in ids) watchedState.Remove(id);
        RefreshLibrary();
        SetStatus(removed == 1 ? "Unavailable video removed from library." : $"{removed} unavailable videos removed from library.");
    }
}
