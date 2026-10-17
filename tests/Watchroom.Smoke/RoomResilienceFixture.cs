using Watchroom.Core;

static class RoomResilienceFixture
{
    public static void Run(Action<bool, string> check)
    {
        var media = new SharedMedia("movie", "Movie", 1024, ".avi");
        Participant[] people = [new("host", "Alex", true, true), new("guest", "Sam", false, true), new("late", "Kim", false, true)];
        WireMessage Ready(HostRoomCoordinator room) => new("ready", Data: Wire.Serialize(new PlaybackReadiness(media.Id, room.Playback!.Generation)));
        WireMessage Buffering(HostRoomCoordinator room) => new("buffering", Data: Wire.Serialize(new PlaybackReadiness(media.Id, room.Playback!.Generation)));
        WireMessage Play(bool playing) => new("playback", Data: Wire.Serialize(new PlaybackState(0, media.Id, playing, 0, 0)));
        var room = new HostRoomCoordinator("host"); room.Discover(people, 1000);
        room.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _);
        room.SetConnected("guest", true, 1000);
        room.Apply("host", Ready(room), 1000, out _); room.Apply("guest", Ready(room), 1000, out _);
        room.Apply("host", Play(true), 1100, out _);
        check(room.Playback is { Playing: true }, "approved guest without a direct peer connection does not hold room playback");
        room.SetConnected("late", true, 1200);
        check(room.Playback is { Playing: true }, "a guest that connects during playback does not pause it until it reports buffering");
        room.Apply("late", Buffering(room), 1300, out var notice);
        check(room.Playback is { Playing: false, WantsPlayback: true } && notice?.Type == "notice", "connected late guest buffering uses the normal pause path");
        room.Apply("late", Ready(room), 1400, out _);
        check(room.Playback is { Playing: true }, "connected late guest resumes playback once ready");
        room.Apply("guest", Buffering(room), 1500, out _);
        check(room.Playback is { Playing: false, WantsPlayback: true }, "connected guest buffering pauses the room");
        check(room.SetConnected("guest", false, 1600) && room.Playback is { Playing: true }, "losing a buffering guest's peer connection resumes the waiting room");
        room.Apply("host", Play(false), 1700, out _);
        room.SetConnected("guest", true, 1800);
        room.Apply("host", Play(true), 1900, out _);
        check(room.Playback is { Playing: false, WantsPlayback: true } && RoomPlaybackStatus.Describe(room.Snapshot)!.Contains("Sam"),
            "a reconnected guest that has not reported ready holds playback like any connected guest");
        room.Apply("guest", Ready(room), 1950, out _);
        check(room.Playback is { Playing: true }, "reconnected guest resumes playback once ready");
        room.Apply("host", new("playback", Data: Wire.Serialize(new PlaybackState(0, media.Id, true, 5000, 0, Seek: true))), 2000, out _);
        check(room.Playback is { Playing: false, WantsPlayback: true }, "seek waits for connected scoped guests");
        room.SetConnected("late", false, 2100);
        check(room.Playback is { Playing: false }, "disconnecting one guest still waits for the remaining connected guest");
        room.Apply("guest", Ready(room), 2200, out _); room.Apply("host", Ready(room), 2200, out _);
        check(room.Playback is { Playing: true, PositionMs: 5000 }, "seek resumes when every connected participant is ready");
        check(!room.SetConnected("host", false, 2300) && room.IsConnected("host"), "the host always counts as connected");

        var host = new RoomLibrary(true, "host"); var guest = new RoomLibrary(false, "guest");
        var queueUpdates = 0;
        host.Send += (_, message) => { if (message.Type == "library-queue") queueUpdates++; };
        host.SetPeople([new("host", "Host", true, true), new("guest", "Anna", false, true)]);
        host.Publish([new SharedLibraryItem("item", "Movie", "Movie", 2026, null, null, null, null, true)]);
        host.PeerReady("guest"); host.SetAccess("guest", new(true, true, true));
        int Send(int request)
        {
            var before = queueUpdates;
            host.Receive("guest", new("library-command", Data: Wire.Serialize(new LibraryCommand("request-" + request, "add", -1, "item"))));
            return queueUpdates - before;
        }
        for (var request = 0; request <= 257; request++) Send(request);
        check(Send(256) == 0 && Send(257) == 0, "newest library results stay cached after the result cache evicts");
        check(Send(1) == 1, "library result cache evicts the oldest request first");
    }
}
