using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

// Picture in picture moves the playing video into its own small always-on-top window while the
// main window returns to the app, and moves it back to the full player without restarting playback.
static class PictureInPictureChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-pip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new[] { "Small S01E01.avi", "Small S01E02.avi" }.Select(name => Path.Combine(directory, name)).ToArray();
        foreach (var path in paths) VideoFixture.Write(path, 20);
        var item = new MediaItem("picture-in-picture-1", paths[0], "Small", "Show", Series: "Small", Season: 1, Episode: 1);
        var second = new MediaItem("picture-in-picture-2", paths[1], "Small", "Show", Series: "Small", Season: 1, Episode: 2);
        var originalItems = Field<List<MediaItem>>("items");
        typeof(MainWindow).GetField("items", flags)!.SetValue(window, originalItems.Concat([item, second]).ToList());
        var frame = (FrameworkElement)window.FindName("VideoFrame");
        var roomPage = (UIElement)window.FindName("RoomPage");
        var mainHandle = new WindowInteropHelper(window).Handle;
        async Task<bool> Advances()
        {
            var player = Field<MediaPlayer>("player"); var start = player.Time;
            for (var i = 0; i < 40; i++) { await Task.Delay(100); if (player.IsPlaying && player.Time > start + 300) return true; }
            return false;
        }
        try
        {
            Call("ShowPage", "Library");
            await (Task)Call("PlayLocalItem", item)!;
            check(await Advances(), "the fixture video plays in the full player");

            Call("SetPictureInPicture", true, true);
            var small = Field<Window>("pictureInPictureWindow");
            var smallHandle = new WindowInteropHelper(small).Handle;
            await Task.Delay(300);
            check(small.IsVisible && small.Topmost && Window.GetWindow(frame) == small,
                "picture in picture shows the video in its own always-on-top window");
            check(roomPage.Visibility != Visibility.Visible && Field<string>("currentPage") == "Library",
                "the main window returns to the page the player was opened from");
            check(HasVideoOutput(smallHandle) && !HasVideoOutput(mainHandle), "the native video window moves into the small window");
            var overlay = Window.GetWindow((UIElement)window.FindName("PlayerOverlay"));
            var scale = PresentationSource.FromVisual(small)!.CompositionTarget!.TransformToDevice;
            var centre = scale.Transform(new Point(small.Left + small.Width / 2, small.Top + small.Height / 2));
            check(overlay is not null && GetAncestor(WindowFromPoint(new NativePoint { X = (int)centre.X, Y = (int)centre.Y }), 2) == new WindowInteropHelper(overlay).Handle &&
                Above(new WindowInteropHelper(overlay).Handle, smallHandle),
                "presses on the small window reach the player overlay, which drags and resizes it");
            check(await Advances(), "playback continues in picture in picture");
            check(small.Width < 700 && small.Height < 500 && small.Width > small.Height, "the small window is small and keeps the video's shape");

            // When an episode ends, the next one is offered and plays in the small window.
            var card = (FrameworkElement)window.FindName("UpNextCard");
            var playing = Field<MediaPlayer>("player"); playing.SetPause(true);
            for (var i = 0; i < 30 && playing.IsPlaying; i++) await Task.Delay(100);
            var ended = (Task)Call("PlaybackEnded", item, null, Field<int>("loadGeneration"))!;
            var strip = (FrameworkElement)window.FindName("PictureInPictureUpNext");
            for (var i = 0; i < 30 && !(strip.IsVisible && strip.ActualHeight > 0); i++) await Task.Delay(100);
            await Task.Delay(300);
            check(!ended.IsCompleted && strip.IsVisible && !card.IsVisible && strip.ActualHeight < 70 && !((UIElement)window.FindName("PictureInPicturePlayButton")).IsVisible,
                "the up-next countdown runs and shows as a compact strip in picture in picture");
            var stripFill = (System.Windows.Media.ScaleTransform)((System.Windows.Controls.Border)((System.Windows.Controls.Border)((System.Windows.Controls.Grid)((System.Windows.Controls.Border)strip).Child).Children[3]).Child).RenderTransform;
            check(stripFill.ScaleX is > 0.2 and < 0.999, "the strip's countdown bar drains with the card's");
            Call("UpNextPlayClicked", window, new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await ended;
            check(Field<MediaItem>("playingItem").Id == second.Id && small.IsVisible && Field<bool>("pictureInPicture") && Window.GetWindow(frame) == small && await Advances(),
                "the next episode plays in picture in picture");
            check(!strip.IsVisible, "the up-next strip closes when the next episode starts");

            Call("ShowPage", "Home");
            check(small.IsVisible && Field<bool>("pictureInPicture") && Field<string>("currentPage") == "Home",
                "browsing the app keeps picture in picture open");

            Call("SetPictureInPicture", false, true);
            await Task.Delay(300);
            check(!small.IsVisible && Window.GetWindow(frame) == window && roomPage.Visibility == Visibility.Visible && Field<string>("currentPage") == "Room",
                "leaving picture in picture returns the video to the full player");
            check(HasVideoOutput(mainHandle) && !HasVideoOutput(smallHandle), "the native video window returns to the main window");
            check(Window.GetWindow((UIElement)window.FindName("PlayerOverlay"))?.Topmost == false, "the full player's overlay no longer stays above other apps");
            check(await Advances(), "playback continues after leaving picture in picture");

            Call("SetPictureInPicture", true, true);
            Call("ShowPage", "Room");
            await Task.Delay(300);
            check(!small.IsVisible && !Field<bool>("pictureInPicture") && Window.GetWindow(frame) == window && roomPage.Visibility == Visibility.Visible,
                "opening the player page closes picture in picture");

            Call("SetPictureInPicture", true, true);
            await (Task)Call("Disconnect", false, false)!;
            check(!small.IsVisible && !Field<bool>("pictureInPicture") && Window.GetWindow(frame) == window && roomPage.Visibility != Visibility.Visible,
                "stopping playback closes picture in picture without opening the player");
        }
        finally
        {
            if (Field<bool>("pictureInPicture")) Call("SetPictureInPicture", false, false);
            await (Task)Call("Disconnect", false, false)!;
            typeof(MainWindow).GetField("items", flags)!.SetValue(window, originalItems);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private static bool Above(IntPtr window, IntPtr other)
    {
        for (var next = GetWindow(window, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) if (next == other) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    private static bool HasVideoOutput(IntPtr parent)
    {
        var found = false; var name = new StringBuilder(64);
        EnumChildWindows(parent, (child, _) =>
        {
            name.Clear(); GetClassName(child, name, name.Capacity);
            found |= name.ToString().StartsWith("VLC video", StringComparison.Ordinal);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
}
