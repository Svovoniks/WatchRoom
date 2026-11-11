using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BlackPearl.Desktop;

/// <summary>
/// A line of text, such as "218 items", whose count rolls when it changes. Only the digits that differ move: each old digit
/// slides out and its replacement slides in, upward when the count grows and downward when it shrinks, the ones place first
/// and each higher place a moment later. A word that changes, such as "items" becoming "item", fades across in place. A part
/// that changes width (a digit appearing, a word gaining a letter) eases to its new width, so nothing beside it jumps. While a
/// new count is on its way the old one dims rather than giving way to a status, so the count rolls straight from one number to
/// the next. Text without a count changes at once. The figures follow the element's typography, such as old-style figures.
/// </summary>
/// <remarks>
/// Text that has not rolled, such as the first count after "Loading library…", is one text block whose text changes; putting
/// new elements in the window on every change measurably stalled the library's first layout. A count that rolls stays as its
/// rolled pieces until the next change, since handing back to the text block would redraw it a hair differently.
/// </remarks>
public sealed class RollingText : Decorator
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RollingText),
        new PropertyMetadata("", (control, change) => ((RollingText)control).Show((string?)change.OldValue ?? "", (string?)change.NewValue ?? "")));
    /// <summary>The text shown. Setting it ends a pending dim even when the text is unchanged, as when a search finds as many
    /// titles as before, since an unchanged value raises no change.</summary>
    public string Text { get => (string)GetValue(TextProperty); set { SetCurrentValue(PendingProperty, false); SetValue(TextProperty, value); } }

    /// <summary>Whether a new count is on its way, which dims the one shown. Another count can follow this one by binding both
    /// its text and this, as Search's count follows the library's.</summary>
    public static readonly DependencyProperty PendingProperty = DependencyProperty.Register(nameof(Pending), typeof(bool), typeof(RollingText),
        new PropertyMetadata(false, (control, change) => ((RollingText)control).Dim((bool)change.NewValue)));
    public bool Pending { get => (bool)GetValue(PendingProperty); set => SetValue(PendingProperty, value); }

    private static readonly TimeSpan RollTime = TimeSpan.FromMilliseconds(420), Stagger = TimeSpan.FromMilliseconds(55), DimTime = TimeSpan.FromMilliseconds(160);
    private const double DimmedOpacity = 0.4;
    private readonly Grid host = new();
    private readonly TextBlock display = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private StackPanel? rolling;
    private readonly List<UIElement> leaving = [];
    private readonly List<FrameworkElement> arriving = [];
    private DispatcherTimer? settle;

    public RollingText()
    {
        Child = host; host.Children.Add(display);
    }

    /// <summary>A new count is on its way: the count shown dims until it arrives, or the status shows if there is no count yet.</summary>
    public void ShowPending(string status)
    {
        if (Split(Text).Number.Length == 0) { Text = status; return; }
        SetCurrentValue(PendingProperty, true);
    }

    private void Show(string from, string to)
    {
        AutomationProperties.SetName(this, to);
        // A new count has arrived. Set without replacing a binding, so a count that follows another keeps following it.
        SetCurrentValue(PendingProperty, false); EndRoll();
        display.Text = to;
        var (fromNumber, fromAfter) = Split(from); var (toNumber, toAfter) = Split(to);
        if (!IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation || fromNumber.Length == 0 || toNumber.Length == 0 || from == to) return;

        var grows = toNumber.Length != fromNumber.Length ? toNumber.Length > fromNumber.Length : string.CompareOrdinal(toNumber, fromNumber) > 0;
        var direction = grows ? 1 : -1;
        var delay = TimeSpan.Zero;
        var places = Math.Max(fromNumber.Length, toNumber.Length);
        var digits = new List<UIElement>();
        for (var place = 0; place < places; place++)
        {
            var was = place < fromNumber.Length ? fromNumber[^(place + 1)].ToString() : null;
            var now = place < toNumber.Length ? toNumber[^(place + 1)].ToString() : null;
            if (was == now) { digits.Add(Cell(null, now, 0, TimeSpan.Zero)); continue; }
            digits.Add(Cell(was, now, direction, delay)); delay += Stagger;
        }
        digits.Reverse();
        // The row as a whole clips the pieces sliding above and below it. Clipping each piece instead would also trim the
        // edges of its glyph, and the text would redraw differently once the clip came off.
        rolling = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, ClipToBounds = true };
        foreach (var digit in digits) rolling.Children.Add(digit);
        // Words are not wheels: a changed word, such as "item" becoming "items", fades across in place.
        rolling.Children.Add(fromAfter == toAfter ? Cell(null, toAfter, 0, TimeSpan.Zero) : Cell(fromAfter, toAfter, direction, delay, roll: false));
        // The text block keeps the new text, and so the new size, underneath the pieces. The pieces stay once they have rolled:
        // handing back to the text block would draw the same text a hair differently, which shows as a flicker.
        display.Visibility = Visibility.Hidden; host.Children.Add(rolling);
        settle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = RollTime + delay + TimeSpan.FromMilliseconds(40) };
        settle.Tick += (_, _) => Settle();
        settle.Start();
    }

    /// <summary>Once everything has arrived, drops the pieces that left and the arrivals' animations, leaving the pieces as
    /// still text that draws exactly as it did when the roll ended.</summary>
    private void Settle()
    {
        settle?.Stop(); settle = null;
        foreach (var element in leaving) if (VisualTreeHelper.GetParent(element) is Panel panel) panel.Children.Remove(element);
        foreach (var element in arriving)
        {
            element.BeginAnimation(OpacityProperty, null); element.Opacity = 1; element.RenderTransform = null;
            if (VisualTreeHelper.GetParent(element) is Grid cell) { cell.BeginAnimation(WidthProperty, null); cell.Width = double.NaN; }
        }
        // Places the count no longer has are empty by now.
        if (rolling is not null) foreach (var cell in rolling.Children.OfType<Panel>().Where(cell => cell.Children.Count == 0).ToList()) rolling.Children.Remove(cell);
        leaving.Clear(); arriving.Clear();
    }

    /// <summary>Shows the text block again in place of any rolling pieces.</summary>
    private void EndRoll()
    {
        settle?.Stop(); settle = null; leaving.Clear(); arriving.Clear();
        if (rolling is not null) { host.Children.Remove(rolling); rolling = null; }
        display.Visibility = Visibility.Visible;
    }

    private void Dim(bool dimmed)
    {
        var target = dimmed ? DimmedOpacity : 1;
        if (!SystemParameters.ClientAreaAnimation) { host.BeginAnimation(OpacityProperty, null); host.Opacity = target; return; }
        host.BeginAnimation(OpacityProperty, new DoubleAnimation(target, DimTime));
    }

    /// <summary>A leading run of digits and whatever follows it.</summary>
    private static (string Number, string After) Split(string text)
    {
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        return (text[..digits], text[digits..]);
    }

    /// <summary>A clipped slot showing one part of the text, rolling from what it showed to what it shows now.</summary>
    private Grid Cell(string? was, string? now, int direction, TimeSpan delay, bool roll = true)
    {
        var cell = new Grid();
        // A word lines up from its start, so the letters two words share overlap exactly as one fades into the other.
        var align = roll ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var incoming = now is { Length: > 0 } ? Glyph(now, align) : null;
        if (incoming is not null) cell.Children.Add(incoming);
        if (direction == 0) return cell;
        var outgoing = was is { Length: > 0 } ? Glyph(was, align) : null;
        if (outgoing is not null) { cell.Children.Add(outgoing); leaving.Add(outgoing); }
        if (incoming is not null) arriving.Add(incoming);
        var distance = roll ? TextElement.GetFontSize(this) * 1.15 : 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (incoming is not null) Slide(incoming, direction * distance, 0, 0, 1, delay, ease);
        if (outgoing is not null) Slide(outgoing, 0, -direction * distance, 1, 0, delay, ease);
        // The slot eases between the two widths, so its neighbours glide rather than jump. The widths are whole screen pixels,
        // as layout rounds them, so the slot is exactly as wide as it will be once it sizes itself again.
        var pixel = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var before = Math.Round(MeasureWidth(outgoing) * pixel) / pixel; var after = Math.Round(MeasureWidth(incoming) * pixel) / pixel;
        if (Math.Abs(before - after) > 0.1)
        {
            cell.Width = before;
            cell.BeginAnimation(WidthProperty, new DoubleAnimation(before, after, RollTime) { BeginTime = delay, EasingFunction = ease });
        }
        return cell;
    }

    private static void Slide(FrameworkElement element, double fromY, double toY, double fromOpacity, double toOpacity, TimeSpan delay, IEasingFunction ease)
    {
        var shift = new TranslateTransform(0, fromY); element.RenderTransform = shift; element.Opacity = fromOpacity;
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, toY, RollTime) { BeginTime = delay, EasingFunction = ease });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, toOpacity, RollTime) { BeginTime = delay, EasingFunction = ease });
    }

    private static TextBlock Glyph(string text, HorizontalAlignment align = HorizontalAlignment.Right) => new() { Text = text, HorizontalAlignment = align };

    /// <summary>How wide a piece is in this element's type, measured before it is shown.</summary>
    private double MeasureWidth(TextBlock? block)
    {
        if (block is null) return 0;
        block.FontFamily = TextElement.GetFontFamily(this); block.FontSize = TextElement.GetFontSize(this); block.FontWeight = TextElement.GetFontWeight(this);
        block.FontStyle = TextElement.GetFontStyle(this); block.FontStretch = TextElement.GetFontStretch(this);
        Typography.SetNumeralStyle(block, Typography.GetNumeralStyle(this)); Typography.SetNumeralAlignment(block, Typography.GetNumeralAlignment(this));
        TextOptions.SetTextFormattingMode(block, TextOptions.GetTextFormattingMode(this));
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return block.DesiredSize.Width;
    }
}
