using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;
using Windows.Media;
using Windows.Media.Control;

static class MediaControlsChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var player = Field<MediaPlayer>("player");
        var controls = Field<SystemMediaTransportControls>("systemMediaControls");
        var empty = (Border)window.FindName("PlayerEmpty");
        var search = (TextBox)window.FindName("SearchBox");
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        async Task WaitFor(Func<bool> condition)
        {
            var until = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < until) await Task.Delay(20);
            check(condition(), "media command reaches the native playback state");
            Call("UpdateSystemMediaControls");
        }
        IntPtr Command(int command, int deviceFlags = 0) => SendMessage(handle, 0x0319, handle, new IntPtr((command | deviceFlags) << 16));
        try
        {
            Call("UpdateSystemMediaControls");
            check(controls.IsEnabled && controls.IsPlayEnabled && controls.IsPauseEnabled &&
                controls.PlaybackStatus == MediaPlaybackStatus.Playing && controls.DisplayUpdater.VideoProperties.Title == Field<MediaItem>("playingItem").Title,
                "Windows media session exposes the current video and playback controls");
            check(Command(47, 0x8000) == new IntPtr(1), "headset/OEM pause command is consumed once");
            await WaitFor(() => player.State == VLCState.Paused);
            check(controls.PlaybackStatus == MediaPlaybackStatus.Paused, "Windows media session follows native pause");
            Command(47); await Task.Delay(100);
            check(!player.IsPlaying, "repeated explicit pause never resumes playback");
            Command(46); await WaitFor(() => player.IsPlaying);
            Command(46); await Task.Delay(100);
            check(player.IsPlaying, "repeated explicit play never pauses playback");

            Call("ShowPage", "Library"); search.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.MediaPlayPause)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(key);
            await WaitFor(() => player.State == VLCState.Paused);
            check(key.Handled, "media play/pause works while browsing with focus in a text field");
            check(Command(14, 0x1000) == new IntPtr(1), "keyboard play/pause app command is consumed");
            await WaitFor(() => player.IsPlaying);

            window.WindowState = WindowState.Minimized;
            if (Environment.GetCommandLineArgs().Contains("--system-media-controls"))
            {
                // Opt in on an interactive desktop; hosted CI runners may have
                // no shell media service. Control only this test executable.
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                GlobalSystemMediaTransportControlsSession? session = null;
                var until = Environment.TickCount64 + 5000;
                while (session is null && Environment.TickCount64 < until)
                {
                    session = manager.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId.Contains("Watchroom.DesktopChecks", StringComparison.OrdinalIgnoreCase));
                    if (session is null) await Task.Delay(50);
                }
                check(session is not null, "Windows discovers the test app's background media session");
                check(await session!.TryPauseAsync(), "Windows accepts a background pause request");
                await WaitFor(() => player.State == VLCState.Paused);
                check(await session.TryPlayAsync(), "Windows accepts a background play request");
                await WaitFor(() => player.IsPlaying);
                check(await session.TryTogglePlayPauseAsync(), "Windows accepts a background play/pause button request");
            }
            else await window.Dispatcher.InvokeAsync(() => Call("HandleMediaPlayback", false));
            await WaitFor(() => player.State == VLCState.Paused);
            check(controls.IsEnabled, "minimized playback retains its Windows media session");

            await using var guest = new RoomClient();
            typeof(RoomClient).GetProperty("Identity")!.SetValue(guest, new Welcome("fixture", "guest", false, [], false));
            var media = new SharedMedia("media-control-fixture", "Room video", 1024, ".avi");
            var state = new PlaybackState(1, media.Id, false, 0, 0);
            typeof(RoomClient).GetProperty("Snapshot")!.SetValue(guest, new RoomSnapshot([], media, state, false));
            Set("room", guest); Set("target", state);
            try
            {
                Call("UpdateSystemMediaControls");
                check(!controls.IsPlayEnabled && !controls.IsPauseEnabled, "host-only guests have disabled system playback buttons");
                Command(46); await Task.Delay(100);
                check(!player.IsPlaying, "media buttons cannot bypass host-only playback permissions");
                typeof(RoomClient).GetProperty("Snapshot")!.SetValue(guest, new RoomSnapshot([], media, state, true));
                Call("UpdateSystemMediaControls");
                check(controls.IsPlayEnabled && controls.IsPauseEnabled, "shared room controls enable system playback buttons");
                Set("target", state with { RequestedPlaying = true }); Call("UpdateSystemMediaControls");
                check(controls.PlaybackStatus == MediaPlaybackStatus.Playing,
                    "temporary room buffering preserves playing intent in the system session");
            }
            finally { Set("room", null); Set("target", null); }

            empty.Visibility = Visibility.Visible;
            Call("UpdateSystemMediaControls");
            check(!controls.IsEnabled && controls.PlaybackStatus == MediaPlaybackStatus.Closed,
                "closing the video releases the Windows media session");
            check(!(bool)Call("HandleMediaPlayback", true)! && !player.IsPlaying,
                "media buttons with no active video cannot start old playback");
        }
        finally
        {
            empty.Visibility = Visibility.Collapsed;
            window.WindowState = WindowState.Normal;
            Call("ShowPage", "Room");
            Call("UpdateSystemMediaControls");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
