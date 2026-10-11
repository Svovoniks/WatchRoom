using Watchroom.Core;

static class RoomPlaybackStatusFixture
{
    public static void Run(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        Participant[] people = [new("host", "Alex", true, true), new("guest", "Sam", false, true), new("pending", "Pending", false, false)];
        var media = new SharedMedia("movie", "Movie", 1024, ".avi");
        room.Discover(people, 1000);
        room.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _);
        string? Status() => RoomPlaybackStatus.Describe(Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot)));
        void Command(string sender, bool playing, bool seek = false, PlaybackStop? stop = null) => room.Apply(sender,
            new("playback", Sender: "host", Data: Wire.Serialize(new PlaybackState(0, media.Id, playing, 0, 0, Seek: seek, Stop: stop))), 2000, out _);
        check(Status() == "Loading video…", "new media explains loading instead of blaming the host for pausing");
        room.Apply("host", new("ready"), 1100, out _);
        room.Apply("host", new("controls", Number: 1), 1100, out _);
        Command("host", true);
        check(Status() == "Waiting for Sam to buffer · Playback resumes automatically", "initial play identifies only approved participants who are not ready");
        room.Apply("guest", new("ready"), 1200, out _);
        check(Status() is null && room.Playback?.Stop is null, "automatic resume clears stop explanation and attribution");
        Command("guest", false, stop: new("paused", "host", "Fake Host"));
        check(Status() == "Paused by Sam" && room.Playback?.Stop?.Peer == "guest", "pause attribution uses authenticated peer despite spoofed sender and name");
        room.Apply("host", new("buffering"), 1300, out _);
        check(Status() == "Paused by Sam", "buffering during a manual pause does not hide who paused");
        room.Apply("host", new("ready"), 1400, out _);
        Command("host", false, seek: true);
        check(Status() == "Paused by Sam", "seeking while paused preserves original attribution");
        Command("host", false, seek: true, stop: new("stopped", "guest", "Fake Guest"));
        check(Status() == "Stopped by Alex" && room.Playback is { PositionMs: 0, Stop.Peer: "host" }, "explicit Stop identifies the actor and resets the timeline");
        Command("host", true);
        room.Apply("guest", new("buffering"), 1500, out _);
        check(Status() == "Waiting for Sam to buffer · Playback resumes automatically", "buffering during playback names the stalled participant");
        room.Apply("host", new("buffering"), 1600, out _);
        check(Status() == "Waiting for Alex, Sam to buffer · Playback resumes automatically", "multiple stalled participants remain visible without a new playback revision");
        room.Apply("guest", new("ready"), 1700, out _);
        check(Status() == "Waiting for Alex to buffer · Playback resumes automatically", "recovered participant disappears from the waiting list");
        room.Apply("host", new("ready"), 1800, out _);
        check(Status() is null, "all participants ready removes the buffering message");
        Command("guest", false, stop: new("ended"));
        check(Status() == "Paused by Sam", "guest cannot mislabel a manual pause as the video ending");
        Command("host", false, stop: new("ended"));
        check(Status() == "Video ended", "natural end explains the video ending instead of attributing a pause to the host");
        Command("guest", false);
        room.Depart("guest", 1900);
        check(Status() == "Paused by Sam", "actor name survives departure and wire snapshots");
        check(RoomPlaybackStatus.Describe(room.Snapshot, reconnecting: true) == "Connection interrupted · Reconnecting to host…", "reconnecting explains the local playback interruption");
        var legacy = room.Snapshot with { Playback = Wire.Read<PlaybackState>("{\"revision\":1,\"mediaId\":\"movie\",\"playing\":false,\"positionMs\":0,\"atUnixMs\":0}") };
        check(RoomPlaybackStatus.Describe(legacy) == "Playback paused", "older host snapshots show a safe pause fallback without invented attribution");
        check(RoomPlaybackStatus.Describe(null) is null && RoomPlaybackStatus.Describe(room.Snapshot with { Media = null }) is null,
            "local playback and rooms without media do not show stale stop messages");
    }
}
