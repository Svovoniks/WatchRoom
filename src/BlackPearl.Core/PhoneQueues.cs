namespace BlackPearl.Core;

// The PC's saved queues as BlackPearl Mobile sees them (PHONE-REMOTE.md, "Queues"): the same places, next videos and
// "Play continues here" as the PC's own Queues page and player. Positions are a video's place in the saved queue, so a
// video queued twice keeps both places.
public static class PhoneQueues
{
    public static SavedQueue[] Read(string? json)
    {
        try { return (Wire.Read<SavedQueue[]>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? []).Where(q => q?.Id is not null && q.Name is not null && q.MediaIds is not null).ToArray(); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    // The queue's videos the library still has, with their places. Ones that left the library are skipped, as the PC's
    // player skips them.
    public static (int Index, MediaItem Item)[] Entries(SavedQueue saved, IReadOnlyDictionary<string, MediaItem> byId) =>
        saved.MediaIds.Select((id, index) => (index, byId.GetValueOrDefault(id))).Where(e => e.Item2 is not null).Select(e => (e.index, e.Item2!)).ToArray();

    // The PC's player keeps only the videos the library has, so its position counts those before the place.
    public static int PlaybackIndex(SavedQueue saved, IReadOnlyDictionary<string, MediaItem> byId, int index) =>
        saved.MediaIds.Take(Math.Clamp(index, 0, saved.MediaIds.Length)).Count(byId.ContainsKey);

    // The place of the PC player's position in the saved queue, or -1.
    public static int SavedIndex(SavedQueue saved, IReadOnlyDictionary<string, MediaItem> byId, int playbackIndex)
    {
        if (playbackIndex < 0) return -1;
        for (int index = 0, resolved = 0; index < saved.MediaIds.Length; index++)
            if (byId.ContainsKey(saved.MediaIds[index]) && resolved++ == playbackIndex) return index;
        return -1;
    }

    // Where Play continues, as on the PC's Queues page: from the queue's place when it is the PC's active one, otherwise
    // its first unwatched video. -1 when everything is watched or missing.
    public static int UpNext(SavedQueue saved, IReadOnlyDictionary<string, MediaItem> byId, int activePlace, Func<string, bool> watched)
    {
        bool Ready(int index) => byId.TryGetValue(saved.MediaIds[index], out var item) && item.Available && !item.IsVirtual && !watched(item.Id);
        for (var index = Math.Max(activePlace, 0); activePlace >= 0 && index < saved.MediaIds.Length; index++) if (Ready(index)) return index;
        for (var index = 0; index < saved.MediaIds.Length; index++) if (Ready(index)) return index;
        return -1;
    }

    // From the video at [place]: the nearest playable places before and after it, and the first playable place, where
    // Loop all starts the queue again, only when nothing comes after it and it isn't this place.
    public static (int? Previous, int? Next, int? First) Steps(SavedQueue saved, IReadOnlyDictionary<string, MediaItem> byId, int place, Func<MediaItem, bool> plays)
    {
        bool Plays(int index) => byId.TryGetValue(saved.MediaIds[index], out var item) && item.Available && !item.IsVirtual && plays(item);
        int? previous = null, next = null, first = null;
        for (var index = place - 1; index >= 0 && previous is null; index--) if (Plays(index)) previous = index;
        for (var index = place + 1; index < saved.MediaIds.Length && next is null; index++) if (Plays(index)) next = index;
        if (next is null) for (var index = 0; index < saved.MediaIds.Length && first is null; index++) if (index != place && Plays(index)) first = index;
        return (previous, next, first);
    }

    // A place given by a phone, if it still holds the video it named.
    public static bool Holds(SavedQueue saved, int place, string? mediaId) => place >= 0 && place < saved.MediaIds.Length && saved.MediaIds[place] == mediaId;
}
