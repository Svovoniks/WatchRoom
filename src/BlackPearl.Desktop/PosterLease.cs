using System.Windows;
using System.Windows.Controls;

namespace BlackPearl.Desktop;

// Tells a card when an image that draws its artwork comes on screen and when it leaves: scrolled out of the grid and
// recycled for another card, or on a page that is no longer the one showing. The card loads its artwork for the first
// and lets go of it some time after the last (see LibraryCardView.Shown).
internal static class PosterLease
{
    private static readonly DependencyProperty CardProperty =
        DependencyProperty.RegisterAttached("Card", typeof(LibraryCardView), typeof(PosterLease));
    private static readonly DependencyProperty WatchedProperty =
        DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(PosterLease));

    // Called from the image's Loaded event, which fires again each time its container returns to the grid.
    public static void Watch(Image image)
    {
        if (!(bool)image.GetValue(WatchedProperty))
        {
            image.SetValue(WatchedProperty, true);
            image.IsVisibleChanged += (sender, _) => Sync((Image)sender);
            image.DataContextChanged += (sender, _) => Sync((Image)sender);
            image.Loaded += (sender, _) => Sync((Image)sender);
            image.Unloaded += (sender, _) => Sync((Image)sender);
        }
        Sync(image);
    }
    private static void Sync(Image image)
    {
        var now = image.IsLoaded && image.IsVisible ? image.DataContext as LibraryCardView : null;
        var before = (LibraryCardView?)image.GetValue(CardProperty);
        if (ReferenceEquals(before, now)) return;
        image.SetValue(CardProperty, now);
        // The new card first: an image handed straight to another card that shares the artwork keeps it alive.
        now?.Shown(); before?.Hidden();
    }
}
