using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private IEnumerable<MediaItem> UnavailableCardItems(LibraryCard card) => items.Where(item => !item.Available && !item.IsVirtual &&
        (card.Level is "series" or "season"
            ? LibraryIdentity.SameShow(item, card.Media) && (card.Level == "series" || (item.Season ?? -1) == card.Season)
            : item.Id == card.Media.Id));

    private async void RemoveUnavailableVideo(object sender, RoutedEventArgs e)
    {
        if (selected is { Available: false } item) await Guard(() => RemoveUnavailableItems([item]));
    }

    private async Task RemoveUnavailableItems(IEnumerable<MediaItem> source)
    {
        if (GuestLibrary || scanning) return;
        var ids = source.Where(item => !item.Available && !item.IsVirtual).Select(item => item.Id).ToArray();
        var removed = library.RemoveUnavailable(ids);
        foreach (var id in ids) watchedState.Remove(id);
        await RefreshLibrary();
        SetStatus(removed == 1 ? "Unavailable video removed from library." : $"{removed} unavailable videos removed from library.");
    }
}
