using BlackPearl.Core;

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

        // The host's queues travel to guests in chunks: any order rebuilds them, late older states are ignored, malformed edits are refused.
        var films = Enumerable.Range(0, 3000).Select(i => new SharedQueueMedia("m" + i, $"Фильм «{i}» \"quoted\"", "Movie", 2026)).ToArray();
        var queues = new SharedQueues([new("q", "Очередь", films.Select(film => film.Id).ToArray())], films, "q", 7);
        var chunks = QueueSharing.Messages(5, queues);
        check(chunks.Length > 1 && chunks.All(chunk => System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(chunk)) < 60000),
            $"shared queues split into {chunks.Length} chunks under the 64 KiB room message limit");
        var assembler = new QueueStateAssembler(); SharedQueues? rebuilt = null;
        foreach (var chunk in chunks.Reverse()) rebuilt = assembler.Add(chunk) ?? rebuilt;
        check(rebuilt is { ActiveIndex: 7 } && rebuilt.Media.Length == 3000 && rebuilt.Media[42].Title == films[42].Title && rebuilt.Queues[0].Name == "Очередь",
            "chunks arriving in any order rebuild the shared queues exactly");
        check(QueueSharing.Messages(4, queues with { Queues = [] }).All(chunk => assembler.Add(chunk) is null), "an older queue state arriving late is ignored");
        check(QueueSharing.Problem(new("order", "q", "short", [0])) is not null && QueueSharing.Problem(new("create", Name: "  ")) is not null &&
            QueueSharing.Problem(new("add", "q", MediaIds: [])) is not null && QueueSharing.Problem(new("delete", "q")) is not null &&
            QueueSharing.Problem(new("create", Name: "Weekend")) is null && QueueSharing.Problem(new("order", "q", QueueSharing.Fingerprint(["a"]), [0])) is null,
            "malformed or unsupported queue edits are refused");
    }
}
