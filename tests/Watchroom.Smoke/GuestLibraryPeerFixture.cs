using System.Collections.Concurrent;
using Watchroom.Core;

static class GuestLibraryPeerFixture
{
    public static async Task RunIsolated(string sdk, string serverDll, string root, string video, Action<bool, string> check)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = $"http://localhost:{port}";
        var start = new System.Diagnostics.ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        start.Environment["WATCHROOM_DATA"] = System.IO.Path.Combine(root, "guest-library-server");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient(); var ready = false;
            for (var i = 0; i < 80; i++)
            {
                try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            if (!ready) throw new Exception("Guest library test server did not start");
            await Run(url, video, check);
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, errors); }
    }
    public static async Task Run(string server, string video, Action<bool, string> check)
    {
        var homeStore = new LibraryStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(video))!, "guest-home-" + Guid.NewGuid().ToString("N")));
        homeStore.Setting("position:direct-video", "15000");
        await using var host = new RoomClient(homeStore); await using var guest = new RoomClient();
        var hostReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guestReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.LibraryPeerReady += _ => hostReady.TrySetResult(); guest.LibraryPeerReady += _ => guestReady.TrySetResult();
        await host.ConnectAsync(server, "Library Host", persistent: true); await guest.ConnectAsync(server, "Library Guest", host.Identity!.Room);
        host.Send(new("admit", Target: guest.Identity!.Peer));
        await Task.WhenAll(hostReady.Task, guestReady.Task, guest.DirectControlsReady).WaitAsync(TimeSpan.FromSeconds(25));
        host.SetHostedLibrary([new MediaItem("direct-video", video, "Direct browsing fixture", "Movie")]);
        var artworkBytes = Convert.ToBase64String(Enumerable.Range(0, 60000).Select(index => (byte)(index % 251)).ToArray());
        host.LibraryArtwork = _ => artworkBytes;
        LibraryBrowsePage? directPage = null; bool directStarted = false;
        LibraryBrowsePage? homePage = null; bool homeDenied = false;
        var artworkParts = new ConcurrentDictionary<int, string>();
        guest.Message += message =>
        {
            if (message.Type == "catalog-page" && message.Number == 1001) directPage = Wire.Read<LibraryBrowsePage>(message.Data!);
            if (message.Type == "catalog-page" && message.Number == 1002) homePage = Wire.Read<LibraryBrowsePage>(message.Data!);
            if (message.Type == "catalog-error" && message.Number == 1006) homeDenied = true;
            if (message.Type == "catalog-started") directStarted = true;
            if (message.Type == "catalog-artwork-chunk" && message.Number == 1003)
            { var part = Wire.Read<LibraryArtworkChunk>(message.Data!); artworkParts[part.Index] = part.Data; }
        };
        async Task AwaitDirect(Func<bool> condition, string name)
        {
            var until = DateTime.UtcNow.AddSeconds(25);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
            check(condition(), name);
        }
        host.Send(new("catalog-permission", Number: 1));
        await AwaitDirect(() => guest.Snapshot?.LibraryBrowsing == true, "guest receives library permission over the peer connection");
        guest.Send(new("catalog-browse", Number: 1001, Data: Wire.Serialize(new LibraryBrowseRequest())));
        await AwaitDirect(() => directPage?.Entries.Length == 1 && host.Snapshot?.LibraryActivity?.Peer == guest.Identity.Peer,
            "integrated guest browsing acquires the host-authoritative lock");
        guest.Send(new("catalog-browse", Number: 1002, Data: Wire.Serialize(new LibraryBrowseRequest(Home: true, Seed: 5))));
        await AwaitDirect(() => homePage?.Entries.Any(entry => entry.HomeSection == "continue" && entry.PositionMs == 15000) == true &&
            homePage.Entries.Any(entry => entry.HomeSection == "mystery"), "guest Home receives host resume progress and a mystery pick over the peer connection");
        var directView = new LibraryBrowseView(new(), 1, .25, "direct-video", "direct-video");
        guest.Send(new("catalog-follow", Data: Wire.Serialize(directView)));
        await AwaitDirect(() => host.Snapshot?.LibraryActivity?.View == directView, "guest scroll, hover, and selected video reach the host mirror");
        LibraryPointer? hostPointer = null;
        host.Message += message => { if (message.Type == "catalog-pointer" && message.Sender == guest.Identity.Peer) hostPointer = Wire.Read<LibraryPointer>(message.Data!); };
        await AwaitDirect(() => guest.Snapshot?.GuestPointers == true, "the host tells guests it draws their pointer");
        var directPointer = new LibraryPointer(.4, .6, "direct-video", .5, .25);
        guest.Send(new("catalog-pointer", Data: Wire.Serialize(directPointer)));
        await AwaitDirect(() => hostPointer == directPointer && host.Snapshot?.LibraryActivity?.View == directView, "the browsing guest's mouse reaches only the host, outside the room state");
        guest.Send(new("catalog-artwork", Number: 1003, Data: Wire.Serialize(new LibraryArtworkRequest("direct-video", "movie"))));
        await AwaitDirect(() => artworkParts.Values.Sum(part => part.Length) == artworkBytes.Length, "high-resolution artwork crosses the peer channel in bounded chunks");
        check(string.Concat(artworkParts.OrderBy(pair => pair.Key).Select(pair => pair.Value)) == artworkBytes, "artwork chunks reconstruct the original bytes without loss");
        guest.Send(new("catalog-watch", Number: 1004, Target: "direct-video"));
        await AwaitDirect(() => directStarted && guest.Snapshot?.Media?.Title == "Direct browsing fixture", "guest detail playback publishes the host video into the room");
        guest.Send(new("catalog-watch", Number: 1005, Target: "direct-video", Text: "resume"));
        await AwaitDirect(() => guest.Snapshot?.Playback?.PositionMs == 15000, "guest continue watching starts room playback at the host's saved position");
        guest.Send(new("catalog-release"));
        await AwaitDirect(() => host.Snapshot?.LibraryActivity is null, "leaving guest browsing releases its lock after playback starts");
        host.Send(new("catalog-permission", Number: 0));
        await AwaitDirect(() => guest.Snapshot?.LibraryBrowsing == false, "host revokes guest Home browsing over the peer connection");
        guest.Send(new("catalog-browse", Number: 1006, Data: Wire.Serialize(new LibraryBrowseRequest(Home: true))));
        await AwaitDirect(() => homeDenied, "guest Home requests are denied after host revocation");
        // The room's current video artwork stays available for the hero without library browsing; nothing else is.
        var sessionParts = new ConcurrentDictionary<long, ConcurrentDictionary<int, string>>();
        var sessionErrors = new ConcurrentBag<long>();
        guest.Message += message =>
        {
            if (message.Type == "session-artwork-chunk")
            { var part = Wire.Read<LibraryArtworkChunk>(message.Data!); sessionParts.GetOrAdd(message.Number, _ => new())[part.Index] = part.Data; }
            if (message.Type == "error" && message.Number < 0) sessionErrors.Add(message.Number);
        };
        host.SetSessionArtwork("Direct browsing fixture", video);
        guest.Send(new("session-artwork", Number: -2001, Text: "Direct browsing fixture"));
        await AwaitDirect(() => sessionParts.GetValueOrDefault(-2001)?.Values.Sum(part => part.Length) == artworkBytes.Length,
            "guests fetch the room video's artwork for the hero without library browsing");
        check(string.Concat(sessionParts[-2001].OrderBy(pair => pair.Key).Select(pair => pair.Value)) == artworkBytes && sessionErrors.IsEmpty, "hero artwork chunks reconstruct the host's artwork");
        guest.Send(new("session-artwork", Number: -2002, Text: "Some other title"));
        await AwaitDirect(() => sessionParts.GetValueOrDefault(-2002) is { Count: 1 } parts && parts[0] == "", "the host returns no artwork for titles the room is not watching");
        // Full HD copies are paced: only a window of chunks is in flight until the guest acknowledges them.
        var fullBytes = Convert.ToBase64String(Enumerable.Range(0, 600000).Select(index => (byte)(index * 7 % 253)).ToArray());
        host.FullHdArtwork = _ => fullBytes;
        var fullParts = new ConcurrentDictionary<int, string>();
        var acknowledge = false; var pacedOnly = true;
        guest.Message += message =>
        {
            if (message.Type != "session-artwork-chunk" || message.Number != -2003) return;
            var part = Wire.Read<LibraryArtworkChunk>(message.Data!); fullParts[part.Index] = part.Data;
            pacedOnly &= part.Paced && ArtworkTransfer.ValidChunk(part);
            if (acknowledge) guest.Send(new("artwork-ack", Number: message.Number));
        };
        // While the room wants playback and the guest has not loaded the video, the host keeps its upload for video.
        var declined = new ConcurrentBag<LibraryArtworkChunk>();
        guest.Message += message => { if (message.Type == "session-artwork-chunk" && message.Number == -2004) declined.Add(Wire.Read<LibraryArtworkChunk>(message.Data!)); };
        if (host.Snapshot?.Playback?.WantsPlayback == true)
        {
            guest.Send(new("session-artwork", Number: -2004, Text: "Direct browsing fixture", Data: "full"));
            await AwaitDirect(() => declined.Count == 1, "the host answers full HD requests while a guest is buffering");
            check(declined.Single() is { Paced: true, Count: 1, Data: "" }, "full HD artwork is declined while someone is still buffering");
        }
        host.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, host.Snapshot!.Media!.Id, false, 15000, 0))));
        await AwaitDirect(() => host.Snapshot?.Playback?.WantsPlayback == false, "pausing frees the host's upload for artwork");
        guest.Send(new("session-artwork", Number: -2003, Text: "Direct browsing fixture", Data: "full"));
        await AwaitDirect(() => fullParts.Count >= ArtworkTransfer.Window, "a full HD transfer starts with its first window of chunks");
        await Task.Delay(700);
        check(fullParts.Count == ArtworkTransfer.Window, "the host waits for acknowledgements before sending more full HD chunks");
        acknowledge = true;
        foreach (var _ in Enumerable.Range(0, ArtworkTransfer.Window)) guest.Send(new("artwork-ack", Number: -2003));
        await AwaitDirect(() => fullParts.Values.Sum(part => part.Length) == fullBytes.Length, "acknowledged full HD chunks complete the transfer");
        check(pacedOnly && string.Concat(fullParts.OrderBy(pair => pair.Key).Select(pair => pair.Value)) == fullBytes, "full HD chunks are paced, bounded and reconstruct the original bytes");
        check(sessionErrors.IsEmpty, "acknowledgements never count as rejected room commands");
        host.Send(new("last-session", Data: Wire.Serialize(new RoomSession("Direct browsing fixture", 15000, 60000, false, 1, ["Library Guest"], "Library Host", "direct-video"))));
        await AwaitDirect(() => guest.Snapshot?.LastSession is { Title: "Direct browsing fixture", LibraryId: null }, "the host's last session reaches guests without its library id");
        // The host's queues reach the guest over the peer connection in chunks, and a guest's queue edit reaches the host.
        var traffic = new ConcurrentQueue<Action>(); var assembler = new QueueStateAssembler();
        SharedQueues? received = null; QueueEdit? edit = null;
        guest.LibraryMessage += (id, message) => traffic.Enqueue(() => { if (message.Type == QueueSharing.StateMessage && assembler.Add(message) is { } state) received = state; });
        host.LibraryMessage += (id, message) => traffic.Enqueue(() => { if (message.Type == QueueSharing.EditMessage && id == guest.Identity.Peer) edit = Wire.Read<QueueEdit>(message.Data!); });
        async Task Wait(Func<bool> condition, string name)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                while (traffic.TryDequeue(out var action)) action();
                if (condition()) { check(true, name); return; }
                if (DateTime.UtcNow > until) throw new Exception("Timeout: " + name);
                await Task.Delay(25);
            }
        }
        var episodes = Enumerable.Range(0, 1200).Select(i => new SharedQueueMedia("video-" + i, "Смешарики " + i, "Show", Series: "Смешарики", Season: 1, Episode: i + 1)).ToArray();
        var shared = new SharedQueues([new("evening", "Вечер", episodes.Select(item => item.Id).ToArray())], episodes, "evening", 3);
        var chunks = QueueSharing.Messages(1, shared);
        foreach (var chunk in chunks) host.SendLibrary(guest.Identity.Peer, chunk);
        await Wait(() => received is not null, $"the host's queues reach the guest in {chunks.Length} chunks over the peer connection");
        check(received!.Queues.Single().MediaIds.SequenceEqual(shared.Queues[0].MediaIds) && received.Media[5].Title == "Смешарики 5" && received.ActiveIndex == 3,
            "the guest rebuilds the host's queues exactly");
        guest.SendLibrary(host.Identity.Peer, new(QueueSharing.EditMessage, Data: Wire.Serialize(new QueueEdit("order", "evening", QueueSharing.Fingerprint(received.Queues[0].MediaIds), [1, 0]))));
        await Wait(() => edit is not null, "a guest's queue edit reaches the host over the peer connection");
        check(edit!.Order!.SequenceEqual([1, 0]) && edit.Fingerprint == QueueSharing.Fingerprint(shared.Queues[0].MediaIds), "the host receives the guest's edit intact");
        var previous = host.HostedMedia; bool missing = false;
        try { host.SetHostedFile(video + ".missing", "Missing"); } catch (FileNotFoundException) { missing = true; }
        check(missing && ReferenceEquals(previous, host.HostedMedia), "failed guest file preparation preserves current media");
        var identity = host.Identity!;
        var departures = new ConcurrentQueue<WireMessage>();
        guest.Message += message => { if (message.Type == "peer-left") departures.Enqueue(message); };
        await host.DisposeAsync();
        await AwaitDirect(() => guest.IsConnected && guest.Snapshot?.People.All(p => !p.IsHost) == true,
            "guest stays connected in the room after the host leaves");
        check(guest.Snapshot?.Media is null && guest.Snapshot?.Playback is null, "host departure clears unavailable playback");
        check(departures.Count == 1 && departures.Single().Text == "Library Host", "host departure emits one named disconnect notification");
        await using var waiting = new RoomClient();
        await waiting.ConnectAsync(server, "Offline joining guest", identity.Room);
        await AwaitDirect(() => waiting.Snapshot?.People.Any(p => p.Id == waiting.Identity!.Peer) == true,
            "new guest can join and wait while the host is offline");
        await using var returning = new RoomClient();
        await returning.ConnectAsync(server, "Library Host", identity.Room, identity.HostKey);
        await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        await AwaitDirect(() => guest.Snapshot?.People.Any(p => p.Id == returning.Identity!.Peer && p.IsHost) == true,
            "waiting admitted guest automatically reconnects when the host returns");
        returning.SetHostedFile(video, "Returned video");
        returning.Send(new("media", Data: Wire.Serialize(returning.HostedMedia!.Media)));
        await AwaitDirect(() => guest.Snapshot?.Media?.Title == "Returned video", "returned host starts a fresh direct playback session");
        returning.Send(new("admit", Target: waiting.Identity!.Peer));
        await waiting.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
        check(waiting.Snapshot?.Media?.Title == "Returned video", "offline joining guest receives playback only after host admission");
        await waiting.DisposeAsync();
        await AwaitDirect(() => departures.Any(m => m.Text == "Offline joining guest"), "guests receive named notifications when another guest disconnects");
        var transports = (System.Collections.IDictionary)typeof(RoomClient).GetField("peers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(guest)!;
        ((IDisposable)transports[returning.Identity!.Peer]!).Dispose();
        await AwaitDirect(() => guest.IsConnected && !guest.DirectControlsReady.IsCompletedSuccessfully,
            "unexpected peer loss leaves guest in the room while rebuilding controls");
        await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
        check(guest.Snapshot?.Media?.Title == "Returned video", "guest automatically repairs a failed peer connection without rejoining the room");
    }
}
