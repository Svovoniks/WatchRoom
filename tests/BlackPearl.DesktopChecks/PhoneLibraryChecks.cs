using System.Diagnostics;
using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;

// What BlackPearl Mobile does with the library (PhoneLibrary.cs), the part the app and the phone host share, through a
// local room server with a stand-in phone (tests/BlackPearl.Smoke/FakePhone.cs). Continue on PC ends the phone's stream
// before the PC confirms: a phone still reading video got the confirmation late, and stayed on its player.
static class PhoneLibraryChecks
{
    static int passed;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); passed++;
    }

    public static async Task Run(string directory)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("src/BlackPearl.Server/bin/Release/net10.0/BlackPearl.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url); start.Environment["BLACKPEARL_DATA"] = Path.Combine(directory, "phone-server");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using (var http = new System.Net.Http.HttpClient())
                for (var attempt = 0; ; attempt++)
                {
                    try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                    if (attempt >= 200) throw new IOException("Phone library check server did not start"); await Task.Delay(50);
                }

            var data = Path.Combine(directory, "phone-library");
            Directory.CreateDirectory(data);
            var path = Path.Combine(data, "Night Signal (2024).mkv");
            var bytes = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 7 % 253)).ToArray();
            File.WriteAllBytes(path, bytes);
            var store = new LibraryStore(data);
            store.Save(new MediaItem("night-signal", path, "Night Signal", "Movie", 2024));
            // A show of three episodes, and a queue that holds one of them twice and a video the library no longer has.
            foreach (var episode in new[] { 1, 2, 3 })
            {
                var file = Path.Combine(data, $"Harbor Lights S01E0{episode}.mkv");
                File.WriteAllBytes(file, bytes.AsSpan(0, 1000).ToArray());
                store.Save(new MediaItem($"harbor-{episode}", file, "Harbor Lights", "Show", 2023, "Harbor Lights", 1, episode));
            }
            var queued = new SavedQueue("evening", "Evening", ["harbor-3", "gone-video", "night-signal", "harbor-1", "harbor-3"]);
            store.Setting("queues", Wire.Serialize(new[] { queued }));
            store.Setting("activeQueue", "evening");
            store.Setting("queuePosition", "-1");
            store.Setting("watched:harbor-3", "true");
            var paired = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Check phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
            new LibraryPhoneStore(store).Save([paired]);
            IReadOnlyList<MediaItem> items = store.All();
            var hostKey = PersistentRooms.NewKey();

            using var remote = new PhoneRemoteHost(new PhoneRemoteOptions
            {
                Server = url, HostKey = hostKey, PcName = "Check PC", AppVersion = "0.0.0-check", Store = new LibraryPhoneStore(store), Features = PhoneLibrary.Features,
            });
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            var plays = 0;
            var library = new PhoneLibrary(store, () => remote, action => dispatcher.BeginInvoke(action), CancellationToken.None)
            {
                Items = () => items, WasWatched = _ => false, SetWatched = (_, _) => { },
                Play = (_, _) => { plays++; return Task.FromResult(true); },
            };
            library.Attach(remote);
            remote.CurrentState = () => new RemoteState(false);
            remote.Start();
            for (var waited = 0; !remote.ServerReady && waited < 15000; waited += 50) await Task.Delay(50);
            Check(remote.ServerReady, "the PC connects to the local room server for phones");

            await using var phone = await FakePhone.Connect(url, remote.PcId, paired.Id, paired.Secret);
            Check(await phone.Opened() && await phone.MediaOpened(), "the stand-in phone links to the PC");
            phone.Send(new { type = "hello", protocol = 1, app = "check", device = "Check phone" });
            Check(await phone.Next("welcome", 10000) is not null, "the PC welcomes the phone");

            phone.Send(new { type = "watch", request = 1, id = "night-signal", quality = StreamQuality.OriginalId, network = "home" });
            var stream = await phone.Next("stream", 15000);
            Check(stream is { } s && s.GetProperty("resource").GetString() == "file/night-signal", "the phone starts watching the video");
            var read = await phone.Read("file/night-signal", 1000, 50000);
            Check(read.Error is null && read.Bytes.AsSpan().SequenceEqual(bytes.AsSpan(1000, 50000)), "the phone reads the video");

            // Queues: the list, a queue's videos with their places, a show's episodes, and stepping through a queue.
            phone.Send(new { type = "queues", request = 10 });
            var queues = await phone.Next("queues", 10000);
            var listed = queues?.GetProperty("queues").EnumerateArray().ToArray() ?? [];
            Check(listed.Length == 1 && listed[0].GetProperty("name").GetString() == "Evening" && listed[0].GetProperty("count").GetInt32() == 4 &&
                listed[0].GetProperty("active").GetBoolean(), "the phone lists the PC's queues, counting the videos the library still has");
            Check(listed.Length == 1 && listed[0].GetProperty("upNext").GetInt32() == 2, "a queue's Play continues at its first unwatched video, as on the PC");
            phone.Send(new { type = "browse", request = 11, queue = "evening" });
            var page = await phone.Next("page", 10000);
            var entries = page?.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("id").GetString(), e.GetProperty("index").GetInt32())).ToArray() ?? [];
            Check(entries.SequenceEqual([("harbor-3", 0), ("night-signal", 2), ("harbor-1", 3), ("harbor-3", 4)]) && page?.GetProperty("location").GetString() == "Evening",
                "a queue's page lists its videos in order with their places, skipping one the library lost");
            phone.Send(new { type = "browse", request = 12, episodesOf = "harbor-2" });
            page = await phone.Next("page", 10000);
            Check(page?.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()).SequenceEqual(["harbor-1", "harbor-2", "harbor-3"]) == true,
                "a show's episodes are listed for a video of it");
            phone.Send(new { type = "watch", request = 13, id = "harbor-1", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 3 });
            var inQueue = await phone.Next("stream", 15000);
            string? Step(System.Text.Json.JsonElement? answer, string name, string field = "id") =>
                answer is { } a && a.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Object ? a.GetProperty(name).GetProperty(field).ToString() : null;
            Check(Step(inQueue, "previous") == "night-signal" && Step(inQueue, "previous", "queueIndex") == "2" && Step(inQueue, "next") == "harbor-3" &&
                Step(inQueue, "next", "queueIndex") == "4" && Step(inQueue, "first") is null, "a video played from a queue steps to the queue's videos around it");
            Check(Step(inQueue, "queue", "index") == "3" && Step(inQueue, "queue", "name") == "Evening", "the answer says which queue and place the phone plays");
            phone.Send(new { type = "watch", request = 14, id = "harbor-3", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 4 });
            var last = await phone.Next("stream", 15000);
            Check(Step(last, "next") is null && Step(last, "first", "queueIndex") == "0", "after a queue's last video, Loop all starts it again from its first");
            phone.Send(new { type = "watch", request = 15, id = "harbor-2", quality = StreamQuality.OriginalId, network = "home", queue = "evening", queueIndex = 0 });
            var outside = await phone.Next("stream", 15000);
            Check(Step(outside, "previous") == "harbor-1" && Step(outside, "next") == "harbor-3" && Step(outside, "queue", "index") is null,
                "a video that isn't in the queue named steps through its show instead");
            // Play on the PC from a queue while BlackPearl is closed: BlackPearl opens on the queue, at the video's place.
            var (handed, place) = library.QueuePlace("evening", 3, "harbor-1");
            if (handed is not null) library.SaveQueueHandOver(handed, place);
            Check(store.Setting("activeQueue") == "evening" && store.Setting("queuePosition") == "2" &&
                store.Setting("queue") == Wire.Serialize(new[] { "harbor-3", "night-signal", "harbor-1", "harbor-3" }), "a queue handed to the PC is saved as its player's queue at the video's place");
            phone.Send(new { type = "watch", request = 16, id = "night-signal", quality = StreamQuality.OriginalId, network = "home" });
            await phone.Next("stream", 15000);

            // Continue on PC: the PC plays it, and the phone's stream has ended by the time the phone hears so.
            phone.Send(new { type = "play", request = 2, id = "night-signal", positionMs = 60000 });
            var done = await phone.Next("done", 10000);
            Check(done is { } d && d.GetProperty("request").GetInt64() == 2 && plays == 1, "Continue on PC plays the video on the PC and confirms it");
            Check((await phone.Read("file/night-signal", 0, 1000)).Error == "gone", "the phone's stream has ended once the PC confirms Continue on PC");
            Console.WriteLine($"{passed} phone library checks passed.");
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
    }
}
