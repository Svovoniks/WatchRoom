using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Microphone and speaker streams for room voice. Device work runs on pool (MTA) threads: Core Audio objects
// created on the WPF thread would have to marshal every call back to it from the audio threads.
internal sealed class VoiceAudio : IAsyncDisposable
{
    public sealed record Device(string Id, string Name);
    public sealed record Settings(string? Input, string? Output, bool EchoCancellation);
    public static Task<(Device[] Inputs, Device[] Outputs)> ListDevicesAsync() => Task.Run(() =>
    {
        using var devices = new MMDeviceEnumerator();
        Device[] List(DataFlow flow) => devices.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => { using (device) return new Device(device.ID, device.FriendlyName); }).OrderBy(d => d.Name).ToArray();
        return (List(DataFlow.Capture), List(DataFlow.Render));
    });

    // Plays a short chime on the chosen output so people can check where voice will come from.
    public static Task PlayTestSoundAsync(string? output) => Task.Run(async () =>
    {
        using var devices = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (output is not null) try { device = devices.GetDevice(output); } catch (COMException) { }
        device ??= devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        using (device)
        {
            var mixer = new VoiceMixer();
            mixer.Cue(VoiceCues.Test);
            using var player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(60).WithCategory(AudioStreamCategory.GameChat).Build();
            player.Init(new MixerProvider(mixer));
            player.Play();
            await Task.Delay(VoiceCues.Test.Length * 1000 / VoicePacket.SampleRate + 250);
            player.Stop();
        }
    });

    private readonly VoiceCapture capture;
    private readonly VoiceMixer? mixer;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private MMDeviceEnumerator? devices;
    private MMDeviceNotificationClient? notifications;
    private WasapiRecorder? recorder;
    private WasapiPlayer? player;
    private Settings settings;
    private int restartQueued;
    public string? InputName { get; private set; }
    public string? OutputName { get; private set; }
    public bool MicrophoneReady { get; private set; }
    // A short, user-facing description of what is wrong with the microphone or speakers; null when both work.
    public string? Problem { get; private set; }
    public bool MicrophoneBlocked { get; private set; }
    public event Action? Changed;
    // Device checks play voice at zero session volume so nothing is heard, and read back what was played.
    internal bool SilentOutput { get; init; }
    internal long SamplesPlayed => provider is { } played ? Interlocked.Read(ref played.Samples) : 0;
    internal float PeakPlayed { get => provider?.Peak ?? 0; set { if (provider is { } played) played.Peak = value; } }
    private MixerProvider? provider;

    // A null mixer opens only the microphone, for the settings level meter.
    public VoiceAudio(VoiceCapture capture, VoiceMixer? mixer, Settings settings)
    { this.capture = capture; this.mixer = mixer; this.settings = settings; }

    public Task StartAsync() => Run(() =>
    {
        devices = new MMDeviceEnumerator();
        notifications = devices.CreateNotificationClient(false);
        notifications.DefaultDeviceChanged += (_, e) =>
        {
            // Following the Windows default: move to the new device, as other apps do.
            if (e.Role == Role.Console && (e.Flow == DataFlow.Capture ? settings.Input : mixer is null ? "" : settings.Output) is null) QueueRestart(500);
        };
        notifications.DeviceStateChanged += (_, _) => { if (!MicrophoneReady || mixer is not null && player is null) QueueRestart(500); };
        notifications.DeviceAdded += (_, _) => { if (!MicrophoneReady || mixer is not null && player is null) QueueRestart(500); };
        OpenAll();
    });
    public Task ApplyAsync(Settings updated) => Run(() => { settings = updated; CloseAll(); OpenAll(); });

    private void OpenAll()
    {
        string? problem = null;
        var outputFailed = false;
        if (mixer is not null)
        {
            try { OpenOutput(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            {
                problem = OutputName is null ? "No speakers or headphones found. Connect some to hear the room."
                    : "Voice can't play on " + OutputName + ". Choose another output in Settings › Voice chat.";
                outputFailed = OutputName is not null;
                PlaybackDiagnostics.Record("voice-output-failed", new { error = ex.GetType().Name, code = ex.HResult });
                CloseOutput();
            }
        }
        try { OpenInput(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            CloseInput();
            MicrophoneBlocked = ex.HResult == unchecked((int)0x80070005) || ex is UnauthorizedAccessException;
            problem = MicrophoneBlocked ? "Windows is blocking the microphone. Allow desktop apps in Settings › Privacy & security › Microphone."
                // Both directions failing points at Windows audio itself rather than one device.
                : outputFailed ? "Windows audio isn't working right now. Restarting your PC usually fixes it."
                : "Your microphone could not be opened. Check that it is connected, or choose another in Settings › Voice chat.";
            PlaybackDiagnostics.Record("voice-input-failed", new { error = ex.GetType().Name, code = ex.HResult });
        }
        // No devices at all is a setup problem, not a broken device.
        Problem = !MicrophoneReady && InputName is null && mixer is not null && player is null && OutputName is null ? "No microphone or speakers found. Connect a headset or check Windows sound settings."
            : problem ?? (MicrophoneReady ? null : mixer is null || player is not null ? "No microphone found. You can still listen." : "No microphone found.");
        Changed?.Invoke();
    }
    private MMDevice? Find(DataFlow flow, string? id)
    {
        if (id is not null)
            try { var chosen = devices!.GetDevice(id); if (chosen.State == DeviceState.Active) return chosen; chosen.Dispose(); }
            catch (COMException) { }
        // A chosen device that is unplugged falls back to the Windows default until it returns.
        return devices!.TryGetDefaultAudioEndpoint(flow, Role.Console, out var fallback) ? fallback : null;
    }
    private void OpenInput()
    {
        MicrophoneBlocked = false; MicrophoneReady = false; InputName = null;
        using var device = Find(DataFlow.Capture, settings.Input);
        if (device is null) return;
        InputName = device.FriendlyName;
        var builder = new WasapiRecorderBuilder().WithDevice(device).WithFormat(new WaveFormat(VoicePacket.SampleRate, 16, 1)).WithBufferLength(20);
        // Communications mode turns on Windows echo cancellation, noise suppression and gain control where available,
        // so speakers playing the movie do not feed it back to the room.
        if (settings.EchoCancellation) builder = builder.WithCommunicationsMode();
        var created = builder.Build();
        created.DataAvailable += (buffer, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) != 0) { Span<short> silence = stackalloc short[buffer.Length / 2]; silence.Clear(); capture.Write(silence); }
            else capture.Write(MemoryMarshal.Cast<byte, short>(buffer));
        };
        created.RecordingStopped += (_, e) =>
        {
            if (e.Exception is null || lifetime.IsCancellationRequested) return;
            // Unplugged or disabled: try again shortly, on the default microphone if needed.
            PlaybackDiagnostics.Record("voice-input-stopped", new { error = e.Exception.GetType().Name, code = e.Exception.HResult });
            MicrophoneReady = false; Problem = "Microphone disconnected. Reconnecting…"; Changed?.Invoke();
            QueueRestart(1500);
        };
        recorder = created;
        created.StartRecording();
        MicrophoneReady = true;
    }
    private void OpenOutput()
    {
        OutputName = null;
        using var device = Find(DataFlow.Render, settings.Output);
        if (device is null) throw new InvalidOperationException("No audio output");
        OutputName = device.FriendlyName;
        // Voice is not marked as communications audio, which would make Windows lower other apps such as the movie.
        var created = new WasapiPlayerBuilder().WithDevice(device).WithLatency(40).WithCategory(AudioStreamCategory.GameChat).Build();
        created.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is null || lifetime.IsCancellationRequested) return;
            PlaybackDiagnostics.Record("voice-output-stopped", new { error = e.Exception.GetType().Name, code = e.Exception.HResult });
            QueueRestart(1500);
        };
        created.Init(provider = new MixerProvider(mixer!));
        if (SilentOutput) created.Volume = 0;
        player = created;
        created.Play();
    }
    private void QueueRestart(int delay)
    {
        if (Interlocked.Exchange(ref restartQueued, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, lifetime.Token); }
            catch (OperationCanceledException) { return; }
            Interlocked.Exchange(ref restartQueued, 0);
            try { await Run(() => { CloseAll(); OpenAll(); }); } catch (ObjectDisposedException) { }
        });
    }
    private void CloseInput()
    {
        var old = recorder; recorder = null; MicrophoneReady = false;
        try { old?.Dispose(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }
    private void CloseOutput()
    {
        var old = player; player = null;
        try { old?.Dispose(); } catch (Exception ex) when (ex is COMException or InvalidOperationException) { }
    }
    private void CloseAll() { CloseInput(); CloseOutput(); }
    // A stuck audio driver can block Core Audio calls indefinitely. Callers stop waiting after a few seconds and
    // report it; the stuck call keeps the gate until it returns, so later device work never overlaps it.
    private async Task Run(Action work)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10))) { ReportStalled(); return; }
        var running = Task.Run(() => { if (!lifetime.IsCancellationRequested) work(); });
        _ = running.ContinueWith(_ => gate.Release(), TaskScheduler.Default);
        if (await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(8))) != running) { ReportStalled(); return; }
        await running;
    }
    private void ReportStalled()
    {
        PlaybackDiagnostics.Record("voice-audio-stalled", new { });
        Problem = "Windows audio isn't responding. Voice chat will start when it recovers; restarting your PC usually fixes it.";
        MicrophoneReady = false;
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        lifetime.Cancel();
        // Leaving voice must never hang the app, even if Windows audio is stuck; cleanup then finishes in the background.
        var cleanup = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                CloseAll();
                try { notifications?.Dispose(); devices?.Dispose(); } catch (COMException) { }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(3)));
    }

    // Plays the room's voice as stereo; the audio engine converts to the device's own format.
    private sealed class MixerProvider(VoiceMixer mixer) : IWaveProvider
    {
        private float[] mono = new float[VoicePacket.FrameSamples];
        public long Samples;
        public float Peak;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoicePacket.SampleRate, 2);
        public int Read(Span<byte> buffer)
        {
            var stereo = MemoryMarshal.Cast<byte, float>(buffer);
            var frames = stereo.Length / 2;
            if (mono.Length < frames) mono = new float[frames];
            // An exception here would stop the device stream; play silence instead.
            try { mixer.Read(mono.AsSpan(0, frames)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { mono.AsSpan(0, frames).Clear(); }
            var peak = Peak;
            for (var i = 0; i < frames; i++) { stereo[2 * i] = stereo[2 * i + 1] = mono[i]; peak = Math.Max(peak, Math.Abs(mono[i])); }
            Peak = peak; Interlocked.Add(ref Samples, frames);
            return frames * 8;
        }
    }
}
