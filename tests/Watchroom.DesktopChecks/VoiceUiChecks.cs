using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Concentus;
using Concentus.Enums;
using Watchroom.Core;
using Watchroom.Desktop;

// Voice chat in the real window: the host's controls, notices and indicators while a guest app joins voice,
// talks, mutes and leaves, plus push-to-talk, ducking the movie, auto-join and leaving the room while in voice.
// Voice volume is zero, so the checks are silent; they pass with or without a working microphone.
static class VoiceUiChecks
{
    sealed class Sink : IVoiceSink
    {
        public readonly ConcurrentQueue<int> Slots = new();
        public void Receive(int slot, ushort sequence, byte flags, ReadOnlySpan<byte> payload) => Slots.Enqueue(slot);
    }

    public static async Task Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void SetField(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Named<T>(string name) => (T)window.FindName(name);
        static object? Prop(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
        async Task Wait(Func<bool> condition, string label, int seconds = 25)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(40);
            check(condition(), label);
        }
        var voiceButton = Named<Button>("VoiceButton"); var voiceText = Named<TextBlock>("VoiceButtonText");
        var muteButton = Named<Button>("VoiceMuteButton"); var badge = Named<Border>("VoiceCountBadge"); var count = Named<TextBlock>("VoiceCountText");
        var card = Named<Border>("VoiceCard"); var status = Named<TextBlock>("VoiceCardStatus"); var cardJoin = Named<Button>("VoiceCardJoin");
        var problem = Named<TextBlock>("VoiceCardProblem");
        var volume = Named<Slider>("Volume");
        object? Session() => Field("voice");
        // WATCHROOM_VOICE_SCREENSHOTS=<folder> saves the voice controls at each stage for review.
        var shots = Environment.GetEnvironmentVariable("WATCHROOM_VOICE_SCREENSHOTS");
        async Task Shot(string name, params string[] elements)
        {
            if (shots is null) return;
            Directory.CreateDirectory(shots);
            await Task.Delay(150); window.UpdateLayout();
            foreach (var element in elements)
            {
                var visual = (FrameworkElement)window.FindName(element);
                if (visual.ActualWidth < 1 || visual.ActualHeight < 1) continue;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth * 2, (int)visual.ActualHeight * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                var background = new System.Windows.Media.DrawingVisual();
                using (var context = background.RenderOpen())
                {
                    var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
                    context.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x0B, 0x0E)), null, bounds);
                    // A visual brush draws the element at the origin, whatever its position in the window.
                    context.DrawRectangle(new System.Windows.Media.VisualBrush(visual), null, bounds);
                }
                bitmap.Render(background);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(shots, name + "-" + element + ".png")); encoder.Save(file);
            }
        }
        VoiceCapture Capture() => (VoiceCapture)Prop(Session()!, "Capture")!;
        VoiceAudio Audio() => (VoiceAudio)Prop(Session()!, "Audio")!;
        void Key(Key key, bool down)
        {
            var source = PresentationSource.FromVisual(window)!;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent };
            Call(down ? "KeyPressed" : "KeyReleased", window, args);
        }

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var url = "http://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var sdk = File.Exists(".tools/dotnet/dotnet.exe") ? Path.GetFullPath(".tools/dotnet/dotnet.exe") : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(sdk) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.GetFullPath("src/Watchroom.Server/bin/Release/net10.0/Watchroom.Server.dll"));
        info.ArgumentList.Add("--urls"); info.ArgumentList.Add(url);
        info.Environment["WATCHROOM_DATA"] = Path.Combine(directory, "voice-ui-server");
        using var server = Process.Start(info)!; server.BeginOutputReadLine(); server.BeginErrorReadLine();
        try
        {
            using (var http = new HttpClient())
                for (var i = 0; i < 100; i++) { try { if ((await http.GetAsync(url + "/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(50); }
            store.Setting("voiceVolume", "0"); store.Setting("voiceAutoJoin", "false"); store.Setting("voicePushToTalk", "false"); store.Setting("voiceDucking", "true");
            SetField("voicePreferences", Call("LoadVoicePreferences"));
            await (Task)Call("Disconnect", false, false)!;
            check(voiceButton.Visibility != Visibility.Visible && card.Visibility != Visibility.Visible, "voice controls stay hidden outside a room");

            // A saved microphone and output survive restarting the app, even before the device list loads.
            store.Setting("voiceInput", "{saved-microphone}"); store.Setting("voiceOutput", "{saved-output}");
            SetField("voicePreferences", Call("LoadVoicePreferences")); Call("LoadPreferences");
            var draft = Call("DraftVoicePreferences")!;
            check((string?)Prop(draft, "Input") == "{saved-microphone}" && (string?)Prop(draft, "Output") == "{saved-output}", "saved voice devices are kept when settings load");
            await (Task)Call("LoadVoiceDevicesAsync")!;
            draft = Call("DraftVoicePreferences")!;
            var inputPicker = Named<ComboBox>("VoiceInputPicker");
            check((string?)Prop(draft, "Input") == "{saved-microphone}" && ((VoiceAudio.Device)inputPicker.SelectedItem).Name == "Saved microphone (not connected)",
                "a saved microphone that is unplugged stays chosen and is labelled as not connected");
            check(!(bool)typeof(MainWindow).GetProperty("HasUnsavedPreferences", flags)!.GetValue(window)!, "loading the device list does not mark settings as changed");
            store.Setting("voiceInput", ""); store.Setting("voiceOutput", "");
            SetField("voicePreferences", Call("LoadVoicePreferences")); Call("LoadPreferences");

            // Record a test in settings: without speech nothing would be sent; with speech, what was sent plays back.
            Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Voice");
            var recordButton = Named<Button>("VoiceRecordButton"); var testStatus = Named<TextBlock>("VoiceTestStatus");
            await Wait(() => Field("voiceTest") is VoiceAudio { MicrophoneReady: true }, "the settings microphone test runs on the simulated microphone");
            Call("RecordVoiceTest", recordButton, new RoutedEventArgs());
            await Wait(() => (string)recordButton.Content == "Stop and play back" && testStatus.Text.StartsWith("Recording, ", StringComparison.Ordinal), "Record a test starts recording and says how to finish");
            await Task.Delay(300);
            Call("RecordVoiceTest", recordButton, new RoutedEventArgs());
            await Wait(() => testStatus.Text == "Nothing would have been sent: Watchroom didn't hear any speech." && (string)recordButton.Content == "Record a test",
                "a test without speech says nothing would have been sent");
            var meter = (VoiceCapture)Field("voiceTestCapture")!;
            Call("RecordVoiceTest", recordButton, new RoutedEventArgs());
            // A tone is not speech to Silero VAD, so the recording is told it is.
            meter.SpeechDetected = true;
            for (var i = 0; i < 25; i++) meter.Write(Tone(i * 960, 960, 300, 0.3f));
            Call("RecordVoiceTest", recordButton, new RoutedEventArgs());
            await Wait(() => testStatus.Text.StartsWith("Playing back what others would hear: ", StringComparison.Ordinal) && (string)recordButton.Content == "Stop playback",
                "stopping a test with speech plays back what would have been sent");
            await Wait(() => (string)recordButton.Content == "Record a test", "the playback ends and another test can be recorded");
            meter.SpeechDetected = false;
            check(!meter.Transmitting, "the settings microphone stops sending once the test ends");
            Call("ShowPage", "Room");

            var host = (RoomClient)Call("NewRoom")!;
            await host.ConnectAsync(url, "Voice Host", persistent: true);
            Call("ShowPage", "Room");
            await Wait(() => voiceButton.Visibility == Visibility.Visible && status.Text == "Talk while you watch. Nobody is in voice yet.", "a new room offers voice chat with nobody in it yet");
            check(cardJoin.IsEnabled && (string)cardJoin.Content == "Join" && badge.Visibility != Visibility.Visible, "the host can join voice from the participants panel");

            await using var guest = new RoomClient(); var sink = new Sink(); guest.VoiceOutput = sink;
            await guest.ConnectAsync(url, "Riley", host.Identity!.Room);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer), "a guest asks to join");
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(25));
            var rileyId = guest.Identity.Peer;
            guest.SetVoice(true, false);
            await Wait(() => badge.Visibility == Visibility.Visible && count.Text == "1" && status.Text == "Riley is in voice.", "before joining, the host sees who is already in voice");
            check(voiceButton.ToolTip as string == "Riley is in voice. Join to talk while you watch.", "the voice button invites the host to join the conversation");
            await Shot("1-before-join", "RoomHeader", "RoomParticipants");

            Call("ToggleVoice", voiceButton, new RoutedEventArgs());
            await Wait(() => Session() is not null && !(bool)Field("voiceJoining")! && host.VoiceState(host.Identity.Peer)?.Joined == true && guest.VoiceState(host.Identity.Peer)?.Joined == true,
                "joining from the top bar puts the host in voice for everyone");
            check(voiceText.Text == "Leave voice" && muteButton.Visibility == Visibility.Visible && (string)cardJoin.Content == "Leave", "in voice, the top bar offers a microphone switch and Leave voice");
            await Wait(() => status.Text.StartsWith("Talking with Riley.", StringComparison.Ordinal), "the panel says who the host is talking with");
            await Shot("2-in-voice", "RoomHeader", "RoomParticipants");
            // The narrowest player: minimum window width with the room panel open.
            var wide = window.Width; window.Width = 900; await Task.Delay(300); window.UpdateLayout();
            var header = Named<FrameworkElement>("RoomHeader"); var leave = Named<FrameworkElement>("LeaveButton");
            var right = leave.TransformToAncestor(header).Transform(new Point(leave.ActualWidth, 0)).X;
            check(right <= header.ActualWidth - 10 && Named<TextBlock>("VoiceButtonText").Visibility != Visibility.Visible,
                $"in the narrowest player every room action still fits, with voice shown as an icon (right edge {right:F0} of {header.ActualWidth:F0})");
            await Shot("2b-narrow", "RoomHeader");
            window.Width = wide; await Task.Delay(300);
            // Voice volume 0 means hearing nobody, which rightly never lowers the movie; use an inaudible level instead.
            ((VoiceMixer)Prop(Session()!, "Mixer")!).Volume = 0.0001f;
            var micReady = Audio().MicrophoneReady;
            Console.WriteLine(micReady ? "Microphone: " + Audio().InputName : "Microphone unavailable: " + Audio().Problem);
            check(VoiceAudio.Simulated && micReady && Audio().InputName == "Test microphone" && Audio().OutputName == "Test speakers",
                "the checks run voice on a simulated microphone and speakers, never the real devices");
            if (micReady)
            {
                check(guest.VoiceState(host.Identity.Peer)?.Muted == false && (string)muteButton.Content == "", "with a working microphone the host's microphone is on");
                var hostSlot = host.VoiceState(host.Identity.Peer)!.Slot;
                // A tone is not speech to Silero VAD (see VoiceProcessingChecks for real speech), so these writes say it is.
                Capture().SpeechDetected = true;
                for (var i = 0; i < 20; i++) Capture().Write(Tone(i * 960, 960, 300, 0.3f));
                await Wait(() => sink.Slots.Count(s => s == hostSlot) >= 15, "what the host says reaches the guest");
                Keyboard.ClearFocus();
                Key(System.Windows.Input.Key.V, true); Key(System.Windows.Input.Key.V, false);
                await Wait(() => guest.VoiceState(host.Identity.Peer)?.Muted == true && (string)muteButton.Content == "", "pressing V mutes the host for everyone");
                sink.Slots.Clear();
                for (var i = 0; i < 20; i++) Capture().Write(Tone(i * 960, 960, 300, 0.3f));
                await Task.Delay(400);
                check(sink.Slots.IsEmpty, "a muted host sends nothing while talking");
                Capture().SpeechDetected = false;
                Key(System.Windows.Input.Key.V, true); Key(System.Windows.Input.Key.V, false);
                await Wait(() => guest.VoiceState(host.Identity.Peer)?.Muted == false, "pressing V again unmutes");
            }
            else
            {
                await Wait(() => guest.VoiceState(host.Identity.Peer)?.Muted == true, "without a working microphone the host listens and others see a muted microphone");
                check(problem.Visibility == Visibility.Visible && Named<System.Windows.Documents.Run>("VoiceCardProblemText").Text.Length > 0, "the panel explains the microphone problem");
            }

            // The guest talks: their indicator lights up and the movie is lowered, then restored.
            var presence = ((System.Collections.IDictionary)Field("voicePresence")!)[rileyId]!;
            bool Speaking() => (bool)Prop(presence, "Speaking")!;
            double Duck() => (double)Field("videoDuck")!;
            var talker = Talk(guest, 1000);
            var sawSpeaking = false; var lowest = 1.0;
            var shotTaken = false;
            while (!talker.IsCompleted)
            {
                sawSpeaking |= Speaking(); lowest = Math.Min(lowest, Duck());
                if (Speaking() && !shotTaken) { shotTaken = true; await Shot("3-guest-talking", "RoomParticipants"); }
                await Task.Delay(30);
            }
            check(sawSpeaking, "the guest's avatar shows when they are talking");
            check(lowest <= 0.55, $"the movie fades to half volume while someone talks (lowest {lowest:F2})");
            await Wait(() => !Speaking() && Duck() == 1, "the indicator clears and the movie volume returns after they stop", 10);
            check((string)Prop(presence, "Description")! == "Riley is in voice" && (Visibility)Prop(presence, "LocalMuteVisibility")! == Visibility.Visible, "the guest's row offers muting them just for the host");

            Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
            check((bool)Prop(presence, "LocallyMuted")! && (string)Prop(presence, "LocalMuteHelp")! == "Unmute Riley for you", "muting the guest for yourself is shown on their row");
            talker = Talk(guest, 1000); lowest = 1; sawSpeaking = false;
            while (!talker.IsCompleted) { sawSpeaking |= Speaking(); lowest = Math.Min(lowest, Duck()); await Task.Delay(30); }
            check(sawSpeaking && lowest == 1, "someone muted for you still shows as talking but never lowers the movie");
            Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
            check(!(bool)Prop(presence, "LocallyMuted")!, "the guest can be unmuted again");

            // Each person's level and the overall voice volume apply to the mixer at once.
            var mixer = (VoiceMixer)Prop(Session()!, "Mixer")!;
            var rileySlot = host.VoiceState(rileyId)!.Slot;
            check((int)Prop(presence, "Level")! == 100 && mixer.Gain(rileySlot) == 1f, "a person starts at full level for you");
            presence.GetType().GetProperty("Level")!.SetValue(presence, 150);
            check(mixer.Gain(rileySlot) == 1.5f && (string)Prop(presence, "LevelHelp")! == "Riley's volume for you: 150%", "moving a person's slider changes only their level in the mix");
            Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
            check(mixer.Gain(rileySlot) == 0f, "muting them for you silences them whatever their level");
            Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
            check(mixer.Gain(rileySlot) == 1.5f, "unmuting restores their level");
            await Wait(() => store.Setting("voiceLevels")?.Contains("Riley") == true, "the level is remembered by name for the next room once the slider settles", 5);
            presence.GetType().GetProperty("Level")!.SetValue(presence, 100);
            var cardVolume = Named<Slider>("VoiceCardVolume"); var cardVolumeText = Named<TextBlock>("VoiceCardVolumeText");
            var settingsVolume = Named<Slider>("VoiceVolumeSlider");
            check(Named<FrameworkElement>("VoiceCardVolumeRow").Visibility == Visibility.Visible, "in voice, the participants panel shows the voice volume slider");
            cardVolume.Value = 60;
            check(Math.Abs(mixer.Volume - 0.6f) < 0.001f && cardVolumeText.Text == "60%" && settingsVolume.Value == 60
                && !(bool)typeof(MainWindow).GetProperty("HasUnsavedPreferences", flags)!.GetValue(window)!,
                $"the panel's voice volume applies at once and is mirrored in Settings without unsaved changes (mixer {mixer.Volume:F2})");
            await Wait(() => store.Setting("voiceVolume") == "60", "the voice volume is saved once the slider settles", 5);
            settingsVolume.Value = 120;
            check(Math.Abs(mixer.Volume - 1.2f) < 0.001f && store.Setting("voiceVolume") == "60", "moving the Settings slider is heard at once but only saved with the page");
            Call("LoadPreferences");
            check(Math.Abs(mixer.Volume - 0.6f) < 0.001f && settingsVolume.Value == 60, "cancelling the Settings page puts the saved voice volume back");
            mixer.Volume = 0.0001f;
            if (shots is not null)
            {
                Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
                await Shot("4-guest-muted-for-me", "RoomParticipants");
                Call("ToggleLocalVoiceMute", new Button { Tag = rileyId }, new RoutedEventArgs());
                Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Voice");
                await Task.Delay(800);
                await Shot("5-settings", "VoicePreferences");
                Call("ShowPage", "Room");
            }

            // The movie's own volume setting survives ducking.
            var before = volume.Value;
            SetField("videoDuck", 0.5); volume.Value = 40; Call("ApplyPlayerVolume");
            SetField("videoDuck", 1.0); Call("ApplyPlayerVolume");
            check(volume.Value == 40, "changing the movie volume while it is lowered keeps the new volume");
            volume.Value = before;

            // Push-to-talk: transmit only while V is held, and never stay on after switching windows.
            if (micReady)
            {
                var options = Field("voicePreferences")!;
                SetField("voicePreferences", WithPushToTalk(options, true));
                Call("ApplyVoiceTransmit"); Call("RefreshVoice");
                check(!Capture().Transmitting && status.Text.EndsWith("Hold V to talk.", StringComparison.Ordinal), "with push-to-talk on, the microphone waits for V");
                Keyboard.ClearFocus(); Key(System.Windows.Input.Key.V, true);
                check(Capture().Transmitting && status.Text.EndsWith("Sending your voice…", StringComparison.Ordinal), "holding V sends your voice");
                Key(System.Windows.Input.Key.V, false);
                check(!Capture().Transmitting, "releasing V stops sending");
                Key(System.Windows.Input.Key.V, true); Call("ReleasePushToTalk");
                check(!Capture().Transmitting, "switching to another window releases push-to-talk");
                SetField("voicePreferences", options);
                Call("ApplyVoiceTransmit");
                check(Capture().Transmitting, "turning push-to-talk off sends your voice whenever you speak again");
            }

            guest.SetVoice(false, false);
            await Wait(() => badge.Visibility != Visibility.Visible, "the voice count clears when someone leaves voice");
            Call("ToggleVoice", cardJoin, new RoutedEventArgs());
            await Wait(() => Session() is null && guest.VoiceState(host.Identity.Peer)?.Joined == false && voiceText.Text == "Voice" && muteButton.Visibility != Visibility.Visible,
                "leaving voice from the panel takes the host out of voice for everyone");

            // Auto-join on entering a room.
            store.Setting("voiceAutoJoin", "true"); SetField("voicePreferences", Call("LoadVoicePreferences"));
            await (Task)Call("Disconnect", false, false)!;
            var again = (RoomClient)Call("NewRoom")!;
            await again.ConnectAsync(url, "Voice Host", host.Identity.Room, host.Identity.HostKey);
            await Wait(() => Session() is not null && again.VoiceState(again.Identity!.Peer)?.Joined == true, "with auto-join on, entering the room joins voice");
            Call("ToggleVoice", voiceButton, new RoutedEventArgs());
            await Wait(() => Session() is null, "leaving voice after auto-join works");
            again.Send(new("room-name", Text: "Voice room renamed"));
            await Wait(() => again.Snapshot?.Name == "Voice room renamed", "the room updates after leaving voice");
            await Task.Delay(300);
            check(Session() is null, "leaving voice by hand is not undone by the next room update");
            Call("ToggleVoice", voiceButton, new RoutedEventArgs());
            await Wait(() => Session() is not null && again.VoiceState(again.Identity!.Peer)?.Joined == true, "joining again works");

            // A dropped connection that reconnects keeps the same voice session and puts the host back in voice.
            var running = Session();
            using (var retry = new CancellationTokenSource())
            {
                SetField("roomReconnect", retry);
                RoomClient reconnected;
                try
                {
                    await (Task)Call("Disconnect", false, true)!;
                    check(Session() == running, "a reconnect keeps the microphone and speakers open");
                    // The server may still hold the old host connection for a moment, as after a real drop; retry like the app does.
                    for (var attempt = 0; ; attempt++)
                    {
                        reconnected = (RoomClient)Call("NewRoom")!;
                        try { await reconnected.ConnectAsync(url, "Voice Host", host.Identity.Room, host.Identity.HostKey); break; }
                        catch (IOException) when (attempt < 40) { await (Task)Call("Disconnect", false, true)!; await Task.Delay(250); }
                    }
                }
                finally { SetField("roomReconnect", null); }
                await Wait(() => Session() == running && reconnected.VoiceState(reconnected.Identity!.Peer)?.Joined == true, "after reconnecting, the host is back in voice without rejoining");
                await Wait(() => voiceText.Text == "Leave voice" && voiceButton.Visibility == Visibility.Visible, "the voice controls stay in the in-voice state after reconnecting");
            }

            // Leaving the room while in voice ends voice and hides its controls, even if a last room update
            // arrives while the devices close and auto-join is on.
            var closing = (RoomClient)Field("room")!;
            var leaving = (Task)Call("Disconnect", false, false)!;
            Call("VoiceSnapshot", closing.Snapshot);
            await leaving;
            await Task.Delay(500);
            check(Session() is null && voiceButton.Visibility != Visibility.Visible && card.Visibility != Visibility.Visible, $"leaving the room while in voice closes voice and its controls (session {Session() is null}, button {voiceButton.Visibility}, card {card.Visibility}, room {Field("room") is null})");

            // A guest left alone after the host goes: clicking Voice says why instead of doing nothing, and voice
            // starts by itself once the host is back.
            store.Setting("voiceAutoJoin", "false"); SetField("voicePreferences", Call("LoadVoicePreferences"));
            await GuestAloneChecks(window, url, check, Wait, Field, (name, args) => Call(name, args), voiceButton, cardJoin, status, Session);

            // What guests see before the host lets them in, while the host is away, or when the host's app has no voice chat.
            var waiting = new RoomClient();
            string Reason() => (string)typeof(MainWindow).GetMethod("VoiceUnavailableReason", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [waiting])!;
            typeof(RoomClient).GetProperty(nameof(RoomClient.ServerAddress))!.SetValue(waiting, url);
            typeof(RoomClient).GetProperty(nameof(RoomClient.Identity))!.SetValue(waiting, new Welcome("ROOM", "me", false, [], false));
            typeof(RoomClient).GetProperty(nameof(RoomClient.Snapshot))!.SetValue(waiting, new RoomSnapshot([new("me", "Me", false, false)], null, null, false));
            check(Reason() == "Voice chat opens once the host lets you in.", "a guest waiting for admission is told voice opens once the host lets them in");
            typeof(RoomClient).GetField("admitted", flags)!.SetValue(waiting, true);
            check(Reason() == "Voice chat starts when the host joins the room.", $"an admitted guest without a host is told voice starts when the host joins ({Reason()})");
            typeof(RoomClient).GetField("hostPeer", flags)!.SetValue(waiting, "host");
            check(Reason() == "Connecting to the host…", $"a guest whose host is present but not yet connected is told it is connecting ({Reason()})");
            var direct = (TaskCompletionSource)typeof(RoomClient).GetField("directConnected", flags)!.GetValue(waiting)!; direct.TrySetResult();
            check(Reason() == "The host needs to update Watchroom to use voice chat.",
                "a guest whose host has an older app is told the host needs to update");
            await waiting.DisposeAsync();
        }
        finally
        {
            store.Setting("voiceVolume", "100"); store.Setting("voiceAutoJoin", "false");
            SetField("voicePreferences", Call("LoadVoicePreferences"));
            try { SetField("roomReconnect", null); await (Task)Call("Disconnect", false, false)!; }
            finally { if (!server.HasExited) server.Kill(true); }
        }
    }

    static async Task GuestAloneChecks(MainWindow window, string url, Action<bool, string> check, Func<Func<bool>, string, int, Task> wait,
        Func<string, object?> field, Func<string, object?[], object?> call, Button voiceButton, Button cardJoin, TextBlock status,
        Func<object?> session)
    {
        Task Wait(Func<bool> condition, string label) => wait(condition, label, 25);
        // Toasts are popups outside the window's tree; this reads the text of any that are open.
        static IEnumerable<string> Toasts() => PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Where(s => s.RootVisual is not null && s.RootVisual is not Window).SelectMany(s => Texts(s.RootVisual!));
        static IEnumerable<string> Texts(System.Windows.Media.Visual root)
        {
            if (root is TextBlock text) yield return text.Text;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                if (System.Windows.Media.VisualTreeHelper.GetChild(root, i) is System.Windows.Media.Visual child) foreach (var found in Texts(child)) yield return found;
        }
        var host = new RoomClient();
        RoomClient? returned = null;
        try
        {
            await host.ConnectAsync(url, "Away host", persistent: true);
            var code = host.Identity!.Room; var key = host.Identity.HostKey;
            var guest = (RoomClient)call("NewRoom", [])!;
            await guest.ConnectAsync(url, "Lonely guest", code);
            await Wait(() => host.Snapshot!.People.Any(p => p.Id == guest.Identity!.Peer), "the host sees the guest arrive");
            host.Send(new("admit", Target: guest.Identity!.Peer));
            await guest.DirectControlsReady.WaitAsync(TimeSpan.FromSeconds(30));
            host.SetVoice(true, true);
            await Wait(() => guest.VoiceState(host.Identity.Peer)?.Joined == true, "the guest sees the host in voice");
            check(session() is null, "with auto-join off, a guest is not put in voice on arrival");
            await host.DisposeAsync();
            await Wait(() => !guest.HostPresent && !guest.VoiceAvailable, "the guest notices the host has left");
            await Task.Delay(300);
            check(guest.Snapshot?.Voice is null, "once the host leaves, nobody is still listed in voice");

            check(voiceButton.Visibility == Visibility.Visible && voiceButton.IsEnabled && voiceButton.Opacity < 1,
                $"with no host the Voice button stays clickable but looks unavailable ({voiceButton.Visibility}, enabled {voiceButton.IsEnabled}, opacity {voiceButton.Opacity}, connected {guest.IsConnected}, available {guest.VoiceAvailable})");
            check(voiceButton.ToolTip as string == "Voice chat starts when the host joins the room.", $"its tooltip says voice waits for the host ({voiceButton.ToolTip})");
            call("ToggleVoice", [voiceButton, new RoutedEventArgs()]);
            await Wait(() => Toasts().Any(t => t == "Voice chat starts when the host joins the room. You'll join automatically."),
                "clicking Voice without a host shows why, wherever the click was");
            check(session() is null, "no microphone opens while there is no host");
            check(cardJoin.Content as string == "Cancel" && status.Text.EndsWith("You'll join automatically.", StringComparison.Ordinal) && (voiceButton.ToolTip as string)!.Contains("Click to cancel"),
                $"the voice controls show that voice will start when the host arrives ({cardJoin.Content}; {status.Text})");
            call("ToggleVoice", [voiceButton, new RoutedEventArgs()]);
            await Wait(() => Toasts().Any(t => t == "You won't join voice when the host arrives."), "clicking again cancels joining when the host arrives");
            check(cardJoin.Content as string == "Join" && field("voiceWaitingRoom") is null, "after cancelling, the card offers Join again");
            call("ToggleVoice", [voiceButton, new RoutedEventArgs()]);

            returned = new RoomClient();
            await returned.ConnectAsync(url, "Away host", code, key);
            await Wait(() => ((RoomClient?)field("room"))?.DirectControlsReady.IsCompletedSuccessfully == true,
                $"the guest reconnects to the returning host (same client {ReferenceEquals(field("room"), guest)}, guest connected {guest.IsConnected}, host present {guest.HostPresent}, recovering {guest.IsPeerRecovering})");
            await Wait(() => session() is not null && returned.VoiceState(guest.Identity!.Peer)?.Joined == true,
                $"when the host returns, the waiting guest joins voice by itself (session {session() is not null}, available {guest.VoiceAvailable}, waiting {field("voiceWaitingRoom")}, joining {field("voiceJoining")}, room same {ReferenceEquals(field("room"), guest)})");
            check(field("voiceWaitingRoom") is null && voiceButton.Opacity == 1, "joining clears the waiting state");
        }
        finally
        {
            await (Task)call("Disconnect", [false, false])!;
            await host.DisposeAsync();
            if (returned is not null) await returned.DisposeAsync();
        }
    }

    // The preferences record is immutable; tests copy it with one value changed.
    static object WithPushToTalk(object options, bool value)
    {
        var copy = options.GetType().GetMethod("<Clone>$")!.Invoke(options, null)!;
        copy.GetType().GetProperty("PushToTalk")!.SetValue(copy, value);
        return copy;
    }
    static async Task Talk(RoomClient guest, int milliseconds)
    {
        var capture = new VoiceCapture { Transmitting = true };
        capture.Frame += frame => guest.TransmitVoice(frame);
        for (var i = 0; i < milliseconds / 20; i++) { capture.Write(Tone(i * 960, 960, 250, 0.3f)); await Task.Delay(20); }
        capture.Write(Tone(0, 960 * 22, 250, 0));
    }
    static short[] Tone(int start, int count, double hz, float amplitude) =>
        Enumerable.Range(start, count).Select(i => (short)(Math.Sin(2 * Math.PI * hz * i / 48000) * amplitude * 32767)).ToArray();
}
