using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Watchroom.Desktop;

// A dialog drawn inside the app: a dimmed layer over the owner window with a card in the middle, in place of a
// separate window. It lives in a popup so it also covers the video, which draws in its own native window. The
// popup is owned by the app window and is not topmost, so it never floats over other apps and minimizes with
// the app. ShowDialog waits for an answer the way Window.ShowDialog does.
public class InAppDialog : ContentControl
{
    private static readonly List<InAppDialog> open = [];
    public static IReadOnlyList<InAppDialog> Open => open;
    private readonly Popup popup;
    private readonly Grid layer;
    private readonly Border card;
    private readonly TextBlock heading;
    private readonly ScaleTransform scale = new(1, 1);
    private DispatcherFrame? frame;
    private IInputElement? restoreFocus;
    private Window? attached;
    private bool? result;
    private const double Gutter = 48, HeaderHeight = 54;

    public InAppDialog()
    {
        Focusable = false;
        heading = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var close = new Button { Content = "", FontSize = 12, Margin = new Thickness(0), ToolTip = "Close (Esc)" };
        close.SetResourceReference(StyleProperty, "QuietIcon");
        AutomationProperties.SetName(close, "Close");
        close.Click += (_, _) => Close();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(heading);
        var body = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header); body.Children.Add(this);
        card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(24, 16, 16, 22), Child = body,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.SetResourceReference(EffectProperty, "PopupShadow");
        card.SetResourceReference(TextElement.ForegroundProperty, "Ink");
        AutomationProperties.SetIsDialog(card, true);
        layer = new Grid { Children = { card } };
        layer.SetResourceReference(Panel.BackgroundProperty, "Scrim");
        TextElement.SetFontFamily(layer, new FontFamily("Segoe UI")); TextElement.SetFontSize(layer, 14);
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(layer, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(layer, KeyboardNavigationMode.Contained);
        layer.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, layer) && CloseOnOutsideClick) { Close(); e.Handled = true; } };
        layer.KeyDown += (_, e) => { if (e.Key == Key.Escape && !e.Handled) { Close(); e.Handled = true; } };
        // A Cancel button answers "no" and closes, as it does in a window.
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is Button { IsCancel: true }) DialogResult = false; }));
        // Not part of any window's tree, so keys typed here never reach the app's own shortcuts.
        popup = new Popup { Child = layer, AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true, PopupAnimation = PopupAnimation.None };
        popup.Opened += (_, _) => KeepWithOwner();
    }

    public Window? Owner { get; set; }
    public string Title
    {
        get => heading.Text;
        set { heading.Text = value; AutomationProperties.SetName(card, value); }
    }
    // Panels that sit over the app close on a click outside them; questions wait for an answer.
    public bool CloseOnOutsideClick { get; set; }
    public bool IsOpen => popup.IsOpen;
    public bool? DialogResult
    {
        get => result;
        set { result = value; Close(); }
    }
    public event CancelEventHandler? Closing;
    public event EventHandler? Closed;

    public void Show()
    {
        if (popup.IsOpen) { Activate(); return; }
        var owner = Owner ?? Application.Current?.MainWindow;
        Attach(owner);
        restoreFocus = Keyboard.FocusedElement;
        open.Add(this);
        Fit();
        popup.IsOpen = true;
        if (SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }
        // Content usually focuses its first field when loaded; otherwise focus moves to the first control.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, Activate);
    }
    public bool? ShowDialog()
    {
        result = null;
        Show();
        var waiting = frame = new DispatcherFrame();
        Dispatcher.PushFrame(waiting);
        if (frame == waiting) frame = null;
        return result;
    }
    public void Activate()
    {
        if (!popup.IsOpen || layer.IsKeyboardFocusWithin) return;
        if (!MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) layer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    public void Close()
    {
        if (!popup.IsOpen) return;
        var closing = new CancelEventArgs();
        Closing?.Invoke(this, closing);
        if (closing.Cancel) return;
        Hide();
        Closed?.Invoke(this, EventArgs.Empty);
    }
    // Removes the dialog without closing it, so it can be shown again as it was.
    public void Hide()
    {
        if (!popup.IsOpen) return;
        popup.IsOpen = false;
        open.Remove(this);
        Detach();
        if (frame is not null) { frame.Continue = false; frame = null; }
        if (restoreFocus is UIElement { IsVisible: true } previous) previous.Focus();
        restoreFocus = null;
    }

    private void Attach(Window? owner)
    {
        Detach();
        attached = owner;
        popup.PlacementTarget = owner?.Content as UIElement ?? owner;
        if (owner is null) return;
        owner.SizeChanged += OwnerChanged; owner.LocationChanged += OwnerChanged; owner.StateChanged += OwnerChanged;
        owner.Activated += OwnerActivated; owner.Closing += OwnerClosing;
    }
    private void Detach()
    {
        if (attached is not { } owner) return;
        owner.SizeChanged -= OwnerChanged; owner.LocationChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged;
        owner.Activated -= OwnerActivated; owner.Closing -= OwnerClosing;
        attached = null;
    }
    private void OwnerChanged(object? sender, EventArgs e) { if (popup.IsOpen) { Fit(); KeepWithOwner(); } }
    private void OwnerActivated(object? sender, EventArgs e) { if (popup.IsOpen && open.LastOrDefault() == this) { KeepWithOwner(); Activate(); } }
    private void OwnerClosing(object? sender, CancelEventArgs e) => Hide();
    // Covers the owner's content exactly and keeps the card inside it.
    private void Fit()
    {
        var target = popup.PlacementTarget as FrameworkElement;
        var width = target is { ActualWidth: > 0 } ? target.ActualWidth : attached?.Width is double w && double.IsFinite(w) ? w : 1000;
        var height = target is { ActualHeight: > 0 } ? target.ActualHeight : attached?.Height is double h && double.IsFinite(h) ? h : 700;
        popup.Width = width; popup.Height = height;
        MaxWidth = Math.Max(160, width - Gutter * 2 - card.Padding.Left - card.Padding.Right - 2);
        MaxHeight = Math.Max(120, height - Gutter * 2 - card.Padding.Top - card.Padding.Bottom - HeaderHeight);
        // Changing an offset makes the popup re-read its position after the window moves.
        popup.HorizontalOffset = popup.HorizontalOffset == 0 ? 0.01 : 0;
    }
    private void KeepWithOwner()
    {
        if (PresentationSource.FromVisual(layer) is not HwndSource source) return;
        StayWithOwner(source.Handle, attached);
    }
    internal static void StayWithOwner(IntPtr handle, Window? owner)
    {
        if (owner is not null && new WindowInteropHelper(owner).Handle is var ownerHandle && ownerHandle != IntPtr.Zero)
            SetWindowLongPtr(handle, -8, ownerHandle); // GWLP_HWNDPARENT sets the owner of a top-level window.
        SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_NOTOPMOST; no move, size or activation.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOP: above the owner's other windows.
    }

    // A short notice in the corner of the owner window. When the app is in the background its taskbar button flashes.
    // Returns an action that dismisses the notice early.
    public static Action Toast(Window owner, string text, TimeSpan duration)
    {
        var message = new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var toastCard = new Border { Width = 340, Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = message, Margin = new Thickness(16) };
        toastCard.SetResourceReference(Border.BackgroundProperty, "Surface");
        toastCard.SetResourceReference(Border.BorderBrushProperty, "Accent");
        toastCard.SetResourceReference(EffectProperty, "PopupShadow");
        TextElement.SetFontFamily(toastCard, new FontFamily("Segoe UI"));
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Assertive);
        var target = owner.Content as FrameworkElement;
        var toast = new Popup { Child = toastCard, AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = target ?? (UIElement)owner, StaysOpen = true, IsHitTestVisible = false };
        void Place()
        {
            toastCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            toast.HorizontalOffset = Math.Max(0, (target?.ActualWidth ?? owner.ActualWidth) - toastCard.DesiredSize.Width);
            toast.VerticalOffset = Math.Max(0, (target?.ActualHeight ?? owner.ActualHeight) - toastCard.DesiredSize.Height - 28);
        }
        void Moved(object? sender, EventArgs e) => Place();
        var timer = new DispatcherTimer { Interval = duration };
        void Dismiss() { timer.Stop(); toast.IsOpen = false; owner.SizeChanged -= Moved; owner.LocationChanged -= Moved; }
        timer.Tick += (_, _) => Dismiss();
        toast.Opened += (_, _) => { if (PresentationSource.FromVisual(toastCard) is HwndSource source) StayWithOwner(source.Handle, owner); };
        owner.SizeChanged += Moved; owner.LocationChanged += Moved;
        Place(); toast.IsOpen = true; timer.Start();
        if (!owner.IsActive && new WindowInteropHelper(owner).Handle is var handle && handle != IntPtr.Zero)
        {
            var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = handle, Flags = 0x3 | 0xC, Count = 0, Timeout = 0 }; // all, until foreground
            FlashWindowEx(ref flash);
        }
        return Dismiss;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo { public uint Size; public IntPtr Window; public uint Flags; public uint Count; public uint Timeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
