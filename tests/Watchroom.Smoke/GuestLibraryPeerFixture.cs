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
        await using var host = new RoomClient(); await using var guest = new RoomClient();
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
        var artworkParts = new ConcurrentDictionary<int, string>();
        guest.Message += message =>
        {
            if (message.Type == "catalog-page" && message.Number == 1001) directPage = Wire.Read<LibraryBrowsePage>(message.Data!);
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
        var directView = new LibraryBrowseView(new(), 1, .25, "direct-video", "direct-video");
        guest.Send(new("catalog-follow", Data: Wire.Serialize(directView)));
        await AwaitDirect(() => host.Snapshot?.LibraryActivity?.View == directView, "guest scroll, hover, and selected video reach the host mirror");
        guest.Send(new("catalog-artwork", Number: 1003, Data: Wire.Serialize(new LibraryArtworkRequest("direct-video", "movie"))));
        await AwaitDirect(() => artworkParts.Values.Sum(part => part.Length) == artworkBytes.Length, "high-resolution artwork crosses the peer channel in bounded chunks");
        check(string.Concat(artworkParts.OrderBy(pair => pair.Key).Select(pair => pair.Value)) == artworkBytes, "artwork chunks reconstruct the original bytes without loss");
        guest.Send(new("catalog-watch", Number: 1004, Target: "direct-video"));
        await AwaitDirect(() => directStarted && guest.Snapshot?.Media?.Title == "Direct browsing fixture", "guest detail playback publishes the host video into the room");
        guest.Send(new("catalog-release"));
        await AwaitDirect(() => host.Snapshot?.LibraryActivity is null, "leaving guest browsing releases its lock after playback starts");
        var owned = new RoomLibrary(true, host.Identity.Peer); var remote = new RoomLibrary(false, guest.Identity.Peer);
        var traffic = new ConcurrentQueue<Action>();
        host.LibraryMessage += (id, message) => traffic.Enqueue(() => owned.Receive(id, message));
        guest.LibraryMessage += (id, message) => traffic.Enqueue(() => remote.Receive(id, message));
        owned.Send += host.SendLibrary; remote.Send += guest.SendLibrary;
        owned.SetPeople(host.Snapshot!.People);
        owned.Publish(Enumerable.Range(0, 43).Select(i => new SharedLibraryItem("shared-" + i, "Room title " + i, "Movie", 2026, null, null, null, null, true)));
        owned.SetAccess(guest.Identity.Peer, new(true, true, true));
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
        await Wait(() => remote.Catalog.Count == 43, "guest catalog crosses dedicated native bulk channel");
        remote.Request(host.Identity.Peer, "add", "shared-10");
        await Wait(() => remote.Queue.Entries.Length == 1, "guest queue edit crosses native room control channel");
        remote.SendView(host.Identity.Peer, new(1, true, "", "Movie", 4, 2, 0, remote.Catalog.Keys.Take(8).ToArray(), "shared-1", "shared-4"));
        await Wait(() => owned.Views.GetValueOrDefault(guest.Identity.Peer)?.HoveredId == "shared-4", "guest hover reaches host over native peer connection");
        LibraryStart? requested = null; owned.StartRequested += request => requested = request;
        remote.Request(host.Identity.Peer, "next");
        await Wait(() => requested is not null, "guest requests next video directly from host");
        var prepared = RoomClient.PrepareHostedFile(video, requested!.Item.DisplayTitle);
        check(owned.CanCommit(requested), "host authorizes prepared guest-requested video");
        host.PublishHostedFile(prepared, true); owned.CompleteStart(requested, true);
        await Wait(() => guest.Snapshot?.Media?.Id == prepared.Media.Media.Id && remote.Queue.Entries.Length == 0, "prepared video is published and queued entry consumed");
        check(guest.Snapshot!.Playback?.Playing == false, "guest-requested start waits for readiness");
        host.Send(new("ready")); guest.Send(new("ready"));
        await Wait(() => guest.Snapshot?.Playback?.Playing == true, "guest-requested video automatically starts when peers are ready");
        var previous = host.HostedMedia; bool missing = false;
        try { host.SetHostedFile(video + ".missing", "Missing"); } catch (FileNotFoundException) { missing = true; }
        check(missing && ReferenceEquals(previous, host.HostedMedia), "failed guest file preparation preserves current media");
        owned.SetAccess(guest.Identity.Peer, new());
        await Wait(() => !remote.OwnAccess.Browse && remote.Catalog.Count == 0, "host revocation clears native guest catalog");
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
