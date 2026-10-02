using Watchroom.Core;

static class HostRoomFixture
{
    public static void Run(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        Participant[] members = [new("host", "Host", true, true), new("guest", "Guest", false, true), new("pending", "Pending", false, false)];
        room.Discover(members, 1000);
        var media = new SharedMedia("movie", "Movie", 1024, ".avi");
        check(room.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _), "host owns media state without discovery service");
        check(!room.Apply("pending", new("ready"), 1000, out _), "unapproved member cannot change direct room state");
        check(!room.Apply("guest", new("controls", Number: 1, Sender: "host"), 1000, out _), "peer cannot spoof host to enable shared controls");
        check(room.Apply("host", new("room-name", Text: "  Movie night  "), 1000, out _) && room.Snapshot.Name == "Movie night", "host publishes the canonical room name in shared state");
        check(!room.Apply("guest", new("room-name", Text: "Guest name", Sender: "host"), 1000, out _) && room.Snapshot.Name == "Movie night", "guest cannot spoof the host to rename the room");
        check(!room.Apply("host", new("room-name", Text: new string('x', 101)), 1000, out _) && !room.Apply("host", new("room-name", Text: "bad\nname"), 1000, out _), "room name rejects excessive length and control characters");
        check(Wire.Read<RoomSnapshot>(Wire.Serialize(room.Snapshot)).Name == "Movie night", "room name survives wire serialization");
        room.Apply("host", new("ready"), 1000, out _);
        var command = new PlaybackState(9999, media.Id, true, 3000, 900000, new string('a', 32));
        check(!room.Apply("guest", new("playback", Data: Wire.Serialize(command)), 1000, out _), "host-only playback denies direct guest commands");
        room.Apply("host", new("playback", Data: Wire.Serialize(command)), 1000, out _);
        check(room.Playback?.Playing == false, "host waits for all approved participants before direct play");
        room.Apply("guest", new("ready"), 1100, out _);
        check(room.Playback is { Playing: true, PositionMs: 3000, AtUnixMs: 1300 } && room.Playback.Revision < 9999, "host assigns revision and clock to direct playback");
        room.Discover(members, 1400);
        check(room.Snapshot.People.Where(p => p.Approved).All(p => p.Ready), "discovery snapshots cannot overwrite direct readiness");
        room.Apply("host", new("controls", Number: 1), 1400, out _);
        room.Apply("guest", new("buffering"), 1500, out var notice);
        check(room.Playback is { Playing: false, PositionMs: 3200 } && notice?.Type == "notice", "direct guest buffering pauses everyone at host clock position");
        var stop = command with { Playing = false, PositionMs = 0, CommandId = new string('b', 32) };
        check(room.Apply("guest", new("playback", Data: Wire.Serialize(stop)), 1600, out _) && room.Playback?.CommandId == stop.CommandId, "host accepts and acknowledges shared guest stop");
        room.Apply("guest", new("ready"), 1700, out _);
        check(room.Playback is { Playing: false, PositionMs: 0 }, "explicit direct stop cancels pending automatic resume");
        check(!room.Apply("guest", new("media", Data: Wire.Serialize(media)), 1700, out _), "guest cannot replace media even with shared controls");
        room.Apply("host", new("playback", Data: Wire.Serialize(command)), 2000, out _);
        room.Apply("guest", new("buffering"), 2500, out _);
        room.Depart("guest", 2600);
        check(room.Playback?.Playing == true, "departed buffering guest cannot block remaining participants");
        room.Discover(members, 2700);
        check(!room.Snapshot.People.Any(p => p.Id == "guest"), "stale discovery cannot restore a revoked peer");
    }
}
