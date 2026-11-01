using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using Windows.Media;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private SystemMediaTransportControls? systemMediaControls;
    private string? systemMediaTitle;
    private bool HasMediaPlayback => !closing && player is not null && PlayerEmpty.Visibility != Visibility.Visible;
    private bool CanControlMediaPlayback => HasMediaPlayback &&
        (room is null || room.Identity?.Host == true || room.Snapshot?.SharedControls == true);
    // While the up-next card shows, its countdown is the playback state: Pause stops it and Play restarts it.
    private bool WantsMediaPlayback => upNextChoice is not null ? UpNextCounting :
        room is null ? player?.IsPlaying == true : playbackIntent.Playing(target, player?.IsPlaying == true);

    private void InitializeMediaControls()
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(MediaWindowMessage);
        try
        {
            systemMediaControls = SystemMediaTransportControlsInterop.GetForWindow(new WindowInteropHelper(this).Handle);
            systemMediaControls.IsEnabled = false;
            systemMediaControls.ButtonPressed += SystemMediaButtonPressed;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            DisableSystemMediaControls(error);
        }
    }

    private void SystemMediaButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (args.Button is not (SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Pause)) return;
        var play = args.Button == SystemMediaTransportControlsButton.Play;
        // Windows delivers these events on a worker thread. Recheck the current
        // media and room permissions on the UI thread before applying them.
        Dispatcher.BeginInvoke(() => HandleMediaPlayback(play, system: true));
    }

    private long lastSystemMediaButtonAt = long.MinValue / 2;
    private bool HandleMediaPlayback(bool? play, bool system = false)
    {
        if (!HasMediaPlayback) return false;
        // One hardware key can arrive both as a window key and as a system
        // media button. A toggle right after a system button must not undo it.
        var now = Environment.TickCount64;
        if (system) lastSystemMediaButtonAt = now; else if (play is null && now - lastSystemMediaButtonAt < 400) return true;
        videoClickUndo = null;
        var requested = play ?? !WantsMediaPlayback;
        try { if (requested != WantsMediaPlayback) RequestPlayback(requested); }
        catch (IOException error) { SetStatus(error.Message); }
        UpdateSystemMediaControls();
        return true;
    }

    private bool MediaKeyPressed(KeyEventArgs e)
    {
        if (e.Key != Key.MediaPlayPause || !HasMediaPlayback) return false;
        // Holding a keyboard media key must not repeatedly toggle playback.
        if (!e.IsRepeat) HandleMediaPlayback(null);
        e.Handled = true;
        return true;
    }

    private IntPtr MediaWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (handled || message != 0x0319) return IntPtr.Zero; // WM_APPCOMMAND
        // Remove the device flags from the high word (keyboard, mouse or OEM).
        var command = (int)((lParam.ToInt64() >> 16) & 0x0fff);
        bool? play = command switch { 46 => true, 47 => false, _ => null };
        if (command is not (14 or 46 or 47) || !HandleMediaPlayback(play)) return IntPtr.Zero;
        handled = true;
        return new IntPtr(1);
    }

    private void UpdateSystemMediaControls()
    {
        if (systemMediaControls is not { } controls) return;
        try
        {
            var active = HasMediaPlayback;
            var allowed = CanControlMediaPlayback;
            var status = !active ? MediaPlaybackStatus.Closed : WantsMediaPlayback ? MediaPlaybackStatus.Playing :
                player?.State is VLCState.Ended or VLCState.Stopped or VLCState.Error ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Paused;
            if (controls.IsPlayEnabled != allowed) controls.IsPlayEnabled = allowed;
            if (controls.IsPauseEnabled != allowed) controls.IsPauseEnabled = allowed;
            if (controls.PlaybackStatus != status) controls.PlaybackStatus = status;
            var title = active ? room?.Snapshot?.Media?.Title ?? playingItem?.Title ?? "BlackPearl" : null;
            // Music shows as a song, with its artist and album, in the Windows media flyout.
            var music = active && audioPlayback;
            var key = music ? $"music\n{AudioTitle.Text}\n{AudioArtist.Text}\n{AudioAlbum.Text}" : title;
            if (systemMediaTitle != key)
            {
                var display = controls.DisplayUpdater;
                display.ClearAll();
                if (music)
                {
                    display.Type = MediaPlaybackType.Music; display.MusicProperties.Title = AudioTitle.Text;
                    display.MusicProperties.Artist = AudioArtist.Text; display.MusicProperties.AlbumTitle = AudioAlbum.Text;
                }
                else if (title is not null) { display.Type = MediaPlaybackType.Video; display.VideoProperties.Title = title; }
                display.Update();
                systemMediaTitle = key;
            }
            if (controls.IsEnabled != active) controls.IsEnabled = active;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            DisableSystemMediaControls(error);
        }
    }

    private void DisableSystemMediaControls(Exception? error = null)
    {
        if (error is not null) PlaybackDiagnostics.Record("system-media-controls-error", new { error = error.GetType().Name, error.HResult });
        var controls = systemMediaControls;
        systemMediaControls = null;
        systemMediaTitle = null;
        if (controls is null) return;
        try
        {
            controls.ButtonPressed -= SystemMediaButtonPressed;
            controls.IsEnabled = false;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
        }
        catch (Exception cleanupError) when (cleanupError is COMException or InvalidOperationException) { }
    }
}
