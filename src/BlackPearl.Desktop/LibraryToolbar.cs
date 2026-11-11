using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BlackPearl.Desktop;

// The Library page's top row: its categories on the left, and the count, Rescan and Add title on the right. A window too
// narrow for both first shortens Add title to its plus, then puts the actions on a row of their own. The location inside a
// category (a show, a season) goes under the row; Search and Music have no categories, so their note or location takes
// the categories' place.
public partial class MainWindow
{
    private const double LibraryToolbarGap = 16, LibraryToolbarRowGap = 10;
    private (double Full, double Compact)? addTitleWidths;
    private bool libraryToolbarWatched, libraryToolbarQueued;

    private void LibraryToolbarSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeLibraryToolbar();
    private void LibraryCategoryBarSizeChanged(object sender, SizeChangedEventArgs e) => ArrangeLibraryToolbar();

    /// <summary>Lays the row out again once what changed has been measured.</summary>
    private void ArrangeLibraryToolbar()
    {
        if (libraryToolbarQueued) return;
        libraryToolbarQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { libraryToolbarQueued = false; LayOutLibraryToolbar(); });
    }

    private void LayOutLibraryToolbar()
    {
        if (!libraryToolbarWatched)
        {
            // A new location can add or remove the row under the categories.
            ((INotifyCollectionChanged)LibraryBreadcrumbs.Items).CollectionChanged += (_, _) => ArrangeLibraryToolbar();
            libraryToolbarWatched = true;
        }
        var categories = LibraryCategoryBar.Visibility == Visibility.Visible;
        Grid.SetRow(LibraryBreadcrumbs, categories ? 2 : 0);
        Grid.SetColumnSpan(LibraryBreadcrumbs, categories ? 4 : 1);
        LibraryBreadcrumbs.Margin = new Thickness(-8, categories && LibraryBreadcrumbs.HasItems ? LibraryToolbarRowGap : 0, 0, 0);

        var room = LibraryToolbar.ActualWidth;
        if (room <= 0 || !IsLoaded) return;
        var adding = AddTitleButton.Visibility == Visibility.Visible;
        var widths = adding ? addTitleWidths ??= MeasureAddTitle() : (0, 0);
        // The categories' own desired size is cut to the room they were given, so their width comes from the words. The thread's
        // fading tail runs below the actions, so it needs no room of its own.
        var left = categories ? LibraryCategories.Children.OfType<FrameworkElement>().Where(segment => segment.Visibility == Visibility.Visible).Sum(segment => segment.DesiredSize.Width) + LibraryToolbarGap : 0;
        var actions = (ResultCount.Visibility == Visibility.Visible ? ResultCount.DesiredSize.Width : 0) + (RescanButton.Visibility == Visibility.Visible ? RescanButton.DesiredSize.Width : 0);
        var wrap = left + actions + widths.Compact > room;
        var compact = wrap ? actions + widths.Full > room : left + actions + widths.Full > room;
        SetAddTitleCompact(compact);
        // The actions' columns stay as wide as the actions on every row, so categories alone on theirs span those columns too.
        Grid.SetColumnSpan(LibraryCategoryBar, wrap ? 4 : 1);
        var row = wrap ? 1 : 0; var top = wrap ? LibraryToolbarRowGap : 0;
        // Beside the categories, the actions centre on the words rather than on the words and their thread.
        var bottom = categories && !wrap ? AllLibraryNav.Margin.Bottom : 0;
        Grid.SetRow(ResultCount, row); Grid.SetRow(RescanButton, row); Grid.SetRow(AddTitleButton, row);
        // Centring sets text of different sizes on different baselines; these nudges put the count's smaller serif and the
        // buttons' sans on the category words' baseline.
        // The count is centred, so trimming its bottom margin lowers it; the buttons fill their slot exactly, so
        // they are lifted by moving the whole slot up.
        var lower = bottom > 0 ? 1 : 0; var lift = bottom > 0 ? 1 : 0;
        ResultCount.Margin = new Thickness(12, top, 8, bottom - lower);
        RescanButton.Margin = new Thickness(0, top - lift, 0, bottom + lift); AddTitleButton.Margin = new Thickness(4, top - lift, 0, bottom + lift);
    }

    /// <summary>How wide Add title is with its label and as only its plus, margin included.</summary>
    private (double Full, double Compact) MeasureAddTitle()
    {
        var was = AddTitleButton.Content is not string;
        double Width(bool compact)
        {
            SetAddTitleCompact(compact);
            // Measuring the button alone returns its template's earlier answer, so everything inside it is marked for measuring
            // first. A window-wide layout pass would do the same but lays out the whole library too, which stalls startup.
            Remeasure(AddTitleButton);
            AddTitleButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AddTitleButton.DesiredSize.Width;
        }
        var widths = (Width(false), Width(true));
        SetAddTitleCompact(was);
        return widths;
    }

    private static void Remeasure(DependencyObject node)
    {
        if (node is UIElement element) element.InvalidateMeasure();
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) Remeasure(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
    }

    private void SetAddTitleCompact(bool compact)
    {
        if (compact == AddTitleButton.Content is not string) return;
        if (compact) { AddTitleButton.ContentTemplate = null; AddTitleButton.Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 13 }; AddTitleButton.Padding = new Thickness(13, 0, 13, 0); }
        else { AddTitleButton.ContentTemplate = (DataTemplate)FindResource("AddContent"); AddTitleButton.Content = "Add title"; AddTitleButton.Padding = new Thickness(14, 0, 14, 0); }
    }
}
