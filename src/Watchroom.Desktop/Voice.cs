using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Room voice chat: joining and leaving, the microphone switch and push-to-talk, who is talking,
// and lowering the movie while people speak.
public partial class MainWindow
{
    private sealed class VoiceSession(VoiceCapture capture, VoiceMixer mixer, VoiceAudio audio)
    {
        public VoiceCapture Capture => capture;
        public VoiceMixer Mixer => mixer;
        public VoiceAudio Audio => audio;
        public Stopwatch Started { get; } = Stopwatch.StartNew();
        // The microphone and speakers have opened; joining then only waits for the host's confirmation.
        public bool DevicesReady { get; set; }
    }
    private sealed record VoiceOptions(string? Input, string? Output, VoiceAudio.Processing Processing, bool PushToTalk, bool AlwaysOn, bool Ducking, bool AutoJoin, int Volume)
    {
        public VoiceAudio.Settings Devices => new(Input, Output, Processing);
    }
    private VoiceSession? voice;
    private VoiceOptions voicePreferences = new(null, null, VoiceAudio.Processing.Windows, false, false, true, false, 100);
    private bool voiceJoining, voiceMuted, pushToTalkHeld;
    // Each person's level for you, by their connection; remembered by name for the next room.
    private readonly Dictionary<string, int> voiceLevels = [];
    private Dictionary<string, int> rememberedVoiceLevels = [];
    // Capture frames go to whichever room connection is current, including after a reconnect.
    private RoomClient? voiceRoom;
    // The room code voice was automatically joined in, so leaving voice by hand sticks through reconnects.
    private string? voiceAutoJoinedRoom;
    // The room code where Voice was clicked before the host was there; voice starts once the host connects.
    private string? voiceWaitingRoom;
    // The room connection being closed; its last updates must not start voice again.
    private RoomClient? voiceClosingRoom;
    private readonly DispatcherTimer voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<string, VoicePresence> voicePresence = [];
    private readonly HashSet<string> voiceLocallyMuted = [];
    private HashSet<string>? voiceRoster;
    private double videoDuck = 1;

    private static readonly SolidColorBrush VoiceTalking = Frozen("#3FB97A"), VoiceOff = Frozen("#F0616D"), VoiceIdle = Frozen("#9A9CAB");
    private static SolidColorBrush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }

    // Binds a participant row to their voice state; rows keep the same instance so talking updates do not rebuild the list.
    private sealed class VoicePresence : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public bool InVoice { get; private set; }
        public bool Muted { get; private set; }
        public bool Speaking { get; private set; }
        public bool Me { get; private set; }
        public bool LocallyMuted { get; private set; }
        public bool Listening { get; private set; }
        public Visibility Visibility => InVoice ? Visibility.Visible : Visibility.Collapsed;
        public string Icon => Muted ? "" : "";
        public Brush IconBrush => Muted ? VoiceOff : Speaking ? VoiceTalking : VoiceIdle;
        public Brush Ring => InVoice && Speaking ? VoiceTalking : Brushes.Transparent;
        public string Description => !InVoice ? "" : Me ? (Muted ? "You are in voice with your microphone off" : Speaking ? "You are talking" : "You are in voice")
            : Muted ? Name + " is in voice with their microphone off" : Speaking ? Name + " is talking" : Name + " is in voice";
        public Visibility LocalMuteVisibility => InVoice && !Me && Listening ? Visibility.Visible : Visibility.Collapsed;
        public string LocalMuteIcon => LocallyMuted ? "" : "";
        public string LocalMuteHelp => LocallyMuted ? "Unmute " + Name + " for you" : "Mute " + Name + " for you";
        // This person's volume for you, 0 to 200%; the slider binds both ways and the window applies changes.
        public string Id { get; set; } = "";
        public Action<VoicePresence>? LevelChanged { get; set; }
        private int level = 100;
        public int Level
        {
            get => level;
            set
            {
                var clamped = Math.Clamp(value, 0, 200);
                if (level == clamped) return;
                level = clamped;
                PropertyChanged?.Invoke(this, new(nameof(Level))); PropertyChanged?.Invoke(this, new(nameof(LevelHelp)));
                LevelChanged?.Invoke(this);
            }
        }
        public string LevelHelp => Name + "'s volume for you: " + level + "%";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(bool inVoice, bool muted, bool me, bool locallyMuted, bool listening, int level)
        {
            var levelChanged = this.level != Math.Clamp(level, 0, 200);
            this.level = Math.Clamp(level, 0, 200);
            if (!levelChanged && InVoice == inVoice && Muted == muted && Me == me && LocallyMuted == locallyMuted && Listening == listening) return;
            InVoice = inVoice; Muted = muted; Me = me; LocallyMuted = locallyMuted; Listening = listening;
            if (!inVoice) Speaking = false;
            PropertyChanged?.Invoke(this, new(string.Empty));
        }
        public void SetSpeaking(bool speaking)
        {
            if (Speaking == speaking) return;
            Speaking = speaking; PropertyChanged?.Invoke(this, new(string.Empty));
        }
    }
    private VoicePresence PresenceFor(string id, string name)
    {
        if (!voicePresence.TryGetValue(id, out var presence))
        {
            voicePresence[id] = presence = new() { Id = id, LevelChanged = ParticipantVoiceLevelChanged };
            // A friend's level from an earlier room, matched by name since connections get new ids each time.
            if (!voiceLevels.ContainsKey(id) && rememberedVoiceLevels.TryGetValue(name, out var remembered)) voiceLevels[id] = remembered;
        }
        presence.Name = name;
        return presence;
    }
    private int VoiceLevel(string id) => voiceLevels.GetValueOrDefault(id, 100);
    private void ParticipantVoiceLevelChanged(VoicePresence presence)
    {
        voiceLevels[presence.Id] = presence.Level;
        if (presence.Name.Length > 0)
        {
            rememberedVoiceLevels[presence.Name] = presence.Level;
            // Keep the list short: the most recent fifty names.
            if (rememberedVoiceLevels.Count > 50) rememberedVoiceLevels.Remove(rememberedVoiceLevels.Keys.First());
            voiceLevelsChanged = true; voiceSettingsSave.Stop(); voiceSettingsSave.Start();
        }
        ApplyVoiceGains();
    }
    // Sliders change many times a second while dragged; their values are written once they settle.
    private readonly DispatcherTimer voiceSettingsSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool voiceLevelsChanged, voiceVolumeChanged;
    private void SaveVoiceSliders()
    {
        voiceSettingsSave.Stop();
        if (voiceLevelsChanged) { voiceLevelsChanged = false; library.Setting("voiceLevels", Wire.Serialize(rememberedVoiceLevels)); }
        if (voiceVolumeChanged) { voiceVolumeChanged = false; library.Setting("voiceVolume", voicePreferences.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    }
    // Everyone's level in the mixer, by their current slot: muted for you is zero, otherwise their own level.
    private void ApplyVoiceGains()
    {
        if (voice is not { } session) return;
        foreach (var member in room?.Snapshot?.Voice ?? [])
            session.Mixer.SetGain(member.Slot, voiceLocallyMuted.Contains(member.Peer) ? 0 : VoiceLevel(member.Peer) / 100f);
    }

    private void InitializeVoice()
    {
        voicePreferences = LoadVoicePreferences();
        try { rememberedVoiceLevels = library.Setting("voiceLevels") is { Length: > 0 } saved ? Wire.Read<Dictionary<string, int>>(saved) : []; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.IO.InvalidDataException) { rememberedVoiceLevels = []; }
        voiceTimer.Tick += (_, _) => VoiceTick();
        voiceSettingsSave.Tick += (_, _) => SaveVoiceSliders();
        voiceTestTimer.Tick += (_, _) => UpdateVoiceTest();
        Deactivated += (_, _) => ReleasePushToTalk();
        RefreshVoice();
    }
    private VoiceOptions LoadVoicePreferences()
    {
        static string? Device(string? id) => string.IsNullOrEmpty(id) ? null : id;
        // Apps before the processing choice stored only whether Windows echo cancellation was on.
        var processing = library.Setting("voiceProcessing") switch
        {
            "standard" => VoiceAudio.Processing.Standard, "raw" => VoiceAudio.Processing.Raw, "windows" => VoiceAudio.Processing.Windows,
            _ => library.Setting("voiceEcho") == "false" ? VoiceAudio.Processing.Standard : VoiceAudio.Processing.Windows
        };
        return new(Device(library.Setting("voiceInput")), Device(library.Setting("voiceOutput")), processing,
            library.Setting("voicePushToTalk") == "true", library.Setting("voiceAlwaysOn") == "true", library.Setting("voiceDucking") != "false", library.Setting("voiceAutoJoin") == "true",
            int.TryParse(library.Setting("voiceVolume"), out var volume) ? Math.Clamp(volume, 0, 200) : 100);
    }

    private async void ToggleVoice(object sender, RoutedEventArgs e) => await Guard(() => voice is null ? JoinVoiceAsync() : LeaveVoiceAsync());
    private async Task JoinVoiceAsync()
    {
        if (voice is not null || voiceJoining || room is not { } client) return;
        // The player covers the rooms page, so the reason appears where the button was clicked.
        if (!client.VoiceAvailable)
        {
            // Waiting for the host: remember the click and join when they connect; a second click cancels.
            var waitable = client.Identity?.Host != true && !client.DirectControlsReady.IsCompletedSuccessfully && client.Identity?.Room is not null;
            var cancelling = waitable && voiceWaitingRoom == client.Identity!.Room;
            voiceWaitingRoom = waitable && !cancelling ? client.Identity!.Room : null;
            VoiceToast(cancelling ? "You won't join voice when the host arrives." : VoiceUnavailableReason(client) + (waitable ? " You'll join automatically." : ""), 4);
            RefreshVoice(); return;
        }
        voiceWaitingRoom = null;
        var capture = new VoiceCapture();
        var mixer = new VoiceMixer { Volume = voicePreferences.Volume / 100f };
        var audio = new VoiceAudio(capture, mixer, voicePreferences.Devices);
        var session = new VoiceSession(capture, mixer, audio);
        capture.Frame += frame => Volatile.Read(ref voiceRoom)?.TransmitVoice(frame);
        audio.Changed += () => Dispatcher.BeginInvoke(() => { if (voice == session) { ApplyVoiceTransmit(); RefreshVoice(); } });
        voiceJoining = true; voice = session; voiceMuted = false;
        // Tell the host at once, with the microphone shown muted until it opens: the room hears the join and this
        // app starts receiving everyone while Windows is still starting the devices.
        AttachVoice();
        RefreshVoice();
        try { await audio.StartAsync(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (voice == session)
            {
                voice = null; voiceJoining = false; Volatile.Write(ref voiceRoom, null);
                if (room is { } failed) { failed.VoiceOutput = null; failed.SetVoice(false, false); }
            }
            await audio.DisposeAsync();
            VoiceToast("Windows audio isn't working right now, so voice chat could not start. Restarting your PC usually fixes it.", 7);
            RefreshVoice();
            return;
        }
        // Leaving voice or the room while devices were opening ends this session; LeaveVoiceAsync has already cleaned up.
        if (voice != session) { await audio.DisposeAsync(); return; }
        var current = room;
        if (current is null || current == voiceClosingRoom)
        {
            voice = null; voiceJoining = false; Volatile.Write(ref voiceRoom, null);
            foreach (var stale in new[] { client, current }) if (stale is not null) { stale.VoiceOutput = null; stale.SetVoice(false, false); }
            RefreshVoice(); await audio.DisposeAsync(); return;
        }
        session.DevicesReady = true;
        PlaybackDiagnostics.Record("voice-join-devices", new { ms = session.Started.ElapsedMilliseconds, confirmed = VoiceConfirmed(current), problem = audio.Problem });
        // The room may have reconnected while the devices opened; voice continues on the new connection.
        AttachVoice();
        mixer.Cue(VoiceCues.Join);
        voiceTimer.Start();
        FinishJoining();
        RefreshVoice();
    }
    // The host's roster lists this app as joined.
    private bool VoiceConfirmed(RoomClient? client) => client?.Identity?.Peer is { } me && client.VoiceState(me)?.Joined == true;
    // Joining ends once the devices are open and the host has confirmed the join, so "Joining…" tells the truth.
    private void FinishJoining()
    {
        if (!voiceJoining || voice is not { } session || !session.DevicesReady) return;
        // After a long wait the card's own status explains what the host connection is doing instead.
        if (!VoiceConfirmed(room) && session.Started.ElapsedMilliseconds < 10000) return;
        voiceJoining = false;
        PlaybackDiagnostics.Record("voice-joined", new { ms = session.Started.ElapsedMilliseconds, confirmed = VoiceConfirmed(room) });
        var audio = session.Audio;
        VoiceToast(audio.Problem ?? (voicePreferences.PushToTalk ? "You joined voice. Hold V to talk." : "You joined voice. Press V to mute or unmute."), audio.Problem is null ? 3 : 7);
    }
    private async Task LeaveVoiceAsync()
    {
        if (voice is not { } session) return;
        voice = null; pushToTalkHeld = false; voiceJoining = false;
        if (voiceSettingsSave.IsEnabled) SaveVoiceSliders();
        Volatile.Write(ref voiceRoom, null);
        if (room is { } client) { client.VoiceOutput = null; client.SetVoice(false, false); }
        voiceTimer.Stop(); videoDuck = 1; ApplyPlayerVolume();
        foreach (var presence in voicePresence.Values) presence.SetSpeaking(false);
        RefreshVoice();
        await session.Audio.DisposeAsync();
    }
    // Connects the running voice session to the current room connection.
    private void AttachVoice()
    {
        var client = room;
        Volatile.Write(ref voiceRoom, client);
        if (client is null || voice is not { } session) return;
        client.VoiceOutput = session.Mixer;
        ApplyVoiceTransmit();
    }
    private void ApplyVoiceTransmit()
    {
        if (voice is not { } session) return;
        var microphoneOn = !voiceMuted && session.Audio.MicrophoneReady;
        session.Capture.PushToTalk = voicePreferences.PushToTalk;
        // Push-to-talk already sends everything while the key is held.
        session.Capture.AlwaysOn = voicePreferences.AlwaysOn && !voicePreferences.PushToTalk;
        session.Capture.Transmitting = microphoneOn && (!voicePreferences.PushToTalk || pushToTalkHeld);
        // Others see a muted microphone when there is none, so nobody waits to hear them.
        room?.SetVoice(true, !microphoneOn);
    }
    private void ToggleVoiceMute(object sender, RoutedEventArgs e) => ToggleVoiceMute();
    private void ToggleVoiceMute()
    {
        if (voice is not { } session) return;
        if (!session.Audio.MicrophoneReady) { VoiceToast(session.Audio.Problem ?? "No microphone is available.", 5); return; }
        voiceMuted = !voiceMuted;
        ApplyVoiceTransmit(); RefreshVoice();
        // The player controls hide while watching, so V gets visible confirmation.
        VoiceToast(voiceMuted ? "Microphone muted" : "Microphone on", 1.5);
    }
    // Voice messages appear as toasts; the player's status chip stays about the room connection.
    private Action? voiceToast;
    private void VoiceToast(string text, double seconds)
    {
        voiceToast?.Invoke();
        voiceToast = InAppDialog.Toast(this, text, TimeSpan.FromSeconds(seconds));
    }
    private void ToggleLocalVoiceMute(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not string id) return;
        if (!voiceLocallyMuted.Remove(id)) voiceLocallyMuted.Add(id);
        RefreshVoice();
    }
    private void OpenMicrophonePrivacy(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { SetStatus("Open Windows Settings › Privacy & security › Microphone."); }
        e.Handled = true;
    }
    // V mutes and unmutes, or with push-to-talk transmits while held. Typing in a text box is never captured.
    private bool VoiceKey(KeyEventArgs e, bool down)
    {
        if (e.Key != Key.V || voice is null || Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBox or PasswordBox) return false;
        if (voicePreferences.PushToTalk) { if (pushToTalkHeld != down) { pushToTalkHeld = down; ApplyVoiceTransmit(); RefreshVoice(); } }
        else if (down && !e.IsRepeat) ToggleVoiceMute();
        e.Handled = true;
        return true;
    }
    private void KeyReleased(object sender, KeyEventArgs e) { if (!e.Handled) VoiceKey(e, false); }
    private void ReleasePushToTalk()
    {
        if (!pushToTalkHeld) return;
        pushToTalkHeld = false; ApplyVoiceTransmit(); RefreshVoice();
    }

    private static string VoiceUnavailableReason(RoomClient client) =>
        !client.IsConnected ? "Connect to a room to use voice chat."
        : client.Identity?.Host != true && !client.DirectControlsReady.IsCompletedSuccessfully ?
            (!client.IsAdmitted ? "Voice chat opens once the host lets you in."
            // Voice travels through the host, so it waits for them.
            : !client.HostPresent ? "Voice chat starts when the host joins the room."
            : client.IsPeerRecovering ? "Reconnecting to the host…" : "Connecting to the host…")
        : "The host needs to update Watchroom to use voice chat.";
    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "", 1 => names[0], 2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
    };
    private void RefreshVoice()
    {
        if (VoiceButton is null) return;
        var client = room;
        var connected = client?.IsConnected == true;
        var me = client?.Identity?.Peer;
        var snapshot = client?.Snapshot;
        var members = snapshot?.Voice ?? [];
        string NameOf(string id) => snapshot?.People.FirstOrDefault(p => p.Id == id)?.Name ?? "Someone";
        var others = members.Where(v => v.Joined && v.Peer != me).Select(v => NameOf(v.Peer)).ToArray();
        var inVoice = voice is not null;
        var microphoneOn = inVoice && !voiceMuted && voice!.Audio.MicrophoneReady;

        VoiceButton.Visibility = VoiceCard.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        VoiceButtonText.Text = voiceJoining ? "Joining…" : inVoice ? "Leave voice" : "Voice";
        var joiningStage = !voiceJoining ? "" : voice?.DevicesReady != true ? "Opening your microphone and speakers…" : "Waiting for the host to confirm…";
        VoiceButtonIcon.Text = inVoice ? "" : "";
        var action = voiceJoining ? "Cancel joining voice chat" : inVoice ? "Leave voice chat" : "Join voice chat";
        // Still clickable while unavailable, so a click explains why instead of doing nothing.
        var unavailable = connected && !inVoice && !voiceJoining && !client!.VoiceAvailable;
        VoiceButton.Opacity = unavailable ? 0.55 : 1;
        var waiting = unavailable && voiceWaitingRoom is not null && voiceWaitingRoom == client!.Identity?.Room;
        VoiceButton.ToolTip = waiting ? VoiceUnavailableReason(client!) + " You'll join automatically. Click to cancel."
            : unavailable ? VoiceUnavailableReason(client!) : voiceJoining ? joiningStage + " Click to cancel." : inVoice ? action : others.Length > 0 ? Names(others) + (others.Length == 1 ? " is" : " are") + " in voice. Join to talk while you watch." : "Join voice chat to talk while you watch";
        System.Windows.Automation.AutomationProperties.SetName(VoiceButton, action);
        // Joining can be cancelled, so the buttons stay enabled throughout.
        VoiceButton.IsEnabled = VoiceCardJoin.IsEnabled = connected;
        VoiceCountBadge.Visibility = others.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VoiceCountText.Text = others.Length.ToString();
        var muteHelp = !inVoice ? "" : voice!.Audio.MicrophoneReady ? (voiceMuted ? "Unmute microphone (V)" : "Mute microphone (V)") : voice.Audio.Problem ?? "No microphone";
        foreach (var button in new[] { VoiceMuteButton, VoiceCardMute })
        {
            button.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
            button.Content = microphoneOn ? "" : "";
            button.ToolTip = muteHelp;
            System.Windows.Automation.AutomationProperties.SetName(button, muteHelp);
            if (microphoneOn) button.ClearValue(ForegroundProperty); else button.Foreground = VoiceOff;
        }
        VoiceCardJoin.Content = voiceJoining ? "Cancel" : inVoice ? "Leave" : waiting ? "Cancel" : "Join";
        // The accent invites joining; leaving is a quieter choice.
        VoiceCardJoin.Style = (Style)FindResource(inVoice || waiting ? "QuietButton" : "Primary");
        System.Windows.Automation.AutomationProperties.SetName(VoiceCardJoin, action);
        // The overall voice volume sits with the voice controls while listening; it applies at once and is kept.
        VoiceCardVolumeRow.Visibility = inVoice ? Visibility.Visible : Visibility.Collapsed;
        if (inVoice && Math.Abs(VoiceCardVolume.Value - voicePreferences.Volume) >= 0.5) { syncingVoiceVolume = true; try { VoiceCardVolume.Value = voicePreferences.Volume; } finally { syncingVoiceVolume = false; } }
        VoiceCardVolumeText.Text = voicePreferences.Volume + "%";
        VoiceCardIcon.Text = inVoice ? (microphoneOn ? "" : "") : "";
        VoiceCardStatus.Text = !connected ? ""
            // In voice while the host is away: say what voice is waiting for, not that you're alone.
            : inVoice && !client!.VoiceAvailable ? VoiceUnavailableReason(client)
            : voiceJoining ? joiningStage
            : inVoice ? (others.Length == 0 ? "You're the only one in voice. Others hear you when they join." : "Talking with " + Names(others) + ".")
                + (!voice!.Audio.MicrophoneReady ? "" : voicePreferences.PushToTalk ? (pushToTalkHeld && !voiceMuted ? " Sending your voice…" : " Hold V to talk.") : voiceMuted ? " Your microphone is off." : "")
            : !client!.VoiceAvailable ? VoiceUnavailableReason(client) + (waiting ? " You'll join automatically." : "")
            : others.Length == 0 ? "Talk while you watch. Nobody is in voice yet." : Names(others) + (others.Length == 1 ? " is" : " are") + " in voice.";
        VoiceLevelTrack.Visibility = microphoneOn ? Visibility.Visible : Visibility.Collapsed;
        var problem = inVoice ? voice!.Audio.Problem : null;
        VoiceCardProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        VoiceCardProblemText.Text = problem ?? "";
        // The privacy link only helps when Windows itself blocks the microphone.
        VoicePrivacyLinkText.Text = voice?.Audio.MicrophoneBlocked == true ? " Open settings" : "";
        foreach (var (id, presence) in voicePresence)
        {
            var member = members.FirstOrDefault(v => v.Peer == id);
            var mine = id == me;
            presence.Update(member?.Joined == true || mine && inVoice, mine && inVoice ? !microphoneOn : member?.Muted == true, mine, voiceLocallyMuted.Contains(id), inVoice, VoiceLevel(id));
        }
        ApplyVoiceGains();
    }
    private bool syncingVoiceVolume;
    // The slider in the participants panel: applied to the mixer at once, saved, and mirrored in Settings.
    private void VoiceCardVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingVoiceVolume || VoiceCardVolumeText is null) return;
        SetVoiceVolume((int)Math.Round(e.NewValue));
    }
    private void SetVoiceVolume(int volume)
    {
        volume = Math.Clamp(volume, 0, 200);
        if (voicePreferences.Volume != volume)
        {
            voicePreferences = voicePreferences with { Volume = volume };
            voiceVolumeChanged = true; voiceSettingsSave.Stop(); voiceSettingsSave.Start();
            // Settings shows the same value without marking the page as changed by the user.
            var dirty = HasUnsavedPreferences;
            loadingPreferences = true;
            try { VoiceVolumeSlider.Value = volume; } finally { loadingPreferences = false; }
            if (!dirty) preferencesBaseline = PreferenceSnapshot();
        }
        if (voice is { } session) session.Mixer.Volume = volume / 100f;
        VoiceCardVolumeText.Text = volume + "%";
    }
    // Moving the slider in Settings is heard at once; Save keeps it and Cancel puts the saved value back.
    private void PreviewVoiceVolume(double value)
    {
        if (voice is { } session) session.Mixer.Volume = (float)(Math.Clamp(value, 0, 200) / 100);
    }
    private void VoiceTick()
    {
        if (voice is not { } session) return;
        if (voiceJoining) { FinishJoining(); if (!voiceJoining) RefreshVoice(); }
        var client = room;
        var me = client?.Identity?.Peer;
        foreach (var member in client?.Snapshot?.Voice ?? [])
            if (voicePresence.TryGetValue(member.Peer, out var presence))
                presence.SetSpeaking(member.Peer == me ? session.Capture.Transmitting && session.Capture.Speaking : member.Joined && session.Mixer.IsSpeaking(member.Slot));
        var level = session.Capture.Transmitting || !voicePreferences.PushToTalk ? Math.Clamp((session.Capture.LevelDb + 60) / 50, 0, 1) : 0;
        VoiceLevelFill.Width = VoiceLevelTrack.ActualWidth * level;
        // The movie fades down while anyone talks and returns after a pause, never pumping between words.
        var talking = voicePreferences.Ducking && Environment.TickCount64 - session.Mixer.LastSpeechTick < 900;
        var duck = talking ? Math.Max(0.5, videoDuck - 0.1) : Math.Min(1, videoDuck + 0.05);
        if (duck != videoDuck) { videoDuck = duck; ApplyPlayerVolume(); }
    }
    private void ApplyPlayerVolume()
    {
        if (player is not null) player.Volume = (int)Math.Round(Volume.Value * videoDuck);
        // The promo is lowered for voice chat like the video.
        if (PostplayActive) PostplayVideo.Volume = player?.Mute == true ? 0 : Volume.Value * videoDuck / 100;
    }
    // Snapshot changes: refresh the panel, sound a cue when someone joins or leaves voice, and join automatically if chosen.
    private void VoiceSnapshot(RoomSnapshot snapshot)
    {
        var client = room;
        var me = client?.Identity?.Peer;
        var joined = (snapshot.Voice ?? []).Where(v => v.Joined).Select(v => v.Peer).ToHashSet();
        // No voice list means no host to ask: start over quietly rather than announce everyone leaving.
        if (snapshot.Voice is null) voiceRoster = null;
        else if (voiceRoster is { } previous)
        {
            var arrived = joined.Except(previous).Where(id => id != me).ToArray();
            // People who left the room are announced by the room itself.
            var departed = previous.Except(joined).Where(id => id != me && snapshot.People.Any(p => p.Id == id)).ToArray();
            if (voice is { } session && (arrived.Length > 0 || departed.Length > 0)) session.Mixer.Cue(arrived.Length > 0 ? VoiceCues.Join : VoiceCues.Leave);
        }
        if (snapshot.Voice is not null) voiceRoster = joined;
        FinishJoining();
        RefreshVoice();
        if (voice is null && !voiceJoining && client != voiceClosingRoom && client?.VoiceAvailable == true && client.Identity?.Room is { } code
            && (voicePreferences.AutoJoin && voiceAutoJoinedRoom != code || voiceWaitingRoom == code))
        {
            voiceAutoJoinedRoom = code;
            _ = Guard(JoinVoiceAsync);
        }
    }
    // A new room connection: reattach a running session, or reset for a different room.
    private void VoiceRoomChanged(RoomClient client, bool reconnecting)
    {
        voiceRoster = null; voicePresence.Clear();
        if (!reconnecting) { voiceLocallyMuted.Clear(); voiceLevels.Clear(); }
        if (voice is not null) AttachVoice();
        RefreshVoice();
    }
    private async Task VoiceRoomClosed(bool reconnecting)
    {
        if (reconnecting) { Volatile.Write(ref voiceRoom, null); return; }
        voiceClosingRoom = room;
        voiceAutoJoinedRoom = voiceWaitingRoom = null;
        await LeaveVoiceAsync();
    }

    // Settings › Voice chat
    private VoiceCapture? voiceTestCapture;
    private VoiceAudio? voiceTest;
    private readonly DispatcherTimer voiceTestTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private static readonly VoiceAudio.Device DefaultVoiceDevice = new("", "Windows default");
    private async Task LoadVoiceDevicesAsync()
    {
        try
        {
            (voiceInputs, voiceOutputs) = await VoiceAudio.ListDevicesAsync();
            loadingPreferences = true;
            try
            {
                // Keep the current draft: the saved choice, or whatever was picked before the list refreshed.
                var (input, output) = (VoiceInputPicker.SelectedValue as string ?? voicePreferences.Input ?? "", VoiceOutputPicker.SelectedValue as string ?? voicePreferences.Output ?? "");
                var draftInput = input.Length > 0 ? input : null; var draftOutput = output.Length > 0 ? output : null;
                VoiceInputPicker.ItemsSource = VoiceChoices(voiceInputs, draftInput, "Saved microphone (not connected)");
                VoiceOutputPicker.ItemsSource = VoiceChoices(voiceOutputs, draftOutput, "Saved output (not connected)");
                VoiceInputPicker.SelectedValue = input; VoiceOutputPicker.SelectedValue = output;
                if (VoiceInputPicker.SelectedIndex < 0) VoiceInputPicker.SelectedIndex = 0;
                if (VoiceOutputPicker.SelectedIndex < 0) VoiceOutputPicker.SelectedIndex = 0;
            }
            finally { loadingPreferences = false; }
        }
        catch (COMException) { VoiceTestStatus.Text = "Windows audio devices are unavailable."; }
    }
    // Until the device list loads, the saved choice is listed by itself so it is shown and kept, not replaced by the default.
    private static VoiceAudio.Device[] VoiceChoices(VoiceAudio.Device[] devices, string? saved, string missing) =>
        [DefaultVoiceDevice, .. devices, .. saved is not null && devices.All(d => d.Id != saved) ? [new VoiceAudio.Device(saved, missing)] : Array.Empty<VoiceAudio.Device>()];
    private VoiceAudio.Device[] voiceInputs = [], voiceOutputs = [];
    private void LoadVoicePreferenceControls()
    {
        var saved = voicePreferences;
        var loaded = voiceInputs.Length + voiceOutputs.Length > 0;
        VoiceInputPicker.ItemsSource = VoiceChoices(voiceInputs, saved.Input, loaded ? "Saved microphone (not connected)" : "Saved microphone");
        VoiceOutputPicker.ItemsSource = VoiceChoices(voiceOutputs, saved.Output, loaded ? "Saved output (not connected)" : "Saved output");
        VoiceInputPicker.SelectedValue = saved.Input ?? ""; VoiceOutputPicker.SelectedValue = saved.Output ?? "";
        if (VoiceInputPicker.SelectedIndex < 0) VoiceInputPicker.SelectedIndex = 0;
        if (VoiceOutputPicker.SelectedIndex < 0) VoiceOutputPicker.SelectedIndex = 0;
        VoiceProcessingPicker.ItemsSource ??= VoiceProcessingChoices;
        VoiceProcessingPicker.SelectedValue = saved.Processing;
        VoicePushToTalk.IsChecked = saved.PushToTalk; VoiceAlwaysOn.IsChecked = saved.AlwaysOn;
        VoiceDucking.IsChecked = saved.Ducking; VoiceAutoJoin.IsChecked = saved.AutoJoin; VoiceVolumeSlider.Value = saved.Volume;
        // Cancel restores the saved microphone; the level meter follows it.
        if (VoicePreferences.IsVisible) _ = Guard(RestartVoiceTest);
    }
    private sealed record VoiceProcessingChoice(VoiceAudio.Processing Id, string Name);
    private static readonly VoiceProcessingChoice[] VoiceProcessingChoices =
    [
        new(VoiceAudio.Processing.Windows, "Windows voice processing: echo and noise reduction"),
        new(VoiceAudio.Processing.Standard, "Standard: the microphone's own settings"),
        new(VoiceAudio.Processing.Raw, "Raw: no processing at all"),
    ];
    private VoiceOptions DraftVoicePreferences()
    {
        static string? Device(object? id) => id is string { Length: > 0 } value ? value : null;
        return new(Device(VoiceInputPicker.SelectedValue), Device(VoiceOutputPicker.SelectedValue), VoiceProcessingPicker.SelectedValue as VoiceAudio.Processing? ?? voicePreferences.Processing,
            VoicePushToTalk.IsChecked == true, VoiceAlwaysOn.IsChecked == true, VoiceDucking.IsChecked == true, VoiceAutoJoin.IsChecked == true, (int)Math.Round(VoiceVolumeSlider.Value));
    }
    private void SaveVoicePreferences()
    {
        var draft = DraftVoicePreferences();
        library.Setting("voiceInput", draft.Input ?? ""); library.Setting("voiceOutput", draft.Output ?? "");
        library.Setting("voiceProcessing", draft.Processing switch { VoiceAudio.Processing.Standard => "standard", VoiceAudio.Processing.Raw => "raw", _ => "windows" });
        // Older versions of the app read only the echo cancellation switch.
        library.Setting("voiceEcho", draft.Processing == VoiceAudio.Processing.Windows ? "true" : "false");
        library.Setting("voicePushToTalk", draft.PushToTalk ? "true" : "false"); library.Setting("voiceAlwaysOn", draft.AlwaysOn ? "true" : "false");
        library.Setting("voiceDucking", draft.Ducking ? "true" : "false"); library.Setting("voiceAutoJoin", draft.AutoJoin ? "true" : "false");
        library.Setting("voiceVolume", draft.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        voiceVolumeChanged = false;
        var previous = voicePreferences; voicePreferences = draft;
        if (voice is not { } session) { RefreshVoice(); return; }
        session.Mixer.Volume = draft.Volume / 100f;
        if (!draft.Ducking && videoDuck != 1) { videoDuck = 1; ApplyPlayerVolume(); }
        if (previous.Devices != draft.Devices) _ = session.Audio.ApplyAsync(draft.Devices);
        ApplyVoiceTransmit(); RefreshVoice();
    }
    private async void VoicePreferencesVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => await Guard(async () =>
    {
        if (VoicePreferences.IsVisible)
        {
            _ = LoadVoiceDevicesAsync();
            await RestartVoiceTest();
            voiceTestTimer.Start();
        }
        else await StopVoiceTest();
    });
    private async Task RestartVoiceTest()
    {
        if (!VoicePreferences.IsVisible) return;
        var settings = DraftVoicePreferences().Devices;
        if (voiceTest is { } running) { await running.ApplyAsync(settings); return; }
        voiceTestCapture = new VoiceCapture();
        var test = voiceTest = new VoiceAudio(voiceTestCapture, null, settings);
        test.Changed += () => Dispatcher.BeginInvoke(UpdateVoiceTest);
        try { await test.StartAsync(); }
        catch (COMException) { VoiceTestStatus.Text = "Windows audio devices are unavailable."; }
    }
    private async Task StopVoiceTest()
    {
        voiceTestTimer.Stop();
        var test = voiceTest; voiceTest = null; voiceTestCapture = null;
        VoiceTestFill.Width = 0;
        if (test is not null) await test.DisposeAsync();
    }
    private void UpdateVoiceTest()
    {
        if (voiceTest is not { } test || voiceTestCapture is not { } capture) return;
        var level = test.MicrophoneReady ? Math.Clamp((capture.LevelDb + 60) / 50, 0, 1) : 0;
        VoiceTestFill.Width = VoiceTestTrack.ActualWidth * level;
        VoiceTestStatus.Text = test.Problem ?? (test.InputName is { } name
            ? (capture.LevelDb > -45 ? "Watchroom hears you on " + name + "." : "Speak to test " + name + ". The bar moves when Watchroom hears you.")
            : "Checking your microphone…");
    }
    private async void PlayVoiceTestSound(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        VoiceTestSoundButton.IsEnabled = false;
        try { await VoiceAudio.PlayTestSoundAsync(DraftVoicePreferences().Output); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { VoiceTestStatus.Text = "The test sound could not play on this device."; }
        finally { VoiceTestSoundButton.IsEnabled = true; }
    });
}
