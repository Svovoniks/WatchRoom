using System.Diagnostics;
using System.IO;
using System.Reflection;
using Watchroom.Core;
using Watchroom.Desktop;

static class RoomRecoveryChecks
{
    public static async Task Run(MainWindow window, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        RoomClient? Current() => (RoomClient?)typeof(MainWindow).GetField("room", flags)!.GetValue(window);
        object? Retry() => typeof(MainWindow).GetField("roomReconnect", flags)!.GetValue(window);
        async Task Wait(Func<bool> condition, string label)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
            check(condition(), label);
        }
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = "http://localhost:" + port;
        Process Start()
        {
            var info = new ProcessStartInfo(Path.GetFullPath(".tools/dotnet/dotnet.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll"));
            info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
            info.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "recovery-server");
            var process = Process.Start(info)!; process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
        }
        async Task Ready()
        {
            using var http = new System.Net.Http.HttpClient();
            for (var i = 0; i < 100; i++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) return; }
                catch (System.Net.Http.HttpRequestException) { }
                await Task.Delay(50);
            }
            throw new IOException("Recovery test server did not start");
        }
        Process server = Start();
        try
        {
            await Ready();
            var original = (RoomClient)Call("NewRoom")!;
            await original.ConnectAsync(url, "Recovery Host", persistent: true);
            var identity = original.Identity!;
            server.Kill(true); await server.WaitForExitAsync(); server.Dispose();
            await Wait(() => Retry() is not null, "unexpected connection loss starts automatic reconnection");
            server = Start(); await Ready();
            await Wait(() => Current()?.IsConnected == true && Current() != original && Retry() is null,
                "automatic reconnection restores the room after the service returns");
            check(Current()!.Identity!.Room == identity.Room && Current()!.Identity!.HostKey == identity.HostKey,
                "automatic reconnection preserves the room and host credential");
            server.Kill(true); await server.WaitForExitAsync();
            await Wait(() => Retry() is not null, "another unexpected disconnect can start recovery again");
            await (Task)Call("Disconnect", false, false)!;
            await Task.Delay(2500);
            check(Current() is null && Retry() is null, "explicit Leave cancels reconnection and keeps the room disconnected");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            server.Dispose();
        }
    }
}
