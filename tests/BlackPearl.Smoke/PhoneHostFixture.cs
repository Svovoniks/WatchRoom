using System.Diagnostics;
using System.Text.Json;
using BlackPearl.Core;

// The phone host (src/BlackPearl.Desktop/PhoneHost.cs) end to end: the desktop app's own executable, started as BlackPearl
// starts it when it closes, serves a paired phone through a local room server. The phone browses the library, reads the
// video, saves how far it got, and is told the remote has nothing to control. The host ends once BlackPearl opens the
// library, and doesn't start while it is open. Uses the Release build of the desktop app, which the test scripts build first.
static class PhoneHostFixture
{
    public static async Task Run(string url, string directory, Action<bool, string> check)
    {
        var exe = Path.GetFullPath(Path.Combine("src", "BlackPearl.Desktop", "bin", "Release", "net10.0-windows10.0.17763.0", "BlackPearl.exe"));
        check(File.Exists(exe), "the desktop app's Release build is there for the phone host checks");
        if (!File.Exists(exe)) return;

        var data = Path.Combine(directory, "phone-host-library");
        var videos = Path.Combine(data, "videos");
        Directory.CreateDirectory(videos);
        var path = Path.Combine(videos, "Night Signal (2024).mkv");
        var bytes = Enumerable.Range(0, 300000).Select(i => (byte)(i * 7 % 253)).ToArray();
        File.WriteAllBytes(path, bytes);
        var hostKey = PersistentRooms.NewKey();
        var pcId = PhoneRemoteProtocol.PcId(hostKey);
        var phone = new PairedPhone(PhoneRemoteProtocol.NewHex(16), "Smoke phone", PhoneRemoteProtocol.NewHex(32), Wire.Now, Wire.Now);
        var id = "night-signal";
        {
            var store = new LibraryStore(data);
            store.Save(new MediaItem(id, path, "Night Signal", "Movie", 2024, RuntimeMinutes: 105));
            store.SetSettings([
                new("server", url), new("phoneRemote", "on"), new("phoneRemoteHostKey", hostKey), new("phoneRemotePhones", Wire.Serialize(new[] { phone })),
                new("folders", Wire.Serialize(new[] { new LibraryFolder(videos, "Movies") })),
                // Not the first run's notice beside the icon: the checks shouldn't pop one up on the desktop.
                new("phoneHostIntroduced", "1"), new("phoneRemoteCanStart", "off"),
            ]);
        }
        // The app's instance mutex for this library, which BlackPearl holds while it has the library open.
        var appMutex = @"Local\Watchroom-" + LibraryIdentity.Hash(Path.GetFullPath(data).ToUpperInvariant());

        Process Start()
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var argument in new[] { "--phone-host", "--data-dir", data }) start.ArgumentList.Add(argument);
            // A dev launch's switch must not leak into the check.
            start.Environment.Remove("BLACKPEARL_PHONE_HOST"); start.Environment.Remove("BLACKPEARL_DATA");
            return Process.Start(start)!;
        }

        using (var open = new Mutex(true, appMutex))
        {
            using var refused = Start();
            check(refused.WaitForExit(15000), "the phone host doesn't start while BlackPearl has the library open");
            open.ReleaseMutex();
        }

        using var host = Start();
        try
        {
            await using var link = await Connect(url, pcId, phone, check);
            if (link is null) return;
            link.Send(new { type = "hello", protocol = 1, app = "smoke", device = "Smoke phone" });
            var welcome = await link.Next("welcome", 10000);
            check(welcome is { } w && w.GetProperty("features").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["library", "watch", "queues"]) && !w.GetProperty("canStart").GetBoolean(),
                "the phone host welcomes the phone with the library, watching and queues, and the PC's settings");
            check(await link.Next("state", 5000) is { } state && !state.GetProperty("media").GetBoolean(), "the phone host says nothing is playing");
            link.Send(new { type = "command", action = "toggle" });
            check(await link.Next("error", 5000) is { } nothing && nothing.GetProperty("code").GetString() == "nothing-playing" &&
                nothing.GetProperty("message").GetString()!.Contains("isn't open", StringComparison.Ordinal), "the remote's buttons say BlackPearl isn't open on the PC");

            link.Send(new { type = "browse", request = 1, category = 0, page = 0 });
            var page = await link.Next("page", 15000);
            check(page is { } p && p.GetProperty("entries").EnumerateArray().Any(e => e.GetProperty("id").GetString() == id && e.GetProperty("playable").GetBoolean()),
                "a phone browses the library while BlackPearl is closed");

            link.Send(new { type = "watch", request = 2, id, quality = "original", network = "home" });
            var stream = await link.Next("stream", 20000);
            check(stream is { } s && s.GetProperty("kind").GetString() == "file" && s.GetProperty("length").GetInt64() == bytes.Length, "a phone starts watching a video while BlackPearl is closed");
            if (stream is { } watching && await link.MediaOpened())
            {
                var read = await link.Read(watching.GetProperty("resource").GetString()!, 1000, 50000);
                check(read.Error is null && read.Length == bytes.Length && read.Bytes.SequenceEqual(bytes[1000..51000]), "the phone reads the video's bytes from the phone host");
            }

            link.Send(new { type = "progress", id, positionMs = 754000, durationMs = 6300000, final = true });
            var store = new LibraryStore(data);
            var saved = false;
            for (var waited = 0; waited < 5000 && !saved; waited += 100) { saved = store.Setting("position:" + id) == "754000" && store.Setting("played:" + id) is not null; if (!saved) await Task.Delay(100); }
            check(saved, "progress made on the phone while BlackPearl is closed is saved to the library");

            link.Send(new { type = "play", request = 3, id, positionMs = 1000 });
            check(await link.Next("error", 5000) is { } refusedPlay && refusedPlay.GetProperty("code").GetString() == "not-allowed",
                "Continue on PC from the phone host follows the PC's setting for phones starting videos");

            // BlackPearl opens the library, then connects for phones itself. The host keeps the phone's link until the room
            // server has handed the PC's id to the app, so the phone isn't left with neither, and then ends.
            // Held to the end and closed, not released: the awaits below can resume on another thread.
            using var app = new Mutex(true, appMutex);
            check(!host.WaitForExit(1500) && !await link.Closed(10), "the phone host keeps the phone's link while BlackPearl is still connecting");
            using var appLink = new PhoneRemoteHost(new PhoneRemoteOptions { Server = url, HostKey = hostKey, PcName = "Test PC", AppVersion = "0.0.0-test", Store = new SettingsStore(store) });
            appLink.Start();
            check(host.WaitForExit(5000), "the phone host ends once BlackPearl has taken the phones over");
            check(await link.Closed(10000), "the phone's link to the phone host closes when it ends, for BlackPearl to take the phone over");
        }
        finally { if (!host.HasExited) { host.Kill(); host.WaitForExit(5000); } }
    }

    // The host needs a moment to start and reach the room server; until then the phone's offer goes unanswered.
    static async Task<FakePhone?> Connect(string url, string pcId, PairedPhone phone, Action<bool, string> check)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var link = await FakePhone.Connect(url, pcId, phone.Id, phone.Secret);
            if (await link.Opened(5000)) { check(true, "a paired phone connects to the phone host"); return link; }
            await link.DisposeAsync();
        }
        check(false, "a paired phone connects to the phone host");
        return null;
    }

    sealed class SettingsStore(LibraryStore library) : IPairedPhoneStore
    {
        public IReadOnlyList<PairedPhone> Load() => Wire.Read<List<PairedPhone>>(library.Setting("phoneRemotePhones") ?? "[]");
        public void Save(IReadOnlyList<PairedPhone> phones) => library.Setting("phoneRemotePhones", Wire.Serialize(phones));
    }
}