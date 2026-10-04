using System.IO;
using System.Reflection;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using Watchroom.Desktop;
using Watchroom.Core;

static class TrackPreferenceChecks
{
    public static void Run(string video)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Core.Initialize();
        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        using var media = new Media(engine, Path.GetFullPath(video), FromType.FromPath);
        var window = new MainWindow();
        void Field(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        void Wait(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < until) Thread.Sleep(25);
            if (!condition()) throw new Exception("Native track selection timed out");
        }
        var audioInput = (TextBox)window.FindName("PreferredAudioLanguages");
        var subtitlesInput = (TextBox)window.FindName("PreferredSubtitleLanguages");
        try
        {
            audioInput.Text = "Russian, English"; subtitlesInput.Text = "English, off";
            Check((bool)Call("SaveTrackPreferences")!, "ordered audio and subtitle preferences save successfully");
            audioInput.Clear(); subtitlesInput.Clear(); Call("LoadTrackPreferences");
            Check(audioInput.Text == "ru, en" && subtitlesInput.Text == "en, off", "saved language preferences reload in the same order");
            Field("player", player); Field("vlc", engine); Field("loadGeneration", 1);
            player.Play(media);
            Wait(() => player.AudioTrackDescription.Count(t => t.Id >= 0) == 2 && player.SpuDescription.Count(t => t.Id >= 0) == 2);
            Field("ready", true);
            var tracks = media.Tracks;
            var russian = tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "rus").Id;
            var english = tracks.Single(t => t.TrackType == TrackType.Audio && t.Language == "eng").Id;
            var subtitle = tracks.Single(t => t.TrackType == TrackType.Text && t.Language == "eng").Id;
            Call("RefreshTracks"); Wait(() => player.AudioTrack == russian && player.Spu == subtitle);
            Check(true, "native playback picks preferred audio and subtitles from stream language metadata");
            var picker = (ComboBox)window.FindName("AudioTracks");
            picker.SelectedItem = picker.Items.Cast<object>().Single(t => (int)t.GetType().GetProperty("Id")!.GetValue(t)! == english);
            Call("RefreshTracks"); Wait(() => player.AudioTrack == english);
            Check(true, "manual track selection survives periodic player refreshes");
            Field("loadGeneration", 2); Call("RefreshTracks"); Wait(() => player.AudioTrack == russian);
            Check(true, "a new video applies preferred languages again");
            subtitlesInput.Text = "off, English"; Call("SaveTrackPreferences");
            Field("loadGeneration", 3); Call("RefreshTracks"); Wait(() => player.Spu == -1);
            Check(true, "off preference disables native subtitles");
            audioInput.Text = "Not a language";
            Check(!(bool)Call("SaveTrackPreferences")!, "invalid settings are rejected without replacing saved preferences");
            player.Stop(); Field("player", null);
            var client = new RoomClient();
            var identity = new Welcome(new string('A', 24), "host", true, [], false);
            typeof(RoomClient).GetProperty("Identity")!.SetValue(client, identity);
            var coordinatorType = typeof(RoomClient).Assembly.GetType("Watchroom.Core.HostRoomCoordinator")!;
            var coordinator = Activator.CreateInstance(coordinatorType, "host")!;
            coordinatorType.GetMethod("Discover")!.Invoke(coordinator, [new Participant[] { new("host", "Host", true, true) }, Wire.Now]);
            typeof(RoomClient).GetField("coordinator", flags)!.SetValue(client, coordinator);
            var control = Task.Run(() => (Task)typeof(RoomClient).GetMethod("ControlLoop", flags)!.Invoke(client, null)!);
            typeof(RoomClient).GetField("controlTask", flags)!.SetValue(client, control);
            Field("room", client);
            var first = new MediaItem("first", Path.GetFullPath(video), "Watch together fixture", "Movie");
            var second = first with { Id = "second", Title = "Queue fixture" };
            try
            {
                Call("StartHostedVideo", first);
                Wait(() => client.Snapshot?.Media?.Title == first.Title && client.Snapshot.Playback?.WantsPlayback == true);
                Check(true, "Watch Together selection requests playback automatically");
                client.Send(new("ready")); Wait(() => client.Snapshot?.Playback?.Playing == true);
                Check(true, "Watch Together starts when participants become ready");
                Field("queue", new List<MediaItem> { first, second }); Field("queuePosition", 0);
                ((Task)Call("PlayQueueStep", 1)!).GetAwaiter().GetResult();
                Wait(() => client.Snapshot?.Media?.Title == second.Title && client.Snapshot.Playback?.WantsPlayback == true);
                Check(true, "queue transition selects the next video and requests playback automatically");
                client.Send(new("ready")); Wait(() => client.Snapshot?.Playback?.Playing == true);
                Check(true, "next queued video starts when participants become ready");
            }
            finally { Field("room", null); Task.Run(() => client.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
        }
        finally { Field("player", null); Field("vlc", null); Field("closing", true); window.Close(); }
    }
}
