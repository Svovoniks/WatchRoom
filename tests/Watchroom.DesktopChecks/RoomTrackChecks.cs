using System.Diagnostics;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Watchroom.Core;
using Watchroom.Desktop;

static class RoomTrackChecks
{
    public static async Task Run(string video, string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        void AddRule(MainWindow window, string audio, string subtitles)
        {
            Call(window, "AddSubtitleRule", window, new RoutedEventArgs());
            var rules = (IList)typeof(MainWindow).GetField("subtitleRuleDrafts", flags)!.GetValue(window)!;
            var rule = rules[^1]!;
            rule.GetType().GetProperty("AudioLanguage")!.SetValue(rule, audio);
            rule.GetType().GetProperty("SubtitleLanguages")!.SetValue(rule, subtitles);
        }
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        async Task Wait(Func<bool> condition, Action? refresh = null)
        {
            var deadline = Environment.TickCount64 + 20000;
            while (!condition() && Environment.TickCount64 < deadline) { refresh?.Invoke(); await Task.Delay(50); }
            if (!condition()) throw new Exception("Native room track check timed out");
        }
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url); start.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "track-server");
        using var server = Process.Start(start)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using var http = new System.Net.Http.HttpClient();
            for (int attempt = 0; ; attempt++)
            {
                try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) break; } catch (System.Net.Http.HttpRequestException) { }
                if (attempt >= 100) throw new IOException("Track fixture server did not start"); await Task.Delay(50);
            }
            await using var host = new RoomClient(); await using var guest = new RoomClient();
            await host.ConnectAsync(url, "Track Host"); await guest.ConnectAsync(url, "Track Guest", host.Identity!.Room);
            host.Send(new("admit", Target: guest.Identity!.Peer)); await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            host.PublishHostedFile(RoomClient.PrepareHostedFile(Path.GetFullPath(video), "Track fixture"), true);
            await Wait(() => guest.Snapshot?.Media is not null);
            Core.Initialize();
            using var hostEngine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
            using var guestEngine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
            using var hostPlayer = new MediaPlayer(hostEngine); using var guestPlayer = new MediaPlayer(guestEngine);
            using var hostMedia = new Media(hostEngine, Path.GetFullPath(video), FromType.FromPath);
            using var guestMedia = new Media(guestEngine, Path.GetFullPath(video), FromType.FromPath);
            hostMedia.AddOption(":sub-autodetect-file=0"); guestMedia.AddOption(":sub-autodetect-file=0");
            var hostWindow = new MainWindow(); var guestWindow = new MainWindow();
            try
            {
                foreach (var tuple in new[] { (hostWindow, hostPlayer, hostEngine, host), (guestWindow, guestPlayer, guestEngine, guest) })
                {
                    Set(tuple.Item1, "player", tuple.Item2); Set(tuple.Item1, "vlc", tuple.Item3); Set(tuple.Item1, "room", tuple.Item4);
                    Set(tuple.Item1, "loadGeneration", 1); Set(tuple.Item1, "loadedMedia", host.Snapshot!.Media!.Id);
                }
                ((TextBox)hostWindow.FindName("PreferredAudioLanguages")).Text = "Russian, English";
                ((TextBox)hostWindow.FindName("PreferredSubtitleLanguages")).Text = "English, off";
                AddRule(hostWindow, "Russian", "off"); AddRule(hostWindow, "English", "Russian, English, off"); Call(hostWindow, "SaveTrackPreferences");
                ((TextBox)guestWindow.FindName("PreferredAudioLanguages")).Text = "English, Russian";
                ((TextBox)guestWindow.FindName("PreferredSubtitleLanguages")).Text = "English, off";
                AddRule(guestWindow, "English", "off"); Call(guestWindow, "SaveTrackPreferences");
                hostPlayer.Play(hostMedia); guestPlayer.Play(guestMedia);
                await Wait(() => hostPlayer.AudioTrackDescription.Count(t => t.Id >= 0) == 2 && guestPlayer.AudioTrackDescription.Count(t => t.Id >= 0) == 2);
                Set(hostWindow, "ready", true); Set(guestWindow, "ready", true);
                void Refresh() { Call(hostWindow, "RefreshTracks"); Call(guestWindow, "RefreshTracks"); }
                var russian = hostMedia.Tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "rus").Id;
                var english = hostMedia.Tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "eng").Id;
                await Wait(() => hostPlayer.AudioTrack == russian && guestPlayer.AudioTrack == russian && guest.Snapshot?.Tracks?.Audio is not null, Refresh);
                Check(true, "guest follows host preferred audio rather than its own language preference");
                await Wait(() => hostPlayer.Spu == -1 && guestPlayer.Spu == -1 && guest.Snapshot?.Tracks?.Subtitles?.Index == -1, Refresh);
                Check(true, "guest follows the host's audio-specific subtitle rule instead of its default list");
                var picker = (ComboBox)hostWindow.FindName("AudioTracks");
                picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
                await Wait(() => guestPlayer.AudioTrack == english, Refresh);
                Check(true, "host audio track changes reach the guest native player");
                var russianSubtitles = hostMedia.Tracks.Single(track => track.TrackType == TrackType.Text && track.Language == "rus").Id;
                await Wait(() => hostPlayer.Spu == russianSubtitles && guestPlayer.Spu == russianSubtitles, Refresh);
                Check(true, "changing host audio publishes its matching subtitle choice to the guest");
                Check(!((ComboBox)guestWindow.FindName("AudioTracks")).IsEnabled, "guest track picker respects host-only controls");
                var embeddedSubtitles = (ComboBox)hostWindow.FindName("SubtitleTracks");
                var manualSubtitleChoice = embeddedSubtitles.Items.Cast<object>().First(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! >= 0);
                var manualSubtitle = (int)manualSubtitleChoice.GetType().GetProperty("Id")!.GetValue(manualSubtitleChoice)!;
                embeddedSubtitles.SelectedItem = manualSubtitleChoice;
                await Wait(() => guestPlayer.Spu == manualSubtitle && hostPlayer.Spu == manualSubtitle, Refresh);
                Check(guestPlayer.AudioTrack == english, "embedded subtitle selection synchronizes without changing room audio");
                picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == russian);
                await Wait(() => guestPlayer.AudioTrack == russian, Refresh);
                Check(hostPlayer.Spu == manualSubtitle && guestPlayer.Spu == manualSubtitle, "manual room subtitles survive host audio changes");
                picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
                await Wait(() => guestPlayer.AudioTrack == english, Refresh);
                var subtitle = Path.Combine(directory, "shared-manual.srt");
                await File.WriteAllTextAsync(subtitle, "1\n00:00:00,000 --> 00:00:20,000\nHost-loaded subtitles\n");
                var playback = host.Snapshot!.Playback;
                hostPlayer.SetPause(true); guestPlayer.SetPause(true);
                host.ShareSubtitle(subtitle);
                await Wait(() => guest.Snapshot?.Tracks?.Subtitles?.AssetId is not null);
                var assetId = guest.Snapshot!.Tracks!.Subtitles!.AssetId!;
                var source = await guest.GetSourceAsync(guest.Snapshot.Media!.Subtitles!.Single(s => s.Id == assetId), default);
                var bytes = await source.ReadAsync(0, (int)source.Media.Length, default);
                var expectedBytes = await File.ReadAllBytesAsync(subtitle);
                Check(bytes.SequenceEqual(expectedBytes), "host-loaded subtitle file transfers exact bytes over the peer connection");
                Dictionary<string, int> Ids(MainWindow window) => (Dictionary<string, int>)typeof(MainWindow).GetField("roomSubtitleIds", flags)!.GetValue(window)!;
                await Wait(() => Ids(hostWindow).ContainsKey(assetId) && Ids(guestWindow).ContainsKey(assetId) &&
                    hostPlayer.Spu == Ids(hostWindow)[assetId] && guestPlayer.Spu == Ids(guestWindow)[assetId], Refresh);
                Check(true, "host and guest attach and select the same external subtitles without restarting video");
                Check(((ComboBox)guestWindow.FindName("SubtitleTracks")).Items.Cast<object>().Any(t =>
                    (string)t.GetType().GetProperty("Name")!.GetValue(t)! == "shared-manual.srt"), "guest subtitle picker shows the host file name");
                Check(host.Snapshot.Playback == playback, "external subtitle loading preserves shared playback state");
                Check(!hostPlayer.IsPlaying && !guestPlayer.IsPlaying, "loading shared subtitles keeps paused players paused");
                host.Send(new("controls", Number: 1)); await Wait(() => guest.Snapshot?.SharedControls == true);
                Refresh(); var guestPicker = (ComboBox)guestWindow.FindName("SubtitleTracks");
                guestPicker.SelectedItem = guestPicker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == -1);
                await Wait(() => hostPlayer.Spu == -1 && host.Snapshot?.Tracks?.Subtitles?.Index == -1, Refresh);
                Check(true, "shared-control guest subtitle choice updates the host and supports subtitles off");
                await using var late = new RoomClient(); await late.ConnectAsync(url, "Late Guest", host.Identity.Room);
                host.Send(new("admit", Target: late.Identity!.Peer)); await late.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
                Check(late.Snapshot?.Media?.Subtitles?.Any(s => s.Id == assetId) == true && late.Snapshot.Tracks?.Subtitles?.Index == -1,
                    "late guest receives attached subtitles and the current room selection");
            }
            finally
            {
                foreach (var window in new[] { hostWindow, guestWindow })
                {
                    Call(window, "ResetRoomTracks"); Set(window, "room", null); Set(window, "player", null); Set(window, "vlc", null); Set(window, "closing", true); window.Close();
                }
                await Task.WhenAll(Task.Run(hostPlayer.Stop), Task.Run(guestPlayer.Stop));
            }
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); }
    }
}
