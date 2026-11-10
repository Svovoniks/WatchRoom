using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// The library's categories are a segmented bar at the top of the Library page. A thumb behind the segments slides to
// the current one; it follows the segments when one appears, disappears or changes width, as Missing does with its count.
public partial class MainWindow
{
    private static readonly Duration CategoryThumbDuration = new(TimeSpan.FromMilliseconds(220));
    private bool categoryThumbPlaced;

    private Button[] LibraryCategoryButtons => [AllLibraryNav, AnimeLibraryNav, ShowsLibraryNav, MoviesLibraryNav, UnavailableLibraryNav, MissingLibraryNav, UnmatchedLibraryNav];

    private void LibraryCategoriesSizeChanged(object sender, SizeChangedEventArgs e) => PlaceCategoryThumb();

    /// <summary>Moves the thumb under the selected segment, sliding there once it has been shown.</summary>
    private void PlaceCategoryThumb()
    {
        if (LibraryCategoryThumb is null) return;
        var target = LibraryCategoryButtons.FirstOrDefault(button => button.IsVisible && AutomationProperties.GetItemStatus(button) == "Selected");
        if (target is null || !LibraryCategories.IsVisible || target.ActualWidth == 0)
        {
            LibraryCategoryThumb.Visibility = Visibility.Hidden; categoryThumbPlaced = false;
            return;
        }
        var left = target.TranslatePoint(new Point(0, 0), LibraryCategories).X;
        var shift = (TranslateTransform)LibraryCategoryThumb.RenderTransform;
        if (!categoryThumbPlaced || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); LibraryCategoryThumb.BeginAnimation(WidthProperty, null);
            shift.X = left; LibraryCategoryThumb.Width = target.ActualWidth;
            LibraryCategoryThumb.Visibility = Visibility.Visible; categoryThumbPlaced = true;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(left, CategoryThumbDuration) { EasingFunction = ease });
        LibraryCategoryThumb.BeginAnimation(WidthProperty, new DoubleAnimation(target.ActualWidth, CategoryThumbDuration) { EasingFunction = ease });
    }

    private void LibraryCategoryBarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Measured positions are only meaningful once the bar is laid out again.
        if (!LibraryCategoryBar.IsVisible) { categoryThumbPlaced = false; return; }
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, PlaceCategoryThumb);
    }
}
