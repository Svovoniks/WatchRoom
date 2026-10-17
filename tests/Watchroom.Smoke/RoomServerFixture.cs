using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Watchroom.Core;

// Exercises the room service's cleanup and rate limits plus host resilience against misbehaving guests.
static class RoomServerFixture
{
    public static async Task Run(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "server");
        start.Environment["WATCHROOM_EMPTY_ROOM_SECONDS"] = "1"; start.Environment["WATCHROOM_SWEEP_SECONDS"] = "1";
        start.Environment["WATCHROOM_PERSISTENT_ROOMS_PER_HOUR"] = "2";
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(200); }
            var socketUrl = new Uri(url.Replace("http", "ws") + "/room");
            async Task<(ClientWebSocket Socket, WireMessage Reply)> Open(WireMessage first)
            {
                var socket = new ClientWebSocket(); await socket.ConnectAsync(socketUrl, default);
                await SocketMessages.SendAsync(socket, first, default);
                return (socket, await Read(socket));
            }
            static async Task<WireMessage> Read(ClientWebSocket socket)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await SocketMessages.ReadAsync(socket, timeout.Token) ?? new("closed");
            }
            static async Task Close(ClientWebSocket socket)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch (Exception) { socket.Abort(); }
                socket.Dispose();
            }

            var (temporary, created) = await Open(new("create", Text: "Host"));
            var temporaryCode = Wire.Read<Welcome>(created.Data!).Room;
            await Close(temporary); await Task.Delay(3500);
            var (late, rejected) = await Open(new("join", Text: "Guest", Data: temporaryCode));
            check(rejected is { Type: "error", Text: "Invitation is invalid or expired" }, "server sweeps an empty temporary room after its grace period");
            await Close(late);

            var (live, liveWelcome) = await Open(new("create", Text: "Host"));
            await Task.Delay(3500);
            var (waiting, joined) = await Open(new("join", Text: "Guest", Data: Wire.Read<Welcome>(liveWelcome.Data!).Room));
            check(joined.Type == "welcome", "server keeps temporary rooms that still have members");

            for (var i = 0; i < 200; i++) await SocketMessages.SendAsync(live, new("ping", Number: i), default);
            var pongs = 0;
            while (pongs < 200) { var reply = await Read(live); if (reply.Type == "pong") pongs++; else if (reply.Type is "error" or "closed") break; }
            check(pongs == 200, "host signaling bursts above the old 80 message window stay connected");
            var guestError = "";
            try
            {
                for (var i = 0; i < 300; i++) await SocketMessages.SendAsync(waiting, new("ping", Number: i), default);
                for (var i = 0; i < 400 && guestError == ""; i++) { var reply = await Read(waiting); if (reply.Type is "error" or "closed") guestError = reply.Text ?? reply.Type; }
            }
            catch (WebSocketException) { guestError = "closed"; }
            check(guestError is "Message rate limit exceeded" or "closed", "guest message floods still disconnect that guest");
            await Close(waiting); await Close(live);

            var (owner, ownerWelcome) = await Open(new("create", Target: "persistent", Text: "Owner"));
            var saved = Wire.Read<Welcome>(ownerWelcome.Data!);
            await Close(owner); await Task.Delay(3500);
            var (resumed, resumedWelcome) = await Open(new("resume", Target: saved.HostKey, Text: "Owner", Data: saved.Room));
            check(resumedWelcome.Type == "welcome" && Wire.Read<Welcome>(resumedWelcome.Data!).Room == saved.Room, "persistent rooms survive the sweep and resume with their host key");
            await Close(resumed);
            var (second, secondWelcome) = await Open(new("create", Target: "persistent", Text: "Owner"));
            await Close(second);
            var (third, limited) = await Open(new("create", Target: "persistent", Text: "Owner"));
            check(secondWelcome.Type == "welcome" && limited is { Type: "error", Text: "Persistent room limit reached. Try again later." }, "persistent room creation is limited per address");
            await Close(third);

            var (readyHost, readyWelcome) = await Open(new("create", Text: "Host"));
            var readyRoom = Wire.Read<Welcome>(readyWelcome.Data!).Room;
            var (spammer, spamWelcome) = await Open(new("join", Text: "Spammer", Data: readyRoom));
            await SocketMessages.SendAsync(readyHost, new("admit", Target: Wire.Read<Welcome>(spamWelcome.Data!).Peer), default);
            for (var reply = await Read(spammer); reply.Type != "admitted"; reply = await Read(spammer)) if (reply.Type == "closed") throw new IOException("Spammer was not admitted");
            var hostSnapshots = 0;
            var counting = Task.Run(async () =>
            {
                try { while (true) { var reply = await Read(readyHost); if (reply.Type == "snapshot") Interlocked.Increment(ref hostSnapshots); if (reply.Type == "closed") break; } }
                catch (Exception) { }
            });
            await Task.Delay(300); Interlocked.Exchange(ref hostSnapshots, 0);
            for (var i = 0; i < 50; i++) await SocketMessages.SendAsync(spammer, new(i % 2 == 0 ? "ready" : "buffering"), default);
            for (var i = 0; i < 50; i++) await SocketMessages.SendAsync(spammer, new("ready"), default);
            await Task.Delay(1000);
            check(Volatile.Read(ref hostSnapshots) is > 0 and <= 30, $"one guest's readiness spam is bounded before it reaches other members ({hostSnapshots} snapshots)");
            await Close(spammer); await Close(readyHost); await counting.WaitAsync(TimeSpan.FromSeconds(10));

            await HostSurvivesGuests(url, directory, check);
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, "server.log"), await stdout + await stderr);
        }
    }

    private static async Task HostSurvivesGuests(string url, string directory, Action<bool, string> check)
    {
        var blocked = Path.Combine(directory, "blocked-video.mkv"); Directory.CreateDirectory(blocked);
        await using var host = new RoomClient(); await using var guest = new RoomClient();
        var guestMessages = new ConcurrentQueue<WireMessage>(); guest.Message += guestMessages.Enqueue;
        host.SetHostedLibrary([new MediaItem("blocked", blocked, "Blocked", "Movie")]);
        await host.ConnectAsync(url, "Host");
        await guest.ConnectAsync(url, "Guest", host.Identity!.Room);
        await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer));
        host.Send(new("admit", Target: guest.Identity!.Peer));
        await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(20));
        host.Send(new("catalog-permission", Number: 1));
        await Wait(() => guest.Snapshot?.LibraryBrowsing == true);
        guest.Send(new("catalog-browse", Number: 7, Data: Wire.Serialize(new LibraryBrowseRequest())));
        await Wait(() => guestMessages.Any(m => m.Type == "catalog-page" && m.Number == 7));
        check(Wire.Read<LibraryBrowsePage>(guestMessages.First(m => m.Type == "catalog-page").Data!).Entries.Any(e => e.Id == "blocked"), "offloaded library browse returns the host page");
        guest.Send(new("catalog-watch", Number: 8, Target: "blocked"));
        await Wait(() => guestMessages.Any(m => m.Type == "catalog-error" && m.Number == 8));
        var error = guestMessages.First(m => m.Type == "catalog-error" && m.Number == 8).Text ?? "";
        check(host.IsConnected && guest.IsConnected && !error.Contains(directory, StringComparison.OrdinalIgnoreCase),
            "an unreadable library title reports an error without closing the host room or exposing its path");
        host.Send(new("room-name", Text: "Still open"));
        await Wait(() => guest.Snapshot?.Name == "Still open");
        check(true, "host room controls keep working after the failed library start");

        var hostId = host.Identity.Peer;
        try { for (var i = 0; i < 1500; i++) guest.SendLibrary(hostId, new("library-flood", Number: i)); }
        catch (IOException) { /* The host disconnected the flooding peer. */ }
        await Task.Delay(1500);
        check(host.IsConnected && guest.IsConnected, "a guest message flood never overflows or closes the host's room controls");
        host.Send(new("room-name", Text: "After flood"));
        await Wait(() => guest.DirectControlsReady.IsCompletedSuccessfully && guest.Snapshot?.Name == "After flood", 30);
        check(true, "the flooding guest recovers and receives later host state");
    }

    private static async Task Wait(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Room server fixture timed out"); await Task.Delay(50); }
    }
}
