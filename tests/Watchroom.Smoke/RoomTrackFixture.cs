using Watchroom.Core;

static class RoomTrackFixture
{
    public static void Run(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        room.Discover([new("host", "Host", true, true), new("guest", "Guest", false, true)], 1000);
        var media = new SharedMedia("movie", "Movie", 1024, ".mkv");
        room.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _);
        WireMessage Tracks(RoomTracks tracks) => new("tracks", Data: Wire.Serialize(tracks));
        check(room.Apply("host", Tracks(new(media.Id, new(1), new(-1))), 1000, out _) && room.Snapshot.Tracks?.Audio?.Index == 1,
            "host publishes room audio and subtitle selections");
        check(!room.Apply("guest", Tracks(new(media.Id, new(0))), 1000, out _), "guests cannot change tracks without shared controls");
        room.Apply("host", new("controls", Number: 1), 1000, out _);
        check(room.Apply("guest", Tracks(new(media.Id, new(0))), 1000, out _) && room.Tracks?.Subtitles?.Index == -1,
            "shared guest track changes preserve the other track selection");
        var playback = room.Playback;
        var subtitle = new SharedMedia("subtitle", "Host subtitles.srt", 60, ".srt");
        var attach = new WireMessage("subtitle", Data: Wire.Serialize(new SubtitleAttachment(media.Id, subtitle)));
        check(!room.Apply("guest", attach, 1000, out _), "guests cannot inject subtitle files into host media");
        check(room.Apply("host", attach, 1000, out _) && room.Media?.Subtitles?.Single().Id == subtitle.Id && room.Tracks?.Subtitles?.AssetId == subtitle.Id,
            "host-loaded subtitles are added and selected in shared room state");
        check(room.Playback == playback, "loading subtitles preserves playback position and readiness");
        check(!room.Apply("host", Tracks(new("old-video", new(0))), 1000, out _) &&
            !room.Apply("host", Tracks(new(media.Id, Subtitles: new(0, "not-shared"))), 1000, out _) &&
            !room.Apply("host", Tracks(new(media.Id, new(999))), 1000, out _), "room rejects stale, unshared and invalid track selections");
        check(Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot)).Tracks == room.Tracks, "late-join snapshot retains shared track selections");
        room.Apply("host", new("media", Data: Wire.Serialize(media with { Id = "next" })), 1000, out _);
        check(room.Tracks is null, "new video resets previous room track selections");
    }
}
