using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Core;
using Watchroom.Desktop;

static class RoomPlaybackStatusChecks
{
    public static void Run(bool screenshots = false)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var window = new MainWindow();
        var client = new RoomClient();
        var roomField = typeof(MainWindow).GetField("room", flags)!;
        var update = typeof(MainWindow).GetMethod("UpdateRoomPlaybackStatus", flags)!;
        var notice = (Border)window.FindName("RoomPlaybackNotice");
        var text = (TextBlock)window.FindName("RoomPlaybackNoticeText");
        var subtitle = (TextBlock)window.FindName("RoomSubtitle");
        var controls = (Border)window.FindName("PlaybackControls");
        var empty = (Border)window.FindName("PlayerEmpty");
        var media = new SharedMedia("movie", "Movie", 1024, ".avi");
        Participant[] people = [new("host", "Alex", true, true, true), new("guest", "Sam", false, true, true)];
        var state = new PlaybackState(1, media.Id, false, 3000, 0, RequestedPlaying: false, Stop: new("paused", "guest", "Sam"));
        var snapshot = new RoomSnapshot(people, media, state, true);
        void Apply(RoomSnapshot value)
        {
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(client, value);
            update.Invoke(window, null);
        }
        void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        try
        {
            roomField.SetValue(window, client);
            empty.Visibility = Visibility.Collapsed;
            Apply(snapshot);
            Check(notice.Visibility == Visibility.Visible && text.Text == "Paused by Sam" && subtitle.Text == text.Text,
                "room pause appears in both player overlay and header");
            if (screenshots)
            {
                typeof(MainWindow).GetMethod("ShowPage", flags)!.Invoke(window, ["Room"]);
                ((TextBlock)window.FindName("RoomHeading")).Text = "Movie night";
                ((TextBlock)window.FindName("PlaybackTitleLabel")).Text = "Sample movie";
                ((TextBlock)window.FindName("PlaybackTime")).Text = "12:34 / 01:42:00";
                ((TextBlock)window.FindName("ConnectionStatus")).Text = "Connected to room";
                controls.Visibility = Visibility.Visible;
                Save(window, "paused");
                Apply(snapshot with { People = people.Select(p => p with { Ready = p.IsHost }).ToArray(),
                    Playback = state with { RequestedPlaying = true, Stop = new("buffering") } });
                Save(window, "buffering");
                Apply(snapshot);
            }
            typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, true);
            controls.Visibility = Visibility.Collapsed;
            Apply(snapshot);
            Check(notice.Visibility == Visibility.Visible && controls.Visibility == Visibility.Collapsed,
                "fullscreen pause explanation stays visible without waking hidden controls");
            Apply(snapshot with { People = people.Select(p => p with { Ready = p.IsHost }).ToArray(),
                Playback = state with { RequestedPlaying = true, Stop = new("buffering") } });
            Check(text.Text.Contains("Sam to buffer") && notice.Visibility == Visibility.Visible,
                "fullscreen buffering message identifies the stalled participant");
            Apply(snapshot with { Playback = state with { Stop = new("stopped", "host", "Alex") } });
            Check(text.Text == "Stopped by Alex", "explicit Stop has its own player explanation");
            Apply(snapshot with { Playback = state with { Playing = true, RequestedPlaying = true, Stop = null } });
            Check(notice.Visibility == Visibility.Collapsed && text.Text == "" && subtitle.Text.Contains("Movie"),
                "resuming removes pause explanation and restores the media header");
            empty.Visibility = Visibility.Visible;
            Apply(snapshot);
            Check(notice.Visibility == Visibility.Collapsed, "empty player does not show a stale room pause overlay");
            empty.Visibility = Visibility.Collapsed;
            roomField.SetValue(window, null);
            update.Invoke(window, null);
            Check(notice.Visibility == Visibility.Collapsed && text.Text == "", "leaving a room clears the overlay for local playback");
        }
        finally
        {
            roomField.SetValue(window, null);
            typeof(MainWindow).GetField("fullscreen", flags)!.SetValue(window, false);
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            window.Close();
        }
    }

    private static void Save(MainWindow window, string name)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var root = (FrameworkElement)window.Content;
        var size = new Size(1280, 810);
        root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 810, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        // VideoView hosts its WPF overlay in a separate native window. Render
        // that same visual at the video bounds to include it in the UI capture.
        var frame = (FrameworkElement)window.FindName("VideoFrame");
        var overlay = (FrameworkElement)window.FindName("PlayerOverlay");
        var bounds = frame.TransformToAncestor(root).TransformBounds(new Rect(new Point(), frame.RenderSize));
        overlay.Measure(bounds.Size); overlay.Arrange(new Rect(new Point(), bounds.Size)); overlay.UpdateLayout();
        var overlayBitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32);
        overlayBitmap.Render(overlay);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, new Rect(new Point(), size));
            drawing.DrawImage(overlayBitmap, bounds);
        }
        var capture = new RenderTargetBitmap(1280, 810, 96, 96, PixelFormats.Pbgra32); capture.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
        var directory = Path.GetFullPath("artifacts/room-playback-preview"); Directory.CreateDirectory(directory);
        using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
        Console.WriteLine("Screenshot: " + Path.Combine(directory, name + ".png"));
    }
}
