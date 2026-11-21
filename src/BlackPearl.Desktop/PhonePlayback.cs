using System.Text.Json;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// The window's part in what BlackPearl Mobile does with the library (PhoneLibrary.cs): a phone taking over the video
// playing here, progress shown on the cards, and Continue on the PC.
public partial class MainWindow
{
    private static readonly string[] PhoneFeatures = PhoneLibrary.Features;
    private PhoneLibrary? phoneLibrary;
    private PhoneLibrary PhoneLibrary => phoneLibrary ??= new(library, () => phoneRemote, action => Dispatcher.BeginInvoke(action), lifetime.Token)
    {
        Items = () => items, WasWatched = WasWatched, SetWatched = SetWatched, Play = PlayFromPhone, TakeOver = TakeOverForPhone,
        PlaysHere = id => room is null && playingItem?.Id == id && HasMediaPlayback && WantsMediaPlayback,
        ProgressSaved = PhoneProgressSaved,
    };

    private void AttachPhonePlayback(PhoneRemoteHost remote) => PhoneLibrary.Attach(remote);
    private void ClosePhoneStreams() => phoneLibrary?.CloseStreams();

    // The phone continues the video playing here: this PC saves where it was and stops. Where the PC is now, also for a
    // title watched before, whose saved position stays 0 while it is watched again. A video still opening holds its
    // resume point until it plays.
    private async Task<long?> TakeOverForPhone(MediaItem item)
    {
        if (room is not null || playingItem?.Id != item.Id || !HasMediaPlayback || player is null) return null;
        var takenAt = Math.Max(0, player.Time <= 0 && localResume > 0 ? localResume : PlaybackPosition());
        SavePlaybackPosition();
        if (nativeStopCount == 0) { ready = false; await loading.WaitAsync(); try { await StopNativePlayback(); } finally { loading.Release(); } }
        return takenAt;
    }

    private void PhoneProgressSaved(MediaItem item, bool final)
    {
        foreach (var card in libraryCards.Where(x => x.Card.Media.Id == item.Id)) UpdateCardPlayback(card);
        // Home moves the title to the front of Continue watching once the phone stops or pauses.
        if (final && currentPage == "Home") RefreshHome();
    }

    // Continue on the PC: plays the video here from where the phone was.
    private async Task<bool> PlayFromPhone(JsonElement message, Action<string, string> fail)
    {
        if (!PhonesCanStart) { fail("not-allowed", "This PC doesn't let phones start videos. Turn it on in Settings › Profile on the PC."); return false; }
        if (room?.IsConnected == true) { fail("in-room", "The PC is in a room. Leave the room on the PC to play this there."); return false; }
        if (PhoneLibrary.Playable(message, fail) is not { } item) return false;
        PhoneLibrary.SaveHandOver(item, PhoneLibrary.Position(message));
        // From one of this PC's queues: it becomes the player's queue, as when played here, and Next goes on through it.
        if (PhoneLibrary.QueuePlace(message, item.Id) is ({ } fromQueue, var place) && savedQueues.FirstOrDefault(q => q.Id == fromQueue.Id) is { } saved)
        {
            SelectPlaybackQueue(saved);
            await PlayQueueAt(PhoneQueues.PlaybackIndex(saved, MediaById(), place));
            return true;
        }
        queuePosition = -1;
        await PlayLocalItem(item);
        PublishQueue();
        return true;
    }
}
