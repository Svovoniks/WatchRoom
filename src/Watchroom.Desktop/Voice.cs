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
    private sealed record VoiceSession(VoiceCapture Capture, VoiceMixer Mixer, VoiceAudio Audio);
    private sealed record VoiceOptions(string? Input, string? Output, bool EchoCancellation, bool PushToTalk, bool Ducking, bool AutoJoin, int Volume)
    {
        public VoiceAudio.Settings Devices => new(Input, Output, EchoCancellation);
    }
    private VoiceSession? voice;
    private VoiceOptions voicePreferences = new(null, null, true, false, true, false, 100);
    private bool voiceJoining, voiceMuted, pushToTalkHeld;
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
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(bool inVoice, bool muted, bool me, bool locallyMuted, bool listening)
        {
            if (InVoice == inVoice && Muted == muted && Me == me && LocallyMuted == locallyMuted && Listening == listening) return;
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
        if (!voicePresence.TryGetValue(id, out var presence)) voicePresence[id] = presence = new();
        presence.Name = name;
        return presence;
    }

    private void InitializeVoice()
    {
        voicePreferences = LoadVoicePreferences();
        voiceTimer.Tick += (_, _) => VoiceTick();
        voiceTestTimer.Tick += (_, _) => UpdateVoiceTest();
        Deactivated += (_, _) => ReleasePushToTalk();
        RefreshVoice();
    }
    private VoiceOptions LoadVoicePreferences()
    {
        static string? Device(string? id) => string.IsNullOrEmpty(id) ? null : id;
        return new(Device(library.Setting("voiceInput")), Device(library.Setting("voiceOutput")), library.Setting("voiceEcho") != "false",
            library.Setting("voicePushToTalk") == "true", library.Setting("voiceDucking") != "false", library.Setting("voiceAutoJoin") == "true",
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
        voiceJoining = true; voice = session; voiceMuted = false; RefreshVoice();
        try { await audio.StartAsync(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            if (voice == session) voice = null;
            await audio.DisposeAsync();
            VoiceToast("Windows audio isn't working right now, so voice chat could not start. Restarting your PC usually fixes it.", 7);
            return;
        }
        finally { voiceJoining = false; RefreshVoice(); }
        // Leaving voice or the room while devices were opening ends this session; LeaveVoiceAsync has already cleaned up.
        if (voice != session) { await audio.DisposeAsync(); return; }
        var current = room;
        if (current is null || current == voiceClosingRoom)
        {
            voice = null; Volatile.Write(ref voiceRoom, null);
            foreach (var stale in new[] { client, current }) if (stale is not null) { stale.VoiceOutput = null; stale.SetVoice(false, false); }
            RefreshVoice(); await audio.DisposeAsync(); return;
        }
        // The room may have reconnected while the devices opened; voice continues on the new connection.
        AttachVoice();
        mixer.Cue(VoiceCues.Join);
        voiceTimer.Start();
        RefreshVoice();
        VoiceToast(audio.Problem ?? (voicePreferences.PushToTalk ? "You joined voice. Hold V to talk." : "You joined voice. Press V to mute or unmute."), audio.Problem is null ? 3 : 7);
    }
    private async Task LeaveVoiceAsync()
    {
        if (voice is not { } session) return;
        voice = null; pushToTalkHeld = false; voiceJoining = false;
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
        VoiceButtonIcon.Text = inVoice ? "" : "";
        var action = inVoice ? "Leave voice chat" : "Join voice chat";
        // Still clickable while unavailable, so a click explains why instead of doing nothing.
        var unavailable = connected && !inVoice && !voiceJoining && !client!.VoiceAvailable;
        VoiceButton.Opacity = unavailable ? 0.55 : 1;
        var waiting = unavailable && voiceWaitingRoom is not null && voiceWaitingRoom == client!.Identity?.Room;
        VoiceButton.ToolTip = waiting ? VoiceUnavailableReason(client!) + " You'll join automatically. Click to cancel."
            : unavailable ? VoiceUnavailableReason(client!) : inVoice ? action : others.Length > 0 ? Names(others) + (others.Length == 1 ? " is" : " are") + " in voice. Join to talk while you watch." : "Join voice chat to talk while you watch";
        System.Windows.Automation.AutomationProperties.SetName(VoiceButton, action);
        VoiceButton.IsEnabled = VoiceCardJoin.IsEnabled = connected && !voiceJoining;
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
        VoiceCardJoin.Content = voiceJoining ? "Joining…" : inVoice ? "Leave" : waiting ? "Cancel" : "Join";
        // The accent invites joining; leaving is a quieter choice.
        VoiceCardJoin.Style = (Style)FindResource(inVoice || waiting ? "QuietButton" : "Primary");
        System.Windows.Automation.AutomationProperties.SetName(VoiceCardJoin, action);
        VoiceCardIcon.Text = inVoice ? (microphoneOn ? "" : "") : "";
        VoiceCardStatus.Text = !connected ? ""
            // In voice while the host is away: say what voice is waiting for, not that you're alone.
            : inVoice && !client!.VoiceAvailable ? VoiceUnavailableReason(client)
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
            presence.Update(member?.Joined == true || mine && inVoice, mine && inVoice ? !microphoneOn : member?.Muted == true, mine, voiceLocallyMuted.Contains(id), inVoice);
        }
        if (voice is { } session)
            foreach (var member in members) session.Mixer.SetGain(member.Slot, voiceLocallyMuted.Contains(member.Peer) ? 0 : 1);
    }
    private void VoiceTick()
    {
        if (voice is not { } session) return;
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
        if (!reconnecting) voiceLocallyMuted.Clear();
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
        VoiceEchoCancellation.IsChecked = saved.EchoCancellation; VoicePushToTalk.IsChecked = saved.PushToTalk;
        VoiceDucking.IsChecked = saved.Ducking; VoiceAutoJoin.IsChecked = saved.AutoJoin; VoiceVolumeSlider.Value = saved.Volume;
        // Cancel restores the saved microphone; the level meter follows it.
        if (VoicePreferences.IsVisible) _ = Guard(RestartVoiceTest);
    }
    private VoiceOptions DraftVoicePreferences()
    {
        static string? Device(object? id) => id is string { Length: > 0 } value ? value : null;
        return new(Device(VoiceInputPicker.SelectedValue), Device(VoiceOutputPicker.SelectedValue), VoiceEchoCancellation.IsChecked == true,
            VoicePushToTalk.IsChecked == true, VoiceDucking.IsChecked == true, VoiceAutoJoin.IsChecked == true, (int)Math.Round(VoiceVolumeSlider.Value));
    }
    private void SaveVoicePreferences()
    {
        var draft = DraftVoicePreferences();
        library.Setting("voiceInput", draft.Input ?? ""); library.Setting("voiceOutput", draft.Output ?? "");
        library.Setting("voiceEcho", draft.EchoCancellation ? "true" : "false"); library.Setting("voicePushToTalk", draft.PushToTalk ? "true" : "false");
        library.Setting("voiceDucking", draft.Ducking ? "true" : "false"); library.Setting("voiceAutoJoin", draft.AutoJoin ? "true" : "false");
        library.Setting("voiceVolume", draft.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
