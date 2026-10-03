using System.IO;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private bool nextEpisodePromptOpen;
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

    private async Task PlaybackEnded(MediaItem? endedItem, RoomClient? endedRoom, int generation)
    {
        if (closing || nextEpisodePromptOpen || generation != loadGeneration || room != endedRoom || playingItem?.Id != endedItem?.Id) return;
        PlayButton.Content = "▶";
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
            if (room is null) await PlayLocalItem(next);
            else
            {
                room.SetHostedFile(next.Path, PlaybackTitle(next)); playingItem = next;
                room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); ShowPage("Room");
            }
        }
        finally { nextEpisodePromptOpen = false; }
    }
}
