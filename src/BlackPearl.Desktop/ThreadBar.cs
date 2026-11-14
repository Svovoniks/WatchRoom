using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BlackPearl.Desktop;

/// <summary>
/// The app's pick-one selector: a row of words with a line of light, in the pearl's colours, just under the chosen one and a
/// soft glow rising from it behind the word. When the choice changes the light squishes into a bead, travels to the next
/// word and opens out under it. It wraps a panel of choices, radio buttons (the checked one is chosen) or buttons marked
/// chosen by an automation item status of "Selected", or a list whose selected item is the choice. The light hangs just under
/// the words' baseline. Each choice keeps the space after its word as right padding, as the ThreadWord and ThreadOption
/// styles do. The light and its colours are in the template (App.xaml), so they follow the theme.
/// </summary>
public sealed class ThreadBar : ContentControl
{
    /// <summary>How far the light reaches past each end of the chosen word, so its fades fall outside the word.</summary>
    public static readonly DependencyProperty ReachProperty = DependencyProperty.Register(nameof(Reach), typeof(double), typeof(ThreadBar), new PropertyMetadata(7.0));
    public double Reach { get => (double)GetValue(ReachProperty); set => SetValue(ReachProperty, value); }

    // A change of choice: the light travels to the new word, narrowing as it speeds up and widening as it slows, a bead at full
    // speed halfway and the new word's width as it arrives.
    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(280));
    /// <summary>How wide the light is at full speed.</summary>
    private const double Bead = 12;
    /// <summary>How far along its glide the light is, from 0 to 1; each step places it.</summary>
    private static readonly DependencyProperty GlideProperty = DependencyProperty.Register("Glide", typeof(double), typeof(ThreadBar), new PropertyMetadata(0.0, (bar, change) => ((ThreadBar)bar).Glided((double)change.NewValue)));
    private (double FromCentre, double ToCentre, double FromWidth, double ToWidth, double FromY, double ToY, double Bead, double Glow) trip;
    /// <summary>How high the glow rises behind the word, in the word's own size.</summary>
    private const double Rise = 1.25;
    private static readonly DependencyPropertyDescriptor ItemStatus = DependencyPropertyDescriptor.FromProperty(AutomationProperties.ItemStatusProperty, typeof(UIElement));
    private readonly List<double> lines = [];
    private readonly HashSet<FrameworkElement> watched = [];
    private readonly EventHandler statusChanged; private readonly SizeChangedEventHandler choiceResized, contentResized; private readonly EventHandler containersChanged;
    private Canvas? layer;
    private FrameworkElement? light, glow;
    /// <summary>The glow's strength at rest, as the template sets it; it fades from this while the light travels.</summary>
    private double glowStrength;
    private bool placed, queued, queuedGlide;

    /// <summary>The lit stretch under the chosen word.</summary>
    public FrameworkElement? Light => light;
    /// <summary>Where the light runs under each row of choices, top row first.</summary>
    public IReadOnlyList<double> Lines => lines;

    public ThreadBar()
    {
        Focusable = false; IsTabStop = false;
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Queue(glide: true)));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => Queue(glide: true)));
        statusChanged = (_, _) => Queue(glide: true);
        choiceResized = (_, _) => Queue(glide: false); contentResized = (_, _) => Queue(glide: false);
        containersChanged = (_, _) => { Watch(); Queue(glide: false); };
        IsVisibleChanged += (_, _) => { placed = false; Queue(glide: false); };
        // Watching a choice's status registers it with WPF for good, which would keep the choice, this bar and the dialog or page
        // around it alive after they close. So the choices are watched only while the bar is shown, and let go when it unloads.
        Loaded += (_, _) => { Watch(); Queue(glide: false); };
        Unloaded += (_, _) => Unwatch();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        layer = GetTemplateChild("PART_Layer") as Canvas; light = GetTemplateChild("PART_Light") as FrameworkElement; glow = GetTemplateChild("PART_Glow") as FrameworkElement;
        // A transform set in a shared template is frozen, so the light gets its own to move by. The glow, in a layer behind the
        // words, moves by the same one.
        if (light is not null) light.RenderTransform = new TranslateTransform();
        if (glow is not null && light is not null) { glow.RenderTransform = light.RenderTransform; glowStrength = glow.Opacity; }
        lines.Clear(); placed = false; Queue(glide: false);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        Unwatch();
        if (oldContent is FrameworkElement old) old.SizeChanged -= contentResized;
        if (oldContent is ItemsControl oldList) oldList.ItemContainerGenerator.StatusChanged -= containersChanged;
        if (newContent is FrameworkElement content) content.SizeChanged += contentResized;
        // A list makes its items' containers as it lays out, so they are watched as they appear.
        if (newContent is ItemsControl list) list.ItemContainerGenerator.StatusChanged += containersChanged;
        Watch(); Queue(glide: false);
    }

    /// <summary>Follows the choices: a new choice glides the light, and a choice changing size moves it with the words.</summary>
    private void Watch()
    {
        if (!IsLoaded || !IsVisible) return;
        // A list replaces an item's container when the item is replaced; the old one is let go here, the next time the bar is laid
        // out while showing, or when it unloads.
        var current = Choices().ToHashSet();
        foreach (var gone in watched.Where(choice => !current.Contains(choice)).ToList()) { ItemStatus.RemoveValueChanged(gone, statusChanged); gone.SizeChanged -= choiceResized; watched.Remove(gone); }
        foreach (var choice in current)
        {
            if (!watched.Add(choice)) continue;
            ItemStatus.AddValueChanged(choice, statusChanged);
            choice.SizeChanged += choiceResized;
        }
    }

    private void Unwatch()
    {
        foreach (var choice in watched) { ItemStatus.RemoveValueChanged(choice, statusChanged); choice.SizeChanged -= choiceResized; }
        watched.Clear();
    }

    /// <summary>Lays the light out once the choices have settled, gliding if any change asked to. A change of
    /// choice clears one mark and sets another; waiting for both keeps the light from vanishing in between.</summary>
    private void Queue(bool glide)
    {
        // A hidden bar, such as one on a page not showing, has nothing to lay out; it does so when it is shown, as becoming visible
        // queues it again. Many bars wait on hidden pages at startup, and laying them all out slowed the first page's layout.
        if (!IsVisible) { placed = false; return; }
        queuedGlide |= glide;
        if (queued) return;
        queued = true;
        // Below input, so laying out the light never holds up the pointer or keyboard, as it did at startup; it waits a frame at most.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { queued = false; var g = queuedGlide; queuedGlide = false; Place(g); });
    }

    private void Place(bool glide)
    {
        if (layer is null || light is null) return;
        Watch();
        var choices = Choices().Where(choice => choice.IsVisible && choice.ActualWidth > 0).ToList();
        // Rows of choices, each with the line its light runs on hung under the row's words.
        var rows = choices.GroupBy(choice => Math.Round(choice.TranslatePoint(new Point(0, 0), this).Y)).OrderBy(row => row.Key)
            .Select(row => (Choices: row.ToList(), Line: Math.Round(row.Max(Line)))).ToList();
        lines.Clear(); lines.AddRange(rows.Select(row => row.Line));

        var chosen = choices.FirstOrDefault(IsChosen);
        if (chosen is null || !IsVisible)
        {
            light.Visibility = Visibility.Hidden; placed = false;
            if (glow is not null) glow.Visibility = Visibility.Hidden;
            return;
        }
        var word = Word(chosen);
        var x = word.Left - Reach; var width = word.Width + 2 * Reach;
        var y = rows.First(row => row.Choices.Contains(chosen)).Line + 0.5 - light.Height / 2;
        // The glow rises from the line to the top of the word's letters.
        if (glow is not null && TextOf(chosen) is { } text)
        {
            var rise = Math.Round(Rise * text.FontSize);
            glow.Height = rise; Canvas.SetTop(glow, light.Height / 2 - rise);
        }
        var shift = (TranslateTransform)light.RenderTransform;
        if (!placed || !glide || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(GlideProperty, null);
            shift.X = x; shift.Y = y; light.Width = width;
            light.Visibility = Visibility.Visible; placed = true;
            if (glow is not null) { glow.Width = width; glow.Opacity = glowStrength; glow.Visibility = Visibility.Visible; }
            return;
        }
        // From wherever the light is now, mid-glide included.
        trip = (shift.X + light.Width / 2, x + width / 2, light.Width, width, shift.Y, y, Math.Min(Bead, Math.Min(light.Width, width)), glowStrength);
        BeginAnimation(GlideProperty, new DoubleAnimation(0, 1, GlideDuration));
    }

    /// <summary>Places the light part way along its trip. Its centre eases in and out; its narrowing follows its speed, so it
    /// thins as it sets off rather than before, is a bead at full speed, and opens out as it slows into the new word. The glow
    /// fades with it, so it never stands as a narrow column over the bead.</summary>
    private void Glided(double t)
    {
        if (light?.RenderTransform is not TranslateTransform shift) return;
        var travelled = (1 - Math.Cos(Math.PI * t)) / 2; var speed = Math.Sin(Math.PI * t); // speed relative to its fastest, halfway
        var width = trip.FromWidth + (trip.ToWidth - trip.FromWidth) * travelled;
        width -= (width - trip.Bead) * speed;
        var centre = trip.FromCentre + (trip.ToCentre - trip.FromCentre) * travelled;
        shift.X = centre - width / 2; shift.Y = trip.FromY + (trip.ToY - trip.FromY) * travelled; light.Width = width;
        if (glow is not null) { glow.Width = width; glow.Opacity = trip.Glow * (1 - speed); }
    }

    /// <summary>The choices: a panel's children, or a list's item containers.</summary>
    private IEnumerable<FrameworkElement> Choices() => Content switch
    {
        ItemsControl list => Enumerable.Range(0, list.Items.Count).Select(i => list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<FrameworkElement>(),
        Panel panel => panel.Children.OfType<FrameworkElement>(),
        _ => [],
    };

    /// <summary>Whether a choice is the chosen one: a checked radio button, a selected list item, or one marked Selected.</summary>
    public static bool IsChosen(FrameworkElement choice) =>
        choice is ToggleButton { IsChecked: true } or ListBoxItem { IsSelected: true } || AutomationProperties.GetItemStatus(choice) == "Selected";

    /// <summary>Where the light runs under a choice: hung a little under its word's baseline, clear of descenders but close enough
    /// to read as the word's own line. A choice without text has it midway through the room it leaves under itself.</summary>
    private double Line(FrameworkElement choice)
    {
        var text = TextOf(choice);
        if (text is null) return choice.TranslatePoint(new Point(0, choice.ActualHeight + choice.Margin.Bottom / 2), this).Y;
        // The baseline of the text's first line, from the font itself, so a serif with tall line spacing hangs its light as
        // closely as the interface's sans.
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var baseline = new FormattedText("x", CultureInfo.InvariantCulture, text.FlowDirection, typeface, text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip).Baseline;
        return text.TranslatePoint(new Point(0, baseline), this).Y + Hang * text.FontSize;
    }

    /// <summary>How far under the baseline the light hangs, in the word's own size.</summary>
    private const double Hang = 0.6;

    private static TextBlock? TextOf(FrameworkElement choice) => Texts(choice).FirstOrDefault(text => text.IsVisible && text.ActualHeight > 0);

    private static IEnumerable<TextBlock> Texts(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock text) yield return text;
            foreach (var nested in Texts(child)) yield return nested;
        }
    }

    /// <summary>Where a choice's word lies, leaving out the space after it, which is part of the choice's padding.</summary>
    private Rect Word(FrameworkElement choice)
    {
        var padding = choice is Control control ? control.Padding : default;
        var at = choice.TranslatePoint(new Point(padding.Left, 0), this);
        return new Rect(at.X, at.Y, Math.Max(0, choice.ActualWidth - padding.Left - padding.Right), choice.ActualHeight);
    }
}
