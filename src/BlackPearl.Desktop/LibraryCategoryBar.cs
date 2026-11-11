using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// The library's categories are words strung on a thread at the top of the Library page. The app's pearl sits on the thread
// under the current one and rolls to the next; it follows the words when one appears, disappears or changes width, as
// Missing does with its count.
public partial class MainWindow
{
    private static readonly Duration CategoryPearlDuration = new(TimeSpan.FromMilliseconds(260));
    private bool categoryPearlPlaced;

    private Button[] LibraryCategoryButtons => [AllLibraryNav, AnimeLibraryNav, ShowsLibraryNav, MoviesLibraryNav, UnavailableLibraryNav, MissingLibraryNav, UnmatchedLibraryNav];

    private void LibraryCategoriesSizeChanged(object sender, SizeChangedEventArgs e) => PlaceCategoryThumb();

    /// <summary>Puts the pearl under the middle of the current category's word, rolling there once it has been shown.</summary>
    private void PlaceCategoryThumb()
    {
        if (LibraryCategoryPearl is null) return;
        var target = LibraryCategoryButtons.FirstOrDefault(button => button.IsVisible && AutomationProperties.GetItemStatus(button) == "Selected");
        if (target is null || !LibraryCategories.IsVisible || target.ActualWidth == 0)
        {
            LibraryCategoryPearl.Visibility = Visibility.Hidden; categoryPearlPlaced = false;
            return;
        }
        // The space after the word is part of its button.
        var word = target.ActualWidth - target.Padding.Left - target.Padding.Right;
        var left = Math.Round(target.TranslatePoint(new Point(target.Padding.Left + word / 2, 0), LibraryCategories).X - LibraryCategoryPearl.Width / 2);
        var shift = (TranslateTransform)LibraryCategoryPearl.RenderTransform;
        if (!categoryPearlPlaced || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = left;
            LibraryCategoryPearl.Visibility = Visibility.Visible; categoryPearlPlaced = true;
            return;
        }
        if (shift.X == left) return;
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(left, CategoryPearlDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private void LibraryCategoryBarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Measured positions are only meaningful once the categories are laid out again.
        if (!LibraryCategoryBar.IsVisible) { categoryPearlPlaced = false; return; }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlaceCategoryThumb);
    }
}
