using Watchroom.Core;

static class RoomSessionFixture
{
    public static void Run(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        room.Discover([new("host", "Alex", true, true), new("guest", "Sam", false, true), new("asking", "Rita", false, false)], 1000);
        room.SetConnected("guest", true, 1000);
        var stored = new RoomSession("Old movie", 60000, 7200000, false, 500, ["Sam", "Jo"], "Alex", "library-id");
        room.Apply("host", new("last-session", Data: Wire.Serialize(stored)), 1000, out _);
        var shared = Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot));
        check(shared.LastSession is { Title: "Old movie", LibraryId: null }, "the host shares its last session without its library id");
        check(!room.Apply("guest", new("last-session", Data: Wire.Serialize(stored with { Title = "Guest claim" })), 1000, out _) && room.LastSession?.Title == "Old movie",
            "guests cannot replace the room's last session");
        check(!room.Apply("host", new("last-session", Data: Wire.Serialize(stored with { Title = new string('x', 301) })), 1000, out _), "oversized sessions are rejected");

        var guestOwn = new RoomSession("Different movie", 5000, 0, false, 100, ["Alex"]);
        var adopted = RoomSessions.Observe(guestOwn, shared, "guest", false, 2000, 2000);
        check(adopted is { Title: "Old movie", PositionMs: 60000 }, "a guest adopts the host's record when nothing is playing");
        check(ReferenceEquals(RoomSessions.Observe(stored, shared, "host", true, 2000, 2000), stored), "the host keeps its own record when nothing is playing");

        var media = new SharedMedia("m1", "Old movie", 1024, ".mkv");
        room.Apply("host", new("media", Data: Wire.Serialize(media)), 3000, out _);
        var loading = RoomSessions.Observe(stored, room.Snapshot, "host", true, 3000, 3000, libraryId: "library-id");
        check(loading is { PositionMs: 60000, LibraryId: "library-id" }, "reopening a video keeps the saved position until the room seeks");
        room.Apply("host", new("playback", Data: Wire.Serialize(new PlaybackState(0, media.Id, false, 61000, 0))), 3100, out _);
        var live = RoomSessions.Observe(loading, room.Snapshot, "guest", false, 3200, 3200, durationMs: 7200000);
        check(live is { Title: "Old movie", PositionMs: 61000, DurationMs: 7200000, Host: "Alex", LibraryId: null }, "a connected guest records the host's live position");
        check(live!.People!.SequenceEqual(["Alex", "Sam", "Jo"]), "participants accumulate across the session, excluding yourself");
        var next = RoomSessions.Observe(live, room.Snapshot with { Media = new SharedMedia("m2", "New movie", 2048, ".mkv") }, "guest", false, 3300, 3300);
        check(next is { Title: "New movie", PositionMs: 0 } && next.People!.SequenceEqual(["Alex"]), "a new title starts a new session");

        var presence = RoomSessions.Presence(room.Snapshot, "host", "Alex", ["Sam", "Jo"]);
        check(presence.Text == "Waiting for Sam and Jo · Rita is asking to join" && !presence.Everyone, "presence names people still loading, absent and asking to join");
        room.Apply("guest", new("ready"), 3400, out _); room.Apply("host", new("ready"), 3400, out _);
        room.Discover([new("host", "Alex", true, true), new("guest", "Sam", false, true)], 3500);
        check(RoomSessions.Presence(room.Snapshot, "host", "Alex", ["Sam"]) is { Text: "Sam is here", Everyone: true }, "presence confirms when everyone is here");
        check(RoomSessions.Presence(room.Snapshot, "guest", "Sam", ["Alex"]).Text == "Alex is here", "guests see the host as a participant");
        check(RoomSessions.Names(["A", "B", "C", "D", "E"]) == "A, B and 3 others", "long name lists stay short");

        var bandwidth = new ArtworkBandwidth();
        check(!bandwidth.AllowsFullHd(), "full HD artwork waits for a measured transfer");
        bandwidth.Record(130000, 100);
        check(bandwidth.AllowsFullHd(), "a fast 600 px transfer allows the full HD upgrade");
        bandwidth.Slowed(); bandwidth.Slowed(); bandwidth.Slowed();
        check(!bandwidth.AllowsFullHd(), "timeouts back off from full HD upgrades");
        var slow = new ArtworkBandwidth(); slow.Record(130000, 800);
        check(!slow.AllowsFullHd(), "a slow link keeps the 600 px copy");
        check(!ArtworkTransfer.ValidChunk(new("k", 0, 26, "")) && ArtworkTransfer.ValidChunk(new("k", 0, 60, new string('a', 24000), true)) &&
            !ArtworkTransfer.ValidChunk(new("k", 0, 60, new string('a', 24000))), "legacy chunks keep their old bounds while paced chunks allow full HD");

    }
}
