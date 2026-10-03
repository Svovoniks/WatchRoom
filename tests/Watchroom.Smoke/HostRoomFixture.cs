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
        var scoped = new HostRoomCoordinator("host"); scoped.Discover(members, 1000);
        scoped.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _);
        var firstGeneration = scoped.Playback!.Generation;
        WireMessage Ready(long generation) => new("ready", Data: Wire.Serialize(new PlaybackReadiness(media.Id, generation)));
        scoped.Apply("host", Ready(firstGeneration), 1000, out _); scoped.Apply("guest", Ready(firstGeneration), 1000, out _);
        scoped.Apply("host", new("playback", Data: Wire.Serialize(command)), 1000, out _);
        scoped.Apply("guest", new("buffering", Data: Wire.Serialize(new PlaybackReadiness(media.Id, firstGeneration))), 1500, out _);
        check(scoped.Playback is { Playing: false, WantsPlayback: true }, "room retains Play intent while a guest buffers");
        scoped.Apply("host", new("playback", Data: Wire.Serialize(command with { Seek = true, PositionMs = 80000 })), 1600, out _);
        var seekGeneration = scoped.Playback!.Generation;
        check(seekGeneration > firstGeneration && scoped.Snapshot.People.All(p => !p.Ready), "seek invalidates previous readiness for every participant");
        scoped.Apply("guest", Ready(firstGeneration), 1700, out _);
        check(!scoped.Snapshot.People.Single(p => p.Id == "guest").Ready && !scoped.Playback.Playing, "obsolete ready cannot resume a newer seek");
        scoped.Apply("host", Ready(seekGeneration), 1800, out _); scoped.Apply("guest", Ready(seekGeneration), 1800, out _);
        check(scoped.Playback is { Playing: true, PositionMs: 80000 }, "current seek resumes after both participants become ready");
        scoped.Apply("guest", new("buffering", Data: Wire.Serialize(new PlaybackReadiness(media.Id, firstGeneration))), 1900, out _);
        check(scoped.Playback.Playing, "late buffering cannot pause a newer seek");
        scoped.Apply("host", new("playback", Data: Wire.Serialize(stop)), 2000, out _);
        scoped.Apply("guest", Ready(seekGeneration), 2100, out _);
        check(scoped.Playback is { Playing: false, WantsPlayback: false }, "explicit Pause cancels resume intent despite delayed readiness");
        scoped.Apply("host", new("playback", Data: Wire.Serialize(command with { Seek = true, PositionMs = 10000 })), 2200, out _);
        scoped.Apply("host", new("playback", Data: Wire.Serialize(command with { Seek = true, PositionMs = 20000 })), 2300, out _);
        var latestGeneration = scoped.Playback!.Generation;
        scoped.Apply("host", Ready(latestGeneration - 1), 2400, out _); scoped.Apply("guest", Ready(latestGeneration - 1), 2400, out _);
        check(!scoped.Playback.Playing && scoped.Playback.PositionMs == 20000, "rapid seeks retain only the latest readiness generation");
        scoped.Apply("host", Ready(latestGeneration), 2500, out _); scoped.Apply("guest", Ready(latestGeneration), 2500, out _);
        check(scoped.Playback.Playing, "latest rapid seek completes without waiting for obsolete seeks");
        scoped.Apply("guest", new("buffering"), 2600, out _);
        check(scoped.Playback.Playing, "scoped peers cannot fall back to ambiguous unscoped buffering events");
        var mixed = new HostRoomCoordinator("host"); mixed.Discover(members, 1000);
        mixed.Apply("host", new("media", Data: Wire.Serialize(media)), 1000, out _);
        mixed.Apply("host", Ready(mixed.Playback!.Generation), 1000, out _);
        mixed.Apply("guest", new("ready"), 1000, out _);
        mixed.Apply("host", new("playback", Data: Wire.Serialize(command with { Seek = true })), 1100, out _);
        check(mixed.Snapshot.People.Single(p => p.Id == "guest").Ready && !mixed.Playback!.Playing,
            "legacy guest retains buffering readiness instead of waiting for an unsupported seek acknowledgement");
        mixed.Apply("host", Ready(mixed.Playback!.Generation), 1200, out _);
        check(mixed.Playback.Playing, "updated host can resume a seek with a legacy guest");
    }
}
