using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
            // A new scaling rounds every slot differently, so the baselines are lined up again.
            DpiChanged += (_, _) => ArrangeLibraryToolbar();
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
        libraryToolbarBeside = categories && !wrap;
        PlaceLibraryToolbarActions(top, bottom);
        if (libraryToolbarBeside) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AlignLibraryToolbarBaselines);
    }

    // Centring sets text of different sizes and faces on different baselines, and where each lands depends on how the
    // display's scaling rounds its slot: fixed 1 DIP nudges that lined them up at 100% left them 2 px apart at 150%. So once
    // laid out, the count's smaller serif and the buttons' sans move onto the category words' baseline as drawn at the
    // window's scaling, a whole device pixel at a time (Display text formatting draws baselines on whole pixels). A margin
    // added to the top and taken from the bottom moves a centred element down by that much, without changing the row.
    private double countNudge, actionNudge;
    private bool libraryToolbarBeside;

    private void PlaceLibraryToolbarActions(double top, double bottom)
    {
        double count = libraryToolbarBeside ? countNudge : 0, actions = libraryToolbarBeside ? actionNudge : 0;
        ResultCount.Margin = new Thickness(12, top + count, 8, bottom - count);
        RescanButton.Margin = new Thickness(0, top + actions, 0, bottom - actions); AddTitleButton.Margin = new Thickness(4, top + actions, 0, bottom - actions);
    }

    /// <summary>Moves the count and the buttons, by whole device pixels, from where their baselines are drawn now onto the
    /// category words' baseline. Each pass corrects from the current layout, so it also settles after the scaling changes.</summary>
    private void AlignLibraryToolbarBaselines()
    {
        if (!libraryToolbarBeside || !LibraryToolbar.IsVisible || FirstText(AllLibraryNav) is not { } words) return;
        var scale = VisualTreeHelper.GetDpi(LibraryToolbar).DpiScaleY;
        var line = BaselinePixel(words, scale);
        // The count's type is inherited, and its text fills it; rolling moves only the pieces inside it.
        var countMove = ResultCount.IsVisible ? line - BaselinePixel(ResultCount, TextElement.GetFontFamily(ResultCount), TextElement.GetFontStyle(ResultCount),
            TextElement.GetFontWeight(ResultCount), TextElement.GetFontStretch(ResultCount), TextElement.GetFontSize(ResultCount), scale) : 0;
        var label = RescanButton.IsVisible ? FirstText(RescanButton) : AddTitleButton.IsVisible ? FirstText(AddTitleButton) : null;
        var actionMove = label is null ? 0 : line - BaselinePixel(label, scale);
        if (countMove == 0 && actionMove == 0) return;
        countNudge += countMove / scale; actionNudge += actionMove / scale;
        PlaceLibraryToolbarActions(0, AllLibraryNav.Margin.Bottom);
    }

    /// <summary>The device-pixel row on which a line of this type, at the top of the element, has its baseline.</summary>
    private double BaselinePixel(FrameworkElement element, FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch, double size, double scale)
    {
        var top = element.TranslatePoint(new Point(0, 0), LibraryToolbar).Y;
        var line = new FormattedText("x", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, style, weight, stretch), size,
            Brushes.Black, null, TextFormattingMode.Display, scale);
        return Math.Round((top + line.Baseline) * scale);
    }
    private double BaselinePixel(TextBlock text, double scale) => BaselinePixel(text, text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch, text.FontSize, scale);

    /// <summary>The first shown text block with words in it, skipping icon glyphs (private-use characters).</summary>
    private static TextBlock? FirstText(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock { IsVisible: true, Text: { Length: > 0 } text } block && text[0] is < '\uE000' or > '\uF8FF') return block;
            if (FirstText(child) is { } found) return found;
        }
        return null;
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
