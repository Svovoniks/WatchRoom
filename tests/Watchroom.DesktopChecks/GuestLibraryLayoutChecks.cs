using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Core;
using Watchroom.Desktop;

static class GuestLibraryLayoutChecks
{

    public static void Run()
    {
        // Render detached WPF content. No desktop windows are opened or personal data read.

        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var host = new RoomLibrary(true, "host"); var guest = new RoomLibrary(false, "guest");
        Participant[] people = [new("host", "Fixture Host", true, true), new("guest", "Fixture Guest", false, true)];
        var traffic = new Queue<Action>();
        host.Send += (_, message) => traffic.Enqueue(() => guest.Receive("host", message));
        guest.Send += (_, message) => traffic.Enqueue(() => host.Receive("guest", message));
        void Pump() { while (traffic.TryDequeue(out var action)) action(); }
        host.SetPeople(people);
        host.Publish(Enumerable.Range(1, 65).Select(i => new SharedLibraryItem("item-" + i, "Fixture movie " + i, "Movie", 2026, null, null, null, "A generated library fixture.", true)));
        host.SetAccess("guest", new(true, true, true)); Pump();
        var guestWindow = new RoomLibraryWindow(guest, false, () => "host", () => people, _ => { });
        var hostWindow = new RoomLibraryWindow(host, true, () => "host", () => people, _ => { });
        void Layout(Window window, int width, int height)
        {
            var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        }
        Layout(guestWindow, 1300, 1220); Invoke(guestWindow, "RenderBrowser"); Layout(guestWindow, 1300, 1220); Invoke(guestWindow, "RenderBrowser"); Layout(guestWindow, 1300, 1220);
        var grid = Field<UniformGrid>(guestWindow, "grid");
        Check(grid.Columns == 8 && grid.Rows == 4 && grid.Children.Count == 32, "guest grid automatically sizes to 8 × 4");
        grid.Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var hovered = grid.Children.OfType<Button>().ElementAt(20); hovered.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Invoke(guestWindow, "SendView", true); Pump();
        var view = host.Views["guest"];
        Check(view.VisibleIds.Length == 32 && view.HoveredId == (string)hovered.Tag, "actual guest hover event is sent with every visible ID");
        var hostTabs = Field<TabControl>(hostWindow, "tabs"); hostTabs.SelectedIndex = 2;
        Layout(hostWindow, 1000, 760); Invoke(hostWindow, "RenderPreviews"); Layout(hostWindow, 1000, 760);
        var previews = Field<StackPanel>(hostWindow, "previews"); var box = previews.Children.OfType<Viewbox>().Single(); var mirror = (UniformGrid)box.Child;
        Check(mirror.Columns == 8 && mirror.Rows == 4 && mirror.Children.OfType<Border>().Count(c => c.Child is not null) == 32, "host mirror renders all 32 cards in the same grid");
        Check(((Border)mirror.Children[20]).BorderBrush == Brushes.Gold, "hovered guest card is highlighted gold in host mirror");
        Check(view.SelectedId == "item-1" && ((Border)mirror.Children[0]).BorderBrush == Brushes.DeepSkyBlue, "guest selection is shown as a blue card in host mirror");
        var renderedLast = ((FrameworkElement)mirror.Children[31]).TransformToAncestor(box).TransformBounds(new Rect(new Point(), ((FrameworkElement)mirror.Children[31]).RenderSize));
        Check(renderedLast.Bottom <= box.ActualHeight + 1 && renderedLast.Right <= box.ActualWidth + 1, "last preview card fits inside scaled host view without clipping");
        var scroll = Field<ScrollViewer>(hostWindow, "previewScroll");
        Check(previews.ActualHeight <= scroll.ViewportHeight + 1, "complete guest page fits host viewport without scrolling through cards");
        Save(guestWindow, "guest-grid.png"); Save(hostWindow, "host-preview.png");
        hostTabs.SelectedIndex = 0; hostWindow.ShowAccess();
        Check(hostTabs.SelectedIndex == 2, "host library entry opens guest access settings directly");
        var nextPage = Descendants((DependencyObject)guestWindow.Content).OfType<Button>().Single(b => Equals(b.Content, "Next page"));
        nextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Invoke(guestWindow, "RenderBrowser");
        Invoke(guestWindow, "SendView", true); Pump();
        Check(host.Views["guest"].Page == 1 && host.Views["guest"].VisibleIds[0] == "item-" + (grid.Columns * grid.Rows + 1), "paging updates the guest grid and exact host mirror IDs");
        hovered.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Invoke(guestWindow, "SendView", true); Pump();
        Check(host.Views["guest"].HoveredId is null, "leaving a guest card clears host hover state");
        Layout(guestWindow, 700, 540); Invoke(guestWindow, "RenderBrowser"); Layout(guestWindow, 700, 540); Invoke(guestWindow, "RenderBrowser"); Invoke(guestWindow, "SendView", true); Pump();
        Check(host.Views["guest"].Columns == grid.Columns && host.Views["guest"].Rows == grid.Rows && host.Views["guest"].VisibleIds.Length == grid.Children.OfType<Button>().Count(), "resized guest grid sends its new exact page to host");
        host.SetAccess("guest", new()); Pump(); Invoke(guestWindow, "RenderBrowser");
        Check(grid.Children.OfType<Button>().Count() == 0, "revoked catalog disappears from guest grid");
        guestWindow.Shutdown(); hostWindow.Shutdown();
        Console.WriteLine("12 desktop layout checks passed.");
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    static void Save(Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/library-ui"); using var output = File.Create(Path.Combine("artifacts/library-ui", name)); encoder.Save(output);
    }
}
