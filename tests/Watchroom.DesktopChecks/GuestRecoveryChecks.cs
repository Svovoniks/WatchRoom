using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

static class GuestRecoveryChecks
{
    public static async Task Run(MainWindow window, string directory, Action<bool, string>? check = null)
    {
        PlaybackDiagnostics.Initialize(Path.Combine(directory, "logs"));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        check ??= (condition, label) => { if (!condition) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); };
        void Check(bool condition, string label) => check(condition, label);
        async Task Wait(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!condition()) await Task.Delay(25, deadline.Token);
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(sdk)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "guest-recovery-server");
        info.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll"));
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
        using var server = Process.Start(info)!;
        var output = server.StandardOutput.ReadToEndAsync(); var errors = server.StandardError.ReadToEndAsync();
        await using var host = new RoomClient();
        host.Status += text => Console.WriteLine("Host: " + text);
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; }
                catch (System.Net.Http.HttpRequestException) { }
                if (attempt == 99) throw new IOException("Guest recovery fixture server did not start");
                await Task.Delay(50);
            }
            var file = Path.Combine(directory, "recovery-video.avi"); VideoFixture.Write(file, 120);
            host.SetHostedFile(file, "Recovery video");
            await host.ConnectAsync(url, "UI recovery host");
            host.Send(new("media", Data: Wire.Serialize(host.HostedMedia!.Media)));
            var guest = (RoomClient)Call("NewRoom")!;
            guest.Status += text => Console.WriteLine("Guest: " + text);
            await guest.ConnectAsync(url, "UI recovery guest", host.Identity!.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer));
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => Field("bridge") is not null && (bool)Field("ready")!);
            var player = (MediaPlayer)Field("player")!;
            host.Send(new("ready", Data: Wire.Serialize(new PlaybackReadiness(host.HostedMedia!.Media.Id, host.Snapshot!.Playback!.Generation))));
            host.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, host.HostedMedia.Media.Id, true, 0, 0))));
            await Wait(() => player.IsPlaying && player.Time > 500);
            var priorPosition = player.Time;
            var bridge = Field("bridge"); var loadedMedia = Field("loadedMedia"); var loadGeneration = Field("loadGeneration");
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(WireMessage message)
            {
                if (message.Type == "peer-recovering") window.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        Check(Equals(Field("loadedMedia"), loadedMedia) && Equals(Field("loadGeneration"), loadGeneration) && ReferenceEquals(Field("bridge"), bridge),
                            "guest interruption retains VLC media, input bridge and load generation");
                        Check(((Border)window.FindName("PlayerEmpty")).Visibility == Visibility.Collapsed && Field("disconnectToast") is null,
                            "brief interruption preserves the video surface and does not open a disconnect alert");
                        preserved.TrySetResult();
                    }
                    catch (Exception ex) { preserved.TrySetException(ex); }
                });
                if (message.Type == "peer-recovered") window.Dispatcher.BeginInvoke(() => restored.TrySetResult());
            }
            guest.Message += Observe;
            var peers = (ConcurrentDictionary<string, PeerTransport>)typeof(RoomClient).GetField("peers", flags)!.GetValue(host)!;
            peers[guest.Identity!.Peer].Dispose();
            await preserved.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(Equals(Field("loadedMedia"), loadedMedia) && Equals(Field("loadGeneration"), loadGeneration) && ReferenceEquals(Field("bridge"), bridge),
                "restored guest connection resumes through the same player input without reloading the movie");
            Check(!guest.IsPeerRecovering && Field("disconnectToast") is null, "successful recovery clears the reconnecting state quietly");
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer && p.Ready));
            Check(true, "the retained player reports readiness again after host readmission");
            await Wait(() => player.IsPlaying && player.Time > priorPosition + 250);
            Check(true, "guest playback automatically resumes and advances after reconnecting");
            guest.Message -= Observe;
            await host.DisposeAsync();
            await Wait(() => guest.Snapshot?.Media is null && Field("loadedMedia") is null);
            Check(!guest.IsPeerRecovering, "an actual host departure clears playback instead of retaining a disconnected movie");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); }
            await File.WriteAllTextAsync(Path.Combine(directory, "guest-recovery-errors.log"), await errors);
            await File.WriteAllTextAsync(Path.Combine(directory, "guest-recovery-server.log"), await output);
            await PlaybackDiagnostics.FlushAsync();
        }
    }
}
