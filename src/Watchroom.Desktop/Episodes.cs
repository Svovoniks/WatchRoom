using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        foreach (var card in libraryCards.Where(card => card.Card.Media.Id == item.Id || LibraryIdentity.SameShow(card.Card.Media, item)))
            UpdateCardPlayback(card);
    }
    private bool HasPlaybackQueue => queue.Count > 0 || roomLibrary?.Queue.Entries.Length > 0 || room?.Snapshot?.Queue?.Length > 0;

    private void RefreshEpisodeEndAction()
    {
        if (EpisodeEndButton is null) return;
        var item = playingItem;
        var visible = item is { IsExtra: false, IsVirtual: false };
        EpisodeEndButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        EpisodeEndButton.IsEnabled = player is not null && ready && !nextEpisodePromptOpen &&
            (room is null || room.IsConnected && room.Identity?.Host == true);
        if (episodeActionItem != item || episodeActionLibrary != items)
        {
            episodeActionItem = item; episodeActionLibrary = items;
            episodeActionNext = EpisodePlayback.Next(items, item!, false, next => File.Exists(next.Path));
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
            ShowPage("Room");
        }
    }
    private void ToggleCardWatched(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not LibraryCardView view) return;
        var watched = !view.IsWatched;
        var item = view.Card.Media;
        // Clearing a logical episode also clears watched versions that cover it.
        var affected = view.Card.Level is "series" or "season"
            ? items.Where(candidate => !candidate.IsExtra && !candidate.IsVirtual && LibraryIdentity.SameShow(candidate, item) &&
                (view.Card.Level == "series" || (candidate.Season ?? -1) == view.Card.Season)).ToArray()
            : !watched && item.Series is not null && item.Season is not null && item.Episode is not null && item.NumberingConflict is null
            ? items.Where(candidate => LibraryIdentity.SameShow(candidate, item) && candidate.Season == item.Season && candidate.NumberingConflict is null &&
                LibraryIdentity.EpisodeNumbers(candidate).Intersect(LibraryIdentity.EpisodeNumbers(item)).Any()).ToArray()
            : [item];
        foreach (var entry in affected) { library.Setting("watched:" + entry.Id, watched ? "true" : "false"); watchedState[entry.Id] = watched; }
        foreach (var card in libraryCards.Where(card => card.Card.Media.Id == item.Id || LibraryIdentity.SameShow(card.Card.Media, item)))
            UpdateCardPlayback(card);
        SetStatus(watched ? "Marked as watched." : "Marked as unwatched.");
    }

    private async Task PlaybackEnded(MediaItem? endedItem, RoomClient? endedRoom, int generation)
    {
        if (closing || nextEpisodePromptOpen || generation != loadGeneration || room != endedRoom || playingItem?.Id != endedItem?.Id) return;
        PlayButton.Content = "▶";
        RefreshEpisodeEndAction();
        if (endedItem is not null && (room is null || room.Identity?.Host == true))
        {
            library.Setting("position:" + endedItem.Id, "0");
            SetWatched(endedItem, true);
        }
        if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player!.Length));
        if (HasPlaybackQueue)
        {
            if (room is null || room.Identity?.Host == true)
            {
                if (roomLibrary?.Queue.Entries.Length > 0) roomLibrary.Request(HostPeerId(), "next");
                else await PlayQueueStep(1);
            }
            return;
        }
        if (endedItem is null || room is not null && (room.Identity?.Host != true || !room.IsConnected)) return;
        var next = EpisodePlayback.Next(items, endedItem, HasPlaybackQueue, item => File.Exists(item.Path));
        if (next is null) return;
        nextEpisodePromptOpen = true;
        try
        {
            if (!Dialogs.OfferNextEpisode(this, next) || closing || generation != loadGeneration || room != endedRoom ||
                playingItem?.Id != endedItem.Id || HasPlaybackQueue) return;
            if (room is not null && (room.Identity?.Host != true || !room.IsConnected)) return;
            await PlayFollowingEpisode(next);
        }
        finally { nextEpisodePromptOpen = false; }
    }
}
