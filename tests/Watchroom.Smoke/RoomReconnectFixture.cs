using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Watchroom.Core;

static class RoomReconnectFixture
{
    public static async Task Run(string directory, bool sites, Action<bool, string> check)
    {
        Directory.CreateDirectory(directory);
        var hostStore = new LibraryStore(Path.Combine(directory, "host"));
        var guestDirectory = Path.Combine(directory, "guest");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(sites ? "node" : Path.GetFullPath(".tools/dotnet/dotnet.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (sites) start.ArgumentList.Add(Path.GetFullPath("sites/tests/direct-control-server.mjs"));
        else
        {
            start.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "server");
            start.ArgumentList.Add(Path.GetFullPath("artifacts/room-server/Watchroom.Server.dll"));
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
        }
        using var server = Process.Start(start)!;
        if (sites) url = await server.StandardOutput.ReadLineAsync() ?? throw new IOException("No fixture URL");
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            await Wait(async () => { try { return (await http.GetAsync(url + "/health")).IsSuccessStatusCode; } catch { return false; } });
            var host = new RoomClient(hostStore);
            await host.ConnectAsync(url, "Host", persistent: true); var identity = host.Identity!;
            var guest = new RoomClient(new LibraryStore(guestDirectory));
            await guest.ConnectAsync(url, "Guest", identity.Room);
            await Wait(() => Task.FromResult(host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer)));
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(20));
            host.Send(new("controls", Number: 1));
            await Wait(() => Task.FromResult(guest.Snapshot?.SharedControls == true));
            check(guest.Identity.GuestKey?.Length == 64, "admission issues a device reconnect credential");
            await guest.DisposeAsync(); await host.DisposeAsync(); await Task.Delay(400);
            await using var reopened = new RoomClient(new LibraryStore(Path.Combine(directory, "host")));
            await reopened.ConnectAsync(url, "Host", identity.Room, identity.HostKey);
            check(reopened.Snapshot?.SharedControls == true, "host restart restores enabled shared controls");
            await using (var returned = new RoomClient(new LibraryStore(guestDirectory)))
            {
                await returned.ConnectAsync(url, "New display name", identity.Room);
                await returned.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(20));
                await Wait(() => Task.FromResult(returned.Snapshot?.SharedControls == true));
                check(true, "guest restart and rename retain admission and receive saved controls");
                check(returned.Snapshot?.AdmittedGuests is null && returned.Snapshot!.People.All(p => p.GuestId is null), "guest snapshots exclude the private admission roster");
            }
            await using (var impostor = new RoomClient())
            {
                await impostor.ConnectAsync(url, "Guest", identity.Room);
                await Wait(() => Task.FromResult(impostor.Snapshot is not null));
                check(!impostor.Snapshot!.People.Single(p => p.Id == impostor.Identity!.Peer).Approved, "matching display names do not inherit admission");
            }
            await Wait(() => Task.FromResult(reopened.Snapshot?.AdmittedGuests?.Length == 1));
            reopened.Send(new("remove", Target: reopened.Snapshot!.AdmittedGuests![0].Id));
            await Wait(() => Task.FromResult(reopened.Snapshot?.AdmittedGuests?.Length == 0));
            await using (var revoked = new RoomClient(new LibraryStore(guestDirectory)))
            {
                await revoked.ConnectAsync(url, "Guest", identity.Room);
                await Wait(() => Task.FromResult(revoked.Snapshot is not null));
                check(!revoked.Snapshot!.People.Single(p => p.Id == revoked.Identity!.Peer).Approved, "removing an offline guest revokes reconnect access");
            }
            reopened.Send(new("controls", Number: 0));
            await Wait(() => Task.FromResult(reopened.Snapshot?.SharedControls == false));
            await reopened.DisposeAsync(); await Task.Delay(400);
            await using var again = new RoomClient(hostStore);
            await again.ConnectAsync(url, "Host", identity.Room, identity.HostKey);
            check(again.Snapshot?.SharedControls == false, "disabled shared controls persist across another reconnect");
            var registry = new PersistentRooms(Path.Combine(directory, "registry"));
            var key = registry.Create("test-room"); registry.Update("test-room", [new("guest-hash", "Guest")], true);
            var restored = new PersistentRooms(Path.Combine(directory, "registry"));
            check(restored.Verify("test-room", key) && restored.All().Single().Guests?.Length == 1 && restored.All().Single().SharedControls, "server registry restart retains ownership, settings and guest admission");
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
    }
    private static async Task Wait(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!await condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Room reconnect fixture timed out"); await Task.Delay(50); }
    }
}
