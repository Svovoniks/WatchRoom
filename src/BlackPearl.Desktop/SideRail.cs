using System.Windows;
using System.Windows.Automation;

namespace BlackPearl.Desktop;

/// <summary>
/// Whether the sidebar is folded into a strip of icons. It is set on the sidebar and inherited by everything in it,
/// so the navigation styles hide their labels and narrow their padding from one switch.
/// </summary>
public static class SideRail
{
    public static readonly DependencyProperty CollapsedProperty = DependencyProperty.RegisterAttached("Collapsed", typeof(bool), typeof(SideRail),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetCollapsed(DependencyObject element) => (bool)element.GetValue(CollapsedProperty);
    public static void SetCollapsed(DependencyObject element, bool value) => element.SetValue(CollapsedProperty, value);
}

// The sidebar folds into a thin strip of icons from its toggle or Ctrl+B, and stays that way across launches.
public partial class MainWindow
{
    private const double RailExpandedWidth = 212, RailCollapsedWidth = 64;
    private const string RailCollapsedKey = "railCollapsed";
    private bool railCollapsed;

    private double RailColumnWidth => railCollapsed ? RailCollapsedWidth : RailExpandedWidth;

    private void InitializeSideRail()
    {
        railCollapsed = library.Setting(RailCollapsedKey) == "true";
        ApplySideRail();
    }

    private void ToggleRailClick(object sender, RoutedEventArgs e) => SetRailCollapsed(!railCollapsed);

    private void SetRailCollapsed(bool collapsed)
    {
        if (railCollapsed == collapsed) return;
        railCollapsed = collapsed;
        library.Setting(RailCollapsedKey, collapsed ? "true" : "false");
        ApplySideRail();
    }

    private void ApplySideRail()
    {
        SideRail.SetCollapsed(Rail, railCollapsed);
        if (Rail.Visibility == Visibility.Visible) RailWidth.Width = new GridLength(RailColumnWidth);
        // Folded, the logo is centred at the top of the strip and the toggle lies over it, showing only while pointed at or
        // focused (MainWindow.xaml); open, the toggle sits at the end of the brand row.
        RailBrand.Margin = railCollapsed ? new Thickness(0) : new Thickness(10, 0, 0, 0);
        RailBrand.HorizontalAlignment = railCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        RailToggle.HorizontalAlignment = railCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        NowPlayingText.Visibility = railCollapsed ? Visibility.Collapsed : Visibility.Visible;
        var label = railCollapsed ? "Expand sidebar" : "Collapse sidebar";
        RailToggle.ToolTip = label + " (Ctrl+B)"; AutomationProperties.SetName(RailToggle, label);
        NowPlayingButton.Padding = railCollapsed ? new Thickness(0, 10, 0, 10) : new Thickness(12, 10, 12, 10);
        NowPlayingButton.HorizontalContentAlignment = railCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        // Without their labels, the icons name themselves in a tooltip. Buttons with their own tooltip keep it.
        foreach (var button in new[] { HomeNav, LibraryNav, MusicLibraryNav, QueuesNav, RoomNav, SettingsNav })
            button.ToolTip = railCollapsed ? AutomationProperties.GetName(button) : null;
    }
}
