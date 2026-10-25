using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private bool nextEpisodePromptOpen;
    private MediaItem? episodeActionItem, episodeActionNext;
    private List<MediaItem>? episodeActionLibrary;
    private readonly Dictionary<string, bool> watchedState = [];
    private bool WasWatched(string id)
    {
        if (!watchedState.TryGetValue(id, out var watched)) watchedState[id] = watched = library.Setting("watched:" + id) == "true";
        return watched;
    }
    private void SetWatched(MediaItem item, bool watched)
    {
        library.Setting("watched:" + item.Id, watched ? "true" : "false"); watchedState[item.Id] = watched;
        RefreshHome();
        foreach (var card in libraryCards.Where(card => card.Card.Media.Id == item.Id || LibraryIdentity.SameShow(card.Card.Media, item)))
            UpdateCardPlayback(card);
    }
    private bool HasPlaybackQueue => queue.Count > 0 || roomLibrary?.Queue.Entries.Length > 0 || room?.Snapshot?.Queue?.Length > 0;
    // A loaded queue continues only the video it started; other videos offer their next episode.
    private bool PlayingFromQueue(MediaItem? item) => item is not null && queuePosition >= 0 && queuePosition < queue.Count && queue[queuePosition].Id == item.Id;
    // Guests' up-next requests and a queue that started this video take priority;
    // at the end of that queue an episode still offers its next episode.
    private bool ContinuesQueue(MediaItem? ended) => roomLibrary?.Queue.Entries.Length > 0 ||
        PlayingFromQueue(ended) && QueuePlayback.FindNext(queue, queuePosition, 1, QueueItemAvailable) >= 0;
    private long episodeActionCheckedAt;
    // Library updates replace records; keep the playing video's current metadata.
    private void RefreshPlayingItem()
    {
        // A video opened from outside the library keeps playing its own file under the record it was recognised as.
        if (playingItem is { } playing && items.FirstOrDefault(item => item.Id == playing.Id) is { } current)
            playingItem = current.Path == playing.Path ? current : current with { Path = playing.Path, Available = true, IsVirtual = false };
    }

    private void RefreshEpisodeEndAction()
    {
        if (EpisodeEndButton is null) return;
        var item = playingItem;
        var visible = item is { IsExtra: false, IsVirtual: false };
        EpisodeEndButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        EpisodeEndButton.IsEnabled = player is not null && ready && !nextEpisodePromptOpen &&
            (room is null || room.IsConnected && room.Identity?.Host == true);
        // Recheck periodically so a drive that comes back reveals the next episode.
        if (episodeActionItem != item || episodeActionLibrary != items || Environment.TickCount64 - episodeActionCheckedAt > 5000)
        {
            episodeActionItem = item; episodeActionLibrary = items; episodeActionCheckedAt = Environment.TickCount64;
            episodeActionNext = EpisodePlayback.Next(items, item!, false, next => RecentlyExists(next.Path));
        }
        EpisodeEndButton.Content = episodeActionNext is null ? "Finish ✓" : "Next episode →";
        EpisodeEndButton.ToolTip = episodeActionNext is null ? item!.Series is null ? "Mark this movie as watched" : "Mark this episode as watched" :
            $"Season {episodeActionNext.Season} · {episodeActionNext.EpisodeDisplayTitle}";
        System.Windows.Automation.AutomationProperties.SetName(EpisodeEndButton, episodeActionNext is null ? item!.Series is null ? "Finish movie" : "Finish episode" : "Next episode");
    }
    private async void EpisodeEndClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await Guard(async () =>
        {
            RefreshEpisodeEndAction();
            if (EpisodeEndButton.Visibility != Visibility.Visible || !EpisodeEndButton.IsEnabled || playingItem is not { } item) return;
            var next = EpisodePlayback.Next(items, item, false, candidate => File.Exists(candidate.Path));
            library.Setting("position:" + item.Id, "0"); SetWatched(item, true);
            if (next is null) { SetStatus(item.Series is null ? "Movie marked as watched." : "Episode marked as watched."); return; }
            await PlayFollowingEpisode(next);
        });
    }
    private async Task PlayFollowingEpisode(MediaItem next)
    {
        if (room is null) await PlayLocalItem(next);
        else
        {
            StartHostedVideo(next);
            if (!pictureInPicture) ShowPage("Room");
        }
    }
    private void ToggleCardWatched(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        // The checkmark stays visible while focused; after a mouse click, return focus to its card so it fades.
        if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is System.Windows.Input.MouseDevice && sender is FrameworkElement { Parent: Panel cardPanel })
            cardPanel.Children.OfType<Button>().FirstOrDefault(button => button != sender)?.Focus();
        if ((sender as FrameworkElement)?.DataContext is not LibraryCardView view) return;
        MarkWatched(view.Card.Media, view.Card.Level, view.Card.Season, !view.IsWatched);
    }
    private void ToggleDetailWatched(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (selected is not { } item) return;
        var watched = !WasWatched(item.Id);
        // A watched video starts from the beginning next time.
        if (watched) library.Setting("position:" + item.Id, "0");
        MarkWatched(item, "video", null, watched);
        UpdateDetailPlayback(item);
    }
    private void UpdateDetailPlayback(MediaItem item)
    {
        var watched = WasWatched(item.Id);
        DetailWatchedText.Text = watched ? "Watched" : "Mark as watched";
        DetailWatchedIcon.Text = watched ? "\uEC61" : "\uE73E";
        DetailWatchedIcon.SetResourceReference(TextBlock.ForegroundProperty, watched ? "Success" : "Ink");
        DetailWatched.ToolTip = watched ? "Mark as unwatched" : "Mark as watched";
        System.Windows.Automation.AutomationProperties.SetName(DetailWatched, watched ? "Mark as unwatched" : "Mark as watched");
        var resume = !watched && long.TryParse(library.Setting("position:" + item.Id), out var position) && position >= 10000 ? position : 0;
        DetailPlay.Content = resume > 0 ? "Resume at " + FormatTime(resume) : "Play locally";
        RestartButton.Visibility = resume > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = DetailPlay.IsEnabled;
    }
    private void MarkWatched(MediaItem item, string level, int? season, bool watched)
    {
        // Clearing a logical episode also clears watched versions that cover it.
        var affected = level is "series" or "season"
            ? items.Where(candidate => !candidate.IsExtra && !candidate.IsVirtual && LibraryIdentity.SameShow(candidate, item) &&
                (level == "series" || (candidate.Season ?? -1) == season)).ToArray()
            : !watched && item.Series is not null && item.Season is not null && item.Episode is not null && item.NumberingConflict is null
            ? items.Where(candidate => LibraryIdentity.SameShow(candidate, item) && candidate.Season == item.Season && candidate.NumberingConflict is null &&
                LibraryIdentity.EpisodeNumbers(candidate).Intersect(LibraryIdentity.EpisodeNumbers(item)).Any()).ToArray()
            : [item];
        var marked = affected.Append(item).Select(entry => entry.Id).Distinct().ToArray();
        library.SetSettings(marked.Select(id => KeyValuePair.Create("watched:" + id, watched ? "true" : "false")));
        foreach (var id in marked) watchedState[id] = watched;
        foreach (var card in libraryCards.Where(card => card.Card.Media.Id == item.Id || LibraryIdentity.SameShow(card.Card.Media, item)))
            UpdateCardPlayback(card);
        SetStatus(watched ? "Marked as watched." : "Marked as unwatched.");
        RefreshHome();
    }

    private async Task PlaybackEnded(MediaItem? endedItem, RoomClient? endedRoom, int generation)
    {
        if (closing || nextEpisodePromptOpen || generation != loadGeneration || room != endedRoom || playingItem?.Id != endedItem?.Id) return;
        PlayButton.Content = "\uF5B0";
        RefreshEpisodeEndAction();
        if (endedItem is not null && (room is null || room.Identity?.Host == true))
        {
            library.Setting("position:" + endedItem.Id, "0");
            SetWatched(endedItem, true);
        }
        if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player!.Length), stopReason: "ended");
        var upNext = roomLibrary?.Queue.Entries.Length > 0;
        if (ContinuesQueue(endedItem))
        {
            if (room is null || room.Identity?.Host == true)
            {
                if (upNext) roomLibrary!.Request(HostPeerId(), "next");
                else await PlayQueueStep(1);
            }
            return;
        }
        if (endedItem is null || room is not null && (room.Identity?.Host != true || !room.IsConnected)) return;
        var next = EpisodePlayback.Next(items, items.FirstOrDefault(item => item.Id == endedItem.Id) ?? endedItem, false, item => File.Exists(item.Path));
        if (next is null) return;
        nextEpisodePromptOpen = true;
        try
        {
            // Restarting, replacing or leaving the video, or losing room hosting, cancels the countdown.
            bool Stale() => closing || generation != loadGeneration || room != endedRoom || playingItem?.Id != endedItem.Id ||
                roomLibrary?.Queue.Entries.Length > 0 || player?.IsPlaying == true || RoomPage.Visibility != Visibility.Visible && !pictureInPicture ||
                room is not null && (room.Identity?.Host != true || !room.IsConnected);
            if (!await OfferNextEpisode(next, Stale) || Stale()) return;
            await PlayFollowingEpisode(next);
        }
        finally { nextEpisodePromptOpen = false; RefreshEpisodeEndAction(); }
    }

    private const int UpNextCountdownMs = 15000;
    private TaskCompletionSource<bool>? upNextChoice;
    private DispatcherTimer? upNextTimer;
    private bool upNextPaused;
    private bool UpNextCounting => upNextChoice is not null && !upNextPaused;
    // Shows the up-next card over the video; resolves true when the countdown ends or the user plays now.
    private Task<bool> OfferNextEpisode(MediaItem next, Func<bool> stale)
    {
        EndUpNext(false);
        UpNextSeries.Text = next.Series ?? next.Title;
        UpNextEpisode.Text = $"Season {next.Season} · {next.EpisodeDisplayTitle}";
        var choice = upNextChoice = new();
        var timer = upNextTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        upNextUpdate = () =>
        {
            if (stale()) { EndUpNext(false); return; }
            if (upNextPaused) return;
            var remaining = upNextDeadline - Environment.TickCount64;
            if (remaining <= 0) { EndUpNext(true); return; }
            UpNextCountdown.Text = $"Playing in {(remaining + 999) / 1000}s";
        };
        timer.Tick += (_, _) => upNextUpdate?.Invoke();
        upNextPaused = false;
        upNextDeadline = Environment.TickCount64 + UpNextCountdownMs;
        upNextUpdate();
        if (!choice.Task.IsCompleted) { UpNextCard.Visibility = Visibility.Visible; timer.Start(); StartUpNextCountdown(); }
        return choice.Task;
    }
    private long upNextDeadline;
    // The card and the compact picture-in-picture strip drain together.
    private void AnimateUpNextFill(System.Windows.Media.Animation.AnimationTimeline? animation)
    {
        UpNextFillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
        PictureInPictureUpNextFillScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, animation);
    }
    private Action? upNextUpdate;
    // Starts the full countdown and its time bar; Play after a pause restarts it from the beginning.
    private void StartUpNextCountdown()
    {
        upNextPaused = false;
        upNextDeadline = Environment.TickCount64 + UpNextCountdownMs;
        UpNextProgress.Visibility = Visibility.Visible;
        AnimateUpNextFill(
            new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(UpNextCountdownMs)));
        PlayButton.Content = "";
        upNextUpdate?.Invoke();
        UpdateSystemMediaControls();
    }
    private void EndUpNext(bool play)
    {
        upNextTimer?.Stop(); upNextTimer = null; upNextUpdate = null; upNextPaused = false;
        if (upNextChoice is not null) PlayButton.Content = "";
        AnimateUpNextFill(null);
        UpNextCard.Visibility = Visibility.Collapsed;
        var choice = upNextChoice; upNextChoice = null;
        choice?.TrySetResult(play);
    }
    // Pause keeps the card so the next episode is one click away, but stops the countdown and drops its time bar.
    private void PauseUpNext()
    {
        upNextPaused = true;
        AnimateUpNextFill(null);
        UpNextProgress.Visibility = Visibility.Collapsed;
        UpNextCountdown.Text = "Autoplay paused";
        PlayButton.Content = "";
        UpdateSystemMediaControls();
    }
    private void UpNextPlayClicked(object sender, RoutedEventArgs e) { e.Handled = true; EndUpNext(true); }
    private void UpNextCancelClicked(object sender, RoutedEventArgs e) { e.Handled = true; EndUpNext(false); }
    // Clicks on the card must not reach the video, where they would toggle playback.
    private void UpNextCardClicked(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;
}
