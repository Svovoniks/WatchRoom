using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

// The first time a video is watched to the end, or finished with Next episode or Finish, a short promo plays once
// over the player before playback carries on as usual. It plays on its own WPF media element in the player overlay,
// so the video player is left as it was. In a room, synchronization waits until the promo ends: room media and
// playback changes are applied afterwards, so a slow or interrupted connection cannot cut the promo short.
public partial class MainWindow
{
    private const string PostplayShownSetting = "postplayShown", PostplayDisabledSetting = "postplayDisabled";
    private static string PostplayPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Promo", "teaser-clam-intro.mp4");
    private TaskCompletionSource? postplayDone;
    private DispatcherTimer? postplayWatchdog;
    private long postplayDeadline;
    private bool PostplayActive => postplayDone is not null;
    private bool SavedPostplayDisabled => library.Setting(PostplayDisabledSetting) == "true";

    // Plays the promo if it has not been shown yet; returns once it ends, or at once when it does not play.
    // pauseVideo pauses a video that is still playing, and plays it again afterwards unless something replaced it.
    private async Task PlayPostplayOnce(bool pauseVideo = false)
    {
        if (postplayDone is { } running) { await running.Task; return; }
        if (closing || SavedPostplayDisabled || library.Setting(PostplayShownSetting) == "true" || !File.Exists(PostplayPath)) return;
        // Only shown where the player is on screen.
        if (RoomPage.Visibility != Visibility.Visible && !pictureInPicture) return;
        library.Setting(PostplayShownSetting, "true");
        await PlayPostplay(pauseVideo);
    }
    // Plays the promo in the player, which must be on screen, and returns once it ends.
    private async Task PlayPostplay(bool pauseVideo)
    {
        var done = postplayDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = loadGeneration;
        var resume = pauseVideo && player?.IsPlaying == true;
        if (resume) player!.SetPause(true);
        PlaybackDiagnostics.Record("postplay-start", new { room = room is not null, resume });
        ClosePlayerMenusNow();
        if (fullscreen || pictureInPicture) PlayerOverlay.Cursor = Cursors.None;
        PostplayVideo.Source = new Uri(PostplayPath);
        ApplyPlayerVolume();
        PostplayLayer.Visibility = Visibility.Visible;
        PostplayVideo.Play();
        // MediaEnded normally finishes the promo; this ends it if the file never opens or stalls.
        postplayDeadline = Environment.TickCount64 + 15000;
        postplayWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        postplayWatchdog.Tick += (_, _) => { if (Environment.TickCount64 > postplayDeadline) EndPostplay("timeout"); };
        postplayWatchdog.Start();
        await done.Task;
        if (closing) return;
        if (resume && generation == loadGeneration && room is null) player?.SetPause(false);
        await ResumeRoomSync();
    }
    // Settings plays the promo on request, whether or not it was shown or is turned off, then returns to Settings.
    // Picture in picture plays it in the small window instead. A playing video pauses meanwhile.
    private async void PlayPostplayClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await Guard(async () =>
        {
            if (PostplayActive || closing) return;
            if (!File.Exists(PostplayPath)) { PreferencesStatus.Text = "The promo video is missing from this installation."; return; }
            var returnToSettings = !pictureInPicture;
            if (returnToSettings)
            {
                ShowPage("Room");
                // Navigation can be declined, for example to keep unsaved settings.
                if (RoomPage.Visibility != Visibility.Visible) return;
                await Dispatcher.InvokeAsync(() => Video.UpdateLayout(), DispatcherPriority.Render);
            }
            await PlayPostplay(pauseVideo: true);
            if (returnToSettings && !closing && currentPage == "Room") ShowPage("Settings");
        });
    }
    private void EndPostplay(string reason)
    {
        if (postplayDone is not { } done) return;
        postplayDone = null;
        postplayWatchdog?.Stop(); postplayWatchdog = null;
        PostplayVideo.Stop(); PostplayVideo.Source = null;
        PostplayLayer.Visibility = Visibility.Collapsed;
        PlayerOverlay.Cursor = Cursors.Arrow;
        PlaybackDiagnostics.Record("postplay-end", new { reason });
        done.TrySetResult();
    }
    // Room changes that arrived during the promo are applied now, as on reconnecting.
    private async Task ResumeRoomSync()
    {
        if (room is null || PostplayActive) return;
        appliedRevision = readyGeneration = -1;
        if (room.IsConnected && room.Snapshot?.Media is { } media && media.Id != loadedMedia) await LoadRoomMedia(media);
        ApplyRoomPlayback(); ReportReady();
        UpdateSessionControls();
    }
    private void PostplayOpened(object sender, RoutedEventArgs e)
    {
        var length = PostplayVideo.NaturalDuration.HasTimeSpan ? PostplayVideo.NaturalDuration.TimeSpan : TimeSpan.FromMinutes(1);
        postplayDeadline = Environment.TickCount64 + (long)length.TotalMilliseconds + 5000;
    }
    private void PostplayEnded(object sender, RoutedEventArgs e) => EndPostplay("ended");
    private void PostplayFailed(object sender, ExceptionRoutedEventArgs e) => EndPostplay("failed: " + e.ErrorException?.GetType().Name);
    // Presses and wheel turns on the promo must not reach the video, where they would control playback.
    private void PostplayInput(object sender, InputEventArgs e) => e.Handled = true;
}
