using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BlackPearl.Desktop;

/// <summary>
/// The app's pick-one selector: words strung on a thread. A thread runs under each row of choices and trails off past the
/// last, and the stretch of thread under the chosen word is lit in the pearl's colours, gliding to the next choice when the
/// choice changes. It wraps a panel of choices, radio buttons (the checked one is chosen) or buttons marked chosen by an
/// automation item status of "Selected", or a list whose selected item is the choice. Each choice leaves room for the thread with its bottom margin and keeps the space
/// after its word as right padding, as the ThreadWord and ThreadOption styles do. The light and its colours are in the
/// template (App.xaml), so they follow the theme.
/// </summary>
public sealed class ThreadBar : ContentControl
{
    /// <summary>How far the light reaches past each end of the chosen word, so its fades fall outside the word.</summary>
    public static readonly DependencyProperty ReachProperty = DependencyProperty.Register(nameof(Reach), typeof(double), typeof(ThreadBar), new PropertyMetadata(7.0));
    public double Reach { get => (double)GetValue(ReachProperty); set => SetValue(ReachProperty, value); }

    /// <summary>How far the thread trails off past the last word; shorter where the selector sits against an edge.</summary>
    public static readonly DependencyProperty TailProperty = DependencyProperty.Register(nameof(Tail), typeof(double), typeof(ThreadBar), new PropertyMetadata(48.0));
    public double Tail { get => (double)GetValue(TailProperty); set => SetValue(TailProperty, value); }
    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly DependencyPropertyDescriptor ItemStatus = DependencyPropertyDescriptor.FromProperty(AutomationProperties.ItemStatusProperty, typeof(UIElement));
    private readonly List<Rectangle> threads = [];
    private readonly HashSet<FrameworkElement> watched = [];
    private readonly EventHandler statusChanged; private readonly SizeChangedEventHandler choiceResized, contentResized; private readonly EventHandler containersChanged;
    private Canvas? layer;
    private FrameworkElement? light;
    private bool placed, queued, queuedGlide;

    /// <summary>The lit stretch under the chosen word.</summary>
    public FrameworkElement? Light => light;
    /// <summary>The thread under each row of choices, top row first.</summary>
    public IReadOnlyList<FrameworkElement> Threads => threads;

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
        layer = GetTemplateChild("PART_Layer") as Canvas; light = GetTemplateChild("PART_Light") as FrameworkElement;
        // A transform set in a shared template is frozen, so the light gets its own to move by.
        if (light is not null) light.RenderTransform = new TranslateTransform();
        threads.Clear(); placed = false; Queue(glide: false);
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
        // A list replaces an item's container when the item is replaced; the old one is let go.
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

    /// <summary>Lays the threads and light out once the choices have settled, gliding if any change asked to. A change of
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
        // Rows of choices, each with its thread midway through the room the choices leave under themselves.
        var rows = choices.GroupBy(choice => Math.Round(choice.TranslatePoint(new Point(0, 0), this).Y)).OrderBy(row => row.Key).Select(row =>
        {
            var left = row.Min(choice => Word(choice).Left); var right = row.Max(choice => Word(choice).Right);
            var line = Math.Round(row.Max(choice => choice.TranslatePoint(new Point(0, choice.ActualHeight + choice.Margin.Bottom / 2), this).Y));
            return (Choices: row.ToList(), Left: left, Right: right, Line: line);
        }).ToList();
        while (threads.Count > rows.Count) { layer.Children.Remove(threads[^1]); threads.RemoveAt(threads.Count - 1); }
        while (threads.Count < rows.Count) { var thread = Thread(); threads.Add(thread); layer.Children.Insert(0, thread); }
        for (var i = 0; i < rows.Count; i++)
        {
            var (_, left, right, line) = rows[i]; var thread = threads[i];
            thread.Width = right - left + Tail; Canvas.SetLeft(thread, left); Canvas.SetTop(thread, line);
            // Solid under the words, fading out over the tail.
            ((LinearGradientBrush)thread.OpacityMask).GradientStops[1].Offset = thread.Width > 0 ? (right - left) / thread.Width : 1;
        }

        var chosen = choices.FirstOrDefault(IsChosen);
        if (chosen is null || !IsVisible)
        {
            light.Visibility = Visibility.Hidden; placed = false;
            return;
        }
        var word = Word(chosen);
        var x = word.Left - Reach; var width = word.Width + 2 * Reach;
        var y = rows.First(row => row.Choices.Contains(chosen)).Line + 0.5 - light.Height / 2;
        var shift = (TranslateTransform)light.RenderTransform;
        if (!placed || !glide || !SystemParameters.ClientAreaAnimation)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null); light.BeginAnimation(WidthProperty, null);
            shift.X = x; shift.Y = y; light.Width = width;
            light.Visibility = Visibility.Visible; placed = true;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, GlideDuration) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, GlideDuration) { EasingFunction = ease });
        light.BeginAnimation(WidthProperty, new DoubleAnimation(width, GlideDuration) { EasingFunction = ease });
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

    /// <summary>Where a choice's word lies, leaving out the space after it, which is part of the choice's padding.</summary>
    private Rect Word(FrameworkElement choice)
    {
        var padding = choice is Control control ? control.Padding : default;
        var at = choice.TranslatePoint(new Point(padding.Left, 0), this);
        return new Rect(at.X, at.Y, Math.Max(0, choice.ActualWidth - padding.Left - padding.Right), choice.ActualHeight);
    }

    private static Rectangle Thread()
    {
        var thread = new Rectangle { Height = 1, Opacity = 0.45, IsHitTestVisible = false };
        thread.SetResourceReference(Shape.FillProperty, "Muted");
        thread.OpacityMask = new LinearGradientBrush([new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.8), new GradientStop(Colors.Transparent, 1)], new Point(0, 0), new Point(1, 0));
        return thread;
    }
}
