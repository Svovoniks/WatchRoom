using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
    private static readonly Duration RailPearlDuration = new(TimeSpan.FromMilliseconds(260)), RailFoldDuration = new(TimeSpan.FromMilliseconds(300));
    private bool railCollapsed, railPearlPlaced;
    private DoubleAnimation? railFolding;

    /// <summary>How far the sidebar has folded, from 0 (open) to 1 (a strip of icons); animated between the two.</summary>
    private static readonly DependencyProperty RailFoldProperty = DependencyProperty.Register("RailFold", typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (window, _) => ((MainWindow)window).LayOutRailFold()));
    private double RailFold { get => (double)GetValue(RailFoldProperty); set => SetValue(RailFoldProperty, value); }

    private double RailColumnWidth => Lerp(RailExpandedWidth, RailCollapsedWidth, RailFold);
    private Button[] RailEntries => [HomeNav, SearchNav, LibraryNav, MusicLibraryNav, QueuesNav, RoomNav, SettingsNav];
    /// <summary>The sidebar's words, which fade out before it narrows and back in once it has widened.</summary>
    private IEnumerable<UIElement> RailText => RailEntries.Select(button => button.Content).OfType<Panel>().Where(panel => panel.Children.Count > 1)
        .Select(panel => panel.Children[1]).Append(RailBrandName).Append(NowPlayingText);
    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;

    private void InitializeSideRail()
    {
        railCollapsed = library.Setting(RailCollapsedKey) == "true";
        ApplySideRail(animate: false);
        // The pearl stays beside its entry while the sidebar scrolls, resizes, folds, gains the Music entry or comes back from the player.
        RailScroll.ScrollChanged += (_, _) => PlaceRailPearl(animate: false);
        RailNavigation.SizeChanged += (_, _) => PlaceRailPearl(animate: false);
        RailHeader.SizeChanged += (_, _) => PlaceRailPearl(animate: false);
        RailPearlLayer.SizeChanged += (_, _) => PlaceRailPearl(animate: false);
        RailPearlLayer.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PlaceRailPearl(animate: false));
    }

    private void ToggleRailClick(object sender, RoutedEventArgs e) => SetRailCollapsed(!railCollapsed);

    private void SetRailCollapsed(bool collapsed)
    {
        if (railCollapsed == collapsed) return;
        railCollapsed = collapsed;
        library.Setting(RailCollapsedKey, collapsed ? "true" : "false");
        ApplySideRail(animate: true);
    }

    private void ApplySideRail(bool animate)
    {
        var label = railCollapsed ? "Expand sidebar" : "Collapse sidebar";
        RailToggle.ToolTip = label + " (Ctrl+B)"; AutomationProperties.SetName(RailToggle, label);
        // Without their labels, the icons name themselves in a tooltip. Buttons with their own tooltip keep it.
        foreach (var button in new[] { HomeNav, LibraryNav, MusicLibraryNav, QueuesNav, RoomNav, SettingsNav })
            button.ToolTip = railCollapsed ? AutomationProperties.GetName(button) : null;
        // Unfolding brings the words back first, faded out, so they can fade in as the sidebar widens.
        if (!railCollapsed) ShowRailText(true);
        var from = RailFold; var to = railCollapsed ? 1.0 : 0.0;
        BeginAnimation(RailFoldProperty, null); railFolding = null;
        RailFold = to;
        if (animate && SystemParameters.ClientAreaAnimation && IsLoaded && from != to)
        {
            // The sidebar glides between its widths while the logo slides between its two places and the entries follow it.
            var fold = new DoubleAnimation(from, to, RailFoldDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
            fold.Completed += (_, _) => { if (railFolding == fold && railCollapsed) ShowRailText(false); };
            railFolding = fold; BeginAnimation(RailFoldProperty, fold);
        }
        else if (railCollapsed) ShowRailText(false);
    }

    /// <summary>Shows the sidebar's words, or takes them out of a folded sidebar's layout so its icons centre.</summary>
    private void ShowRailText(bool shown)
    {
        SideRail.SetCollapsed(Rail, !shown);
        NowPlayingText.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lays the sidebar out for how far it has folded.</summary>
    private void LayOutRailFold()
    {
        var fold = RailFold;
        if (Rail.Visibility == Visibility.Visible) RailWidth.Width = new GridLength(RailColumnWidth);
        // The toggle stays where it is, so folding and unfolding happen from one spot. Open, the logo and name sit beside it;
        // folded, the logo sits under it, centred over the column of icons.
        RailBrand.Margin = new Thickness(Lerp(42, 6, fold), Lerp(0, 44, fold), 0, 0);
        // The words are gone within the first third of the fold, before the narrowing sidebar would cut them off.
        var opacity = Math.Clamp(1 - fold * 3, 0, 1);
        foreach (var text in RailText) text.Opacity = opacity;
        // The player button's disc moves to the middle of the strip.
        var inset = Lerp(12, 5, fold);
        NowPlayingButton.Padding = new Thickness(inset, 10, inset, 10);
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
