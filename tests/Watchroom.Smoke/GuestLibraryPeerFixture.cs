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
        await host.ConnectAsync(server, "Library Host"); await guest.ConnectAsync(server, "Library Guest", host.Identity!.Room);
        host.Send(new("admit", Target: guest.Identity!.Peer));
        await Task.WhenAll(hostReady.Task, guestReady.Task, guest.DirectControlsReady).WaitAsync(TimeSpan.FromSeconds(25));
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
    }
}
