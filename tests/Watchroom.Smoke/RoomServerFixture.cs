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
        await CloudflareRelay(sdk, serverDll, directory, check);
    }

    // A stand-in for Cloudflare's credential API checks that the server turns its ICE servers into libdatachannel URLs,
    // and that rate limits follow the client address reported by the loopback reverse proxy.
    private static async Task CloudflareRelay(string sdk, string serverDll, string directory, Action<bool, string> check)
    {
        static int FreePort() { using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); return ((IPEndPoint)probe.LocalEndpoint).Port; }
        var api = $"http://localhost:{FreePort()}/";
        using var cloudflare = new HttpListener(); cloudflare.Prefixes.Add(api); cloudflare.Start();
        var requests = new ConcurrentQueue<(string Path, string? Authorization, string Body)>();
        var failing = 1;
        _ = Task.Run(async () =>
        {
            while (cloudflare.IsListening)
            {
                HttpListenerContext context;
                try { context = await cloudflare.GetContextAsync(); } catch (Exception) { return; }
                using (var reader = new StreamReader(context.Request.InputStream)) requests.Enqueue((context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], await reader.ReadToEndAsync()));
                if (Volatile.Read(ref failing) == 1) { context.Response.StatusCode = 503; context.Response.Close(); continue; }
                var body = """{"iceServers":[{"urls":["stun:stun.cloudflare.com:3478","stun:stun.cloudflare.com:53"]},{"urls":["turn:turn.cloudflare.com:3478?transport=udp","turn:turn.cloudflare.com:53?transport=udp","turns:turn.cloudflare.com:5349?transport=tcp"],"username":"user/1","credential":"pa:ss"}]}"""u8.ToArray();
                context.Response.ContentType = "application/json"; await context.Response.OutputStream.WriteAsync(body); context.Response.Close();
            }
        });
        var url = "http://localhost:" + FreePort();
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "cloudflare-server");
        start.Environment["CLOUDFLARE_TURN_KEY_ID"] = "key-id"; start.Environment["CLOUDFLARE_TURN_API_TOKEN"] = "api-token";
        start.Environment["CLOUDFLARE_TURN_API"] = api.TrimEnd('/'); start.Environment["CLOUDFLARE_TURN_RETRY_SECONDS"] = "1";
        start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            string health = "";
            for (var i = 0; i < 100 && health == ""; i++) { try { var reply = await http.GetAsync(url + "/health"); if (reply.IsSuccessStatusCode) health = await reply.Content.ReadAsStringAsync(); } catch (HttpRequestException) { } if (health == "") await Task.Delay(200); }
            async Task<string[]> CreateRoom()
            {
                using var socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(url.Replace("http", "ws") + "/room"), default);
                await SocketMessages.SendAsync(socket, new("create", Text: "Host"), default);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var reply = await SocketMessages.ReadAsync(socket, timeout.Token);
                socket.Abort();
                return reply is { Type: "welcome" } ? Wire.Read<Welcome>(reply.Data!).IceServers : ["no welcome: " + reply?.Type];
            }
            await Task.Delay(500);
            health = await http.GetStringAsync(url + "/health");
            check(health.Contains("\"relayConfigured\":false") && health.Contains("\"stunConfigured\":true"), "a failed Cloudflare request at startup reports STUN without relay");
            var fallback = await CreateRoom();
            check(fallback.SequenceEqual(["stun:stun.cloudflare.com:3478"]), $"rooms still open with STUN alone while Cloudflare fails ({string.Join(' ', fallback)})");

            Volatile.Write(ref failing, 0); await Task.Delay(1500);
            await CreateRoom();
            for (var i = 0; i < 10 && !health.Contains("\"relayConfigured\":true"); i++) { await Task.Delay(300); health = await http.GetStringAsync(url + "/health"); }
            check(health.Contains("\"relayConfigured\":true"), "the server retries Cloudflare and reports relay once it recovers");
            check(requests.Any(r => r.Path == "/turn/keys/key-id/credentials/generate-ice-servers" && r.Authorization == "Bearer api-token" && r.Body.Contains("\"ttl\":86400")),
                "the server requests day-long Cloudflare credentials with its API token");
            var ice = await CreateRoom();
            check(ice.SequenceEqual(["stun:stun.cloudflare.com:3478", "turn://user%2F1:pa%3Ass@turn.cloudflare.com:3478?transport=udp", "turns://user%2F1:pa%3Ass@turn.cloudflare.com:5349?transport=tcp"]),
                $"welcomes carry Cloudflare's STUN and TURN servers with escaped credentials and without port 53 ({string.Join(' ', ice)})");

            async Task<int> Probe(string client)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url + "/health"); message.Headers.Add("X-Forwarded-For", client);
                return (int)(await http.SendAsync(message)).StatusCode;
            }
            var statuses = new List<int>();
            for (var i = 0; i < 31; i++) statuses.Add(await Probe("203.0.113.7"));
            check(statuses[^1] == 429 && await Probe("203.0.113.8") == 200, "rate limits apply per client address forwarded by a loopback proxy");
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync(); cloudflare.Stop();
            await File.WriteAllTextAsync(Path.Combine(directory, "cloudflare-server.log"), await stdout + await stderr);
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
