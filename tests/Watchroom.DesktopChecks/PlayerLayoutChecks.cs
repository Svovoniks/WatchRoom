using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Desktop;

static class PlayerLayoutChecks
{
    public static void Run()
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var window = new MainWindow();
        Invoke(window, "ShowPage", "Room");
        var options = (Border)window.FindName("PlayerOptions");
        var scroll = (ScrollViewer)window.FindName("PlayerOptionsScroll");
        var page = (Grid)window.FindName("RoomPage");
        var video = (FrameworkElement)window.FindName("Video");
        // Enough queue rows to exercise the expanded panel's worst case.
        ((ListBox)window.FindName("QueueList")).ItemsSource = Enumerable.Range(1, 40).Select(i => "Fixture video " + i).ToArray();
        var root = (FrameworkElement)window.Content;
        void Layout(int width, int height)
        {
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        }
        foreach (var size in new[] { (900, 600), (1280, 810) })
        {
            options.Visibility = Visibility.Collapsed; Layout(size.Item1, size.Item2);
            var closedHeight = video.ActualHeight;
            Invoke(window, "TogglePlayerOptions", window, new RoutedEventArgs()); Layout(size.Item1, size.Item2);
            Check(options.Visibility == Visibility.Visible && scroll.ViewportHeight > 0, "playback options open at " + size);
            Check(video.ActualHeight >= 180 && video.ActualHeight < closedHeight, "expanded options preserve a usable video area at " + size);
            Check(scroll.ScrollableHeight > 0, "long playback options scroll at " + size);
            var controls = (FrameworkElement)window.FindName("PlaybackControls");
            var bounds = controls.TransformToAncestor(page).TransformBounds(new Rect(new Point(), controls.RenderSize));
            Check(bounds.Bottom <= page.ActualHeight + 1, "transport controls stay inside the player at " + size);
            if (size.Item1 == 900) Save(root, "player-options.png");
            Invoke(window, "TogglePlayerOptions", window, new RoutedEventArgs()); Layout(size.Item1, size.Item2);
            Check(options.Visibility == Visibility.Collapsed && Math.Abs(video.ActualHeight - closedHeight) < 1, "closing options restores video size at " + size);
        }
        var library = (Button)window.FindName("RoomLibraryButton");
        Check(!IsDescendant(library, options), "library access is outside collapsed playback options");
        Check(window.FindName("SavedRoomLibraryButton") is Button, "Rooms settings provide a library access entry");
        library.Visibility = Visibility.Visible; library.Content = "Library & guest access";
        ((Button)window.FindName("RoomToggle")).Visibility = Visibility.Visible;
        ((Button)window.FindName("InviteButton")).Visibility = Visibility.Visible;
        Layout(900, 600);
        var header = (FrameworkElement)window.FindName("RoomHeader");
        foreach (var name in new[] { "RoomLibraryButton", "RoomToggle", "InviteButton", "LeaveButton" })
        {
            var button = (Button)window.FindName(name);
            var bounds = button.TransformToAncestor(header).TransformBounds(new Rect(new Point(), button.RenderSize));
            Check(bounds.Left >= 0 && bounds.Right <= header.ActualWidth + 1, name + " fits in the smallest room header");
        }
        Save(root, "player-library-access.png");
        window.Close();
        Console.WriteLine("16 player layout checks passed.");
    }
    static bool IsDescendant(DependencyObject child, DependencyObject parent)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current)) if (current == parent) return true;
        return false;
    }
    static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    static void Save(FrameworkElement root, string name)
    {
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/library-ui"); using var output = File.Create(Path.Combine("artifacts/library-ui", name)); encoder.Save(output);
    }
}
