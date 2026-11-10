using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
// A pearl strung on its edge marks the current page and rolls to the next one.
public partial class MainWindow
{
    private const double RailExpandedWidth = 212, RailCollapsedWidth = 64;
    private const string RailCollapsedKey = "railCollapsed";
    private static readonly Duration RailPearlDuration = new(TimeSpan.FromMilliseconds(260));
    private bool railCollapsed, railPearlPlaced;

    private double RailColumnWidth => railCollapsed ? RailCollapsedWidth : RailExpandedWidth;
    private Button[] RailEntries => [HomeNav, SearchNav, LibraryNav, MusicLibraryNav, QueuesNav, RoomNav, SettingsNav];

    private void InitializeSideRail()
    {
        railCollapsed = library.Setting(RailCollapsedKey) == "true";
        ApplySideRail();
        // The pearl stays beside its entry while the sidebar scrolls, resizes, gains the Music entry or comes back from the player.
        RailScroll.ScrollChanged += (_, _) => PlaceRailPearl(animate: false);
        RailNavigation.SizeChanged += (_, _) => PlaceRailPearl(animate: false);
        RailPearlLayer.SizeChanged += (_, _) => PlaceRailPearl(animate: false);
        RailPearlLayer.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceRailPearl(animate: false));
    }

    private void ToggleRailClick(object sender, RoutedEventArgs e)
    {
        SetRailCollapsed(!railCollapsed);
        // A click focuses the toggle, and a focused toggle stays over the folded strip's logo. Only keyboard focus should
        // keep it there, so a click hands focus to the window, where keyboard shortcuts are handled.
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice || !RailToggle.IsKeyboardFocused) return;
        Keyboard.Focus(this);
        if (RailToggle.IsKeyboardFocused) Keyboard.ClearFocus();
    }

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

    /// <summary>Rolls the pearl to the newly selected entry once the sidebar has updated.</summary>
    private void QueueRailPearl() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceRailPearl(animate: true));

    /// <summary>Puts the pearl beside the selected sidebar entry, rolling there when asked and it is already showing.</summary>
    private void PlaceRailPearl(bool animate)
    {
        if (RailPearl is null) return;
        var target = RailEntries.FirstOrDefault(button => button.IsVisible && AutomationProperties.GetItemStatus(button) == "Selected");
        // An entry scrolled out of the sidebar's view has no pearl beside it; Settings is outside the scrolling part.
        var shown = target is not null && RailPearlLayer.IsVisible && target.ActualHeight > 0 && (target == SettingsNav ||
            target.TranslatePoint(new Point(0, target.ActualHeight / 2), RailScroll).Y is var middle && middle >= 0 && middle <= RailScroll.ActualHeight);
        var shift = (TranslateTransform)RailPearl.RenderTransform;
        if (!shown)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            RailPearl.Visibility = Visibility.Hidden; railPearlPlaced = false;
            return;
        }
        // Centred on the sidebar's one-pixel edge, whatever its width.
        Canvas.SetLeft(RailPearl, RailPearlLayer.ActualWidth - 0.5 - RailPearl.Width / 2);
        var top = Math.Round(target!.TranslatePoint(new Point(0, target.ActualHeight / 2), RailPearlLayer).Y - RailPearl.Height / 2);
        if (!railPearlPlaced || !animate || !SystemParameters.ClientAreaAnimation)
        {
            // A layout change while the pearl rolls lands it where it was heading.
            shift.BeginAnimation(TranslateTransform.YProperty, null); shift.Y = top;
            RailPearl.Visibility = Visibility.Visible; railPearlPlaced = true;
            return;
        }
        if (shift.Y == top) return;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(top, RailPearlDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }
}
