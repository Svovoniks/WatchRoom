using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private sealed record BrowseLocation(string Page, int Category, string? Series, string? Kind, int? Season,
        string? MediaId, string Search, int Filter, double LibraryOffset, double DetailOffset);
    private readonly List<BrowseLocation> backLocations = [], forwardLocations = [];
    private string currentPage = "";
    private bool restoringNavigation;
    private int navigationGeneration;
    private BrowseLocation CaptureLocation() => new(currentPage, libraryCategory, browseSeries, browseKind, browseSeason,
        selected?.Id, SearchBox.Text, LibraryStatusFilter.SelectedIndex, LibraryScroll.VerticalOffset, Details.VerticalOffset);
    private void RememberLocation()
    {
        if (restoringNavigation || currentPage.Length == 0) return;
        backLocations.Add(CaptureLocation());
        if (backLocations.Count > 100) backLocations.RemoveAt(0);
        forwardLocations.Clear(); navigationGeneration++;
    }
    private bool BrowseHistory(bool back)
    {
        var source = back ? backLocations : forwardLocations;
        var destination = back ? forwardLocations : backLocations;
        if (source.Count == 0) return false;
        destination.Add(CaptureLocation()); var location = source[^1]; source.RemoveAt(source.Count - 1);
        restoringNavigation = true;
        try
        {
            libraryCategory = location.Category; browseSeries = location.Series; browseKind = location.Kind; browseSeason = location.Season;
            SearchBox.Text = location.Search; LibraryStatusFilter.SelectedIndex = location.Filter;
            if (location.Page == "Details" && items.FirstOrDefault(x => x.Id == location.MediaId) is { } item) Select(item);
            else ShowPage(location.Page == "Details" ? "Library" : location.Page);
            UpdateBreadcrumbs();
        }
        finally { restoringNavigation = false; }
        RestoreBrowseOffset(location, ++navigationGeneration);
        return true;
    }
    private async void RestoreBrowseOffset(BrowseLocation location, int generation)
    {
        // Filtering builds the cards asynchronously. Restore after the last filter finishes.
        while (librarySearchCancellation is not null && generation == navigationGeneration && !closing) await Task.Delay(20);
        if (closing || generation != navigationGeneration) return;
        await Dispatcher.InvokeAsync(() =>
        { if (!closing && generation == navigationGeneration) { LibraryScroll.ScrollToVerticalOffset(location.LibraryOffset); Details.ScrollToVerticalOffset(location.DetailOffset); } }, DispatcherPriority.Loaded);
    }
    private void InitializeBackNavigation()
    {
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, (_, e) => { BrowseHistory(true); e.Handled = true; }, (_, e) => { e.CanExecute = backLocations.Count > 0; e.Handled = true; }));
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseForward, (_, e) => { BrowseHistory(false); e.Handled = true; }, (_, e) => { e.CanExecute = forwardLocations.Count > 0; e.Handled = true; }));
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(NavigationMouseUp), true);
        AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(NavigationTouchDown), true);
        AddHandler(UIElement.PreviewTouchUpEvent, new EventHandler<TouchEventArgs>(NavigationTouchUp), true);
        AddHandler(UIElement.PreviewTouchMoveEvent, new EventHandler<TouchEventArgs>(NavigationTouchMove), true);
    }
    private void NavigationMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        navigationMouseTime = Environment.TickCount64;
        BrowseHistory(e.ChangedButton == MouseButton.XButton1); e.Handled = true;
    }
    private static bool GestureControl(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is TextBoxBase or PasswordBox or Slider or ScrollBar or ComboBox) return true;
            if (node is ScrollViewer viewer && viewer.ScrollableWidth > 0) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    private readonly HashSet<int> navigationTouches = [];
    private Point touchNavigationStart;
    private long touchNavigationTime;
    private bool touchNavigationAllowed;
    private void NavigationTouchDown(object? sender, TouchEventArgs e)
    {
        navigationTouches.Add(e.TouchDevice.Id);
        if (navigationTouches.Count != 1) { touchNavigationAllowed = false; return; }
        touchNavigationStart = e.GetTouchPoint(this).Position; touchNavigationTime = Environment.TickCount64;
        touchNavigationAllowed = !GestureControl(e.OriginalSource as DependencyObject);
    }
    private void NavigationTouchMove(object? sender, TouchEventArgs e)
    {
        var delta = e.GetTouchPoint(this).Position - touchNavigationStart;
        if (Math.Abs(delta.Y) > 60 || Math.Abs(delta.Y) > 20 && Math.Abs(delta.Y) > Math.Abs(delta.X)) touchNavigationAllowed = false;
    }
    private void NavigationTouchUp(object? sender, TouchEventArgs e)
    {
        var delta = e.GetTouchPoint(this).Position - touchNavigationStart;
        if (touchNavigationAllowed && navigationTouches.Count == 1 && Environment.TickCount64 - touchNavigationTime < 1200 &&
            Math.Abs(delta.X) >= 100 && Math.Abs(delta.X) > Math.Abs(delta.Y) * 2)
        { BrowseHistory(delta.X > 0); e.Handled = true; }
        navigationTouches.Remove(e.TouchDevice.Id); touchNavigationAllowed = false;
    }
    private int horizontalNavigationDelta;
    private long horizontalNavigationTime, horizontalNavigationCooldown, navigationMouseTime;
    private IntPtr NavigationWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0319) // WM_APPCOMMAND: Windows/driver-configured navigation gesture.
        {
            var command = (int)((lParam.ToInt64() >> 16) & 0x0fff);
            if (command is 1 or 2) { if (Environment.TickCount64 - navigationMouseTime > 100) BrowseHistory(command == 1); handled = true; return new IntPtr(1); }
        }
        if (message == 0x020a) horizontalNavigationDelta = 0; // Vertical scrolling must stay scrolling.
        if (message != 0x020e || GestureControl(Mouse.DirectlyOver as DependencyObject)) return IntPtr.Zero;
        var now = Environment.TickCount64;
        if (now < horizontalNavigationCooldown) { handled = true; return IntPtr.Zero; }
        var delta = (short)((wParam.ToInt64() >> 16) & 0xffff);
        if (now - horizontalNavigationTime > 300 || Math.Sign(delta) != Math.Sign(horizontalNavigationDelta)) horizontalNavigationDelta = 0;
        horizontalNavigationTime = now; horizontalNavigationDelta += delta; handled = true;
        if (Math.Abs(horizontalNavigationDelta) >= 360)
        { BrowseHistory(horizontalNavigationDelta < 0); horizontalNavigationDelta = 0; horizontalNavigationCooldown = now + 900; }
        return IntPtr.Zero;
    }
}
