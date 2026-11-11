using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// The library's categories are words strung on a thread at the top of the Library page. The stretch of thread under the
// current one is lit in the pearl's colours and glides to the next, taking that word's width; it follows the words when one
// appears, disappears or changes width, as Missing does with its count. The pearl itself marks the page on the sidebar.
public partial class MainWindow
{
    private static readonly Duration CategoryGlowDuration = new(TimeSpan.FromMilliseconds(260));
    private const double CategoryGlowReach = 7;
    private bool categoryGlowPlaced;

    private Button[] LibraryCategoryButtons => [AllLibraryNav, AnimeLibraryNav, ShowsLibraryNav, MoviesLibraryNav, UnavailableLibraryNav, MissingLibraryNav, UnmatchedLibraryNav];

    private void LibraryCategoriesSizeChanged(object sender, SizeChangedEventArgs e) => PlaceCategoryThumb();

    /// <summary>Lights the thread under the current category's word, gliding there once it has been shown.</summary>
    private void PlaceCategoryThumb()
    {
        if (LibraryCategoryGlow is null) return;
        var target = LibraryCategoryButtons.FirstOrDefault(button => button.IsVisible && AutomationProperties.GetItemStatus(button) == "Selected");
        if (target is null || !LibraryCategories.IsVisible || target.ActualWidth == 0)
        {
            LibraryCategoryGlow.Visibility = Visibility.Hidden; categoryGlowPlaced = false;
            return;
        }
        // The space after the word is part of its button. The light reaches a little past each end of the word, so its fades fall
        // outside the word and even a short one like All is lit along its whole length.
        var width = target.ActualWidth - target.Padding.Left - target.Padding.Right + 2 * CategoryGlowReach;
        var left = target.TranslatePoint(new Point(target.Padding.Left, 0), LibraryCategories).X - CategoryGlowReach;
        var shift = (TranslateTransform)LibraryCategoryGlow.RenderTransform;
        if (!categoryGlowPlaced || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); LibraryCategoryGlow.BeginAnimation(WidthProperty, null);
            shift.X = left; LibraryCategoryGlow.Width = width;
            LibraryCategoryGlow.Visibility = Visibility.Visible; categoryGlowPlaced = true;
            return;
        }
        if (shift.X == left && LibraryCategoryGlow.Width == width) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(left, CategoryGlowDuration) { EasingFunction = ease });
        LibraryCategoryGlow.BeginAnimation(WidthProperty, new DoubleAnimation(width, CategoryGlowDuration) { EasingFunction = ease });
    }

    private void LibraryCategoryBarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Measured positions are only meaningful once the categories are laid out again.
        if (!LibraryCategoryBar.IsVisible) { categoryGlowPlaced = false; return; }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlaceCategoryThumb);
    }
}
