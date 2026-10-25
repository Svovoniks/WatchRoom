using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using Watchroom.Core;
using Watchroom.Desktop;

// Voice chat on the real Windows audio stack: the default microphone in communications mode, playback
// through the default output at zero session volume (so nothing is heard), device switching and fallback.
// Without audio hardware, or with Windows audio broken, it checks that voice fails quickly and explains why.
static class VoiceDeviceChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var (inputs, outputs) = await VoiceAudio.ListDevicesAsync();
        Console.WriteLine($"Audio devices: {inputs.Length} microphones, {outputs.Length} outputs");
        var frames = 0;
        var mic = new VoiceCapture { PushToTalk = true, Transmitting = true };
        mic.Frame += _ => Interlocked.Increment(ref frames);
        var mixer = new VoiceMixer();
        var clock = Stopwatch.StartNew();
        await using (var audio = new VoiceAudio(mic, mixer, new(null, null, VoiceAudio.Processing.Windows)) { SilentOutput = true })
        {
            await audio.StartAsync();
            check(clock.ElapsedMilliseconds < 9000, $"opening voice devices returns promptly ({clock.ElapsedMilliseconds} ms)");
            if (!audio.MicrophoneReady)
            {
                check(audio.Problem is { Length: > 0 }, $"when the microphone can't open, voice explains why: \"{audio.Problem}\"");
                if (inputs.Length == 0 && outputs.Length > 0) check(audio.Problem!.Contains("You can still listen"), "without a microphone voice says you can still listen");
                if (inputs.Length == 0 && outputs.Length == 0) check(audio.Problem == "No microphone or speakers found. Connect a headset or check Windows sound settings.", "with no audio devices at all voice says so");
                // Listening still works without a microphone.
                if (audio.OutputName is not null) await CheckPlayback(audio, mixer, check);
                Console.WriteLine("SKIP: the microphone checks need a working microphone.");
                return;
            }
            check(audio.Problem is null && audio.InputName is not null, $"the Windows default microphone opens in communications mode ({audio.InputName})");
            clock.Restart(); var before = Volatile.Read(ref frames);
            await Task.Delay(2000);
            var rate = (Volatile.Read(ref frames) - before) * 1000.0 / clock.ElapsedMilliseconds;
            check(rate is > 45 and < 55, $"the microphone delivers 48 kHz audio in real time ({rate:F1} frames/s, 50 expected)");
            check(mic.LevelDb is > -101 and < 0, $"the microphone level is measured ({mic.LevelDb:F1} dBFS)");

            await CheckPlayback(audio, mixer, check);

            await audio.ApplyAsync(new(null, null, VoiceAudio.Processing.Standard));
            before = Volatile.Read(ref frames); await Task.Delay(600);
            check(audio.MicrophoneReady && Volatile.Read(ref frames) - before > 20, "switching to standard microphone processing reopens the microphone without interrupting voice");
            await audio.ApplyAsync(new(null, null, VoiceAudio.Processing.Raw));
            before = Volatile.Read(ref frames); await Task.Delay(600);
            check(audio.MicrophoneReady && Volatile.Read(ref frames) - before > 20, "the raw microphone mode opens, or falls back to standard processing where the device has none");
            await audio.ApplyAsync(new("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}", null, VoiceAudio.Processing.Windows));
            before = Volatile.Read(ref frames); await Task.Delay(600);
            check(audio.MicrophoneReady && Volatile.Read(ref frames) - before > 20, "a saved microphone that is no longer connected falls back to the Windows default");

            // The settings meter opens the same microphone while voice is running.
            var meterFrames = 0;
            var meter = new VoiceCapture { PushToTalk = true, Transmitting = true }; meter.Frame += _ => Interlocked.Increment(ref meterFrames);
            await using (var test = new VoiceAudio(meter, null, new(null, null, VoiceAudio.Processing.Windows)))
            {
                await test.StartAsync(); before = Volatile.Read(ref frames); await Task.Delay(600);
                check(test.MicrophoneReady && meterFrames > 20 && Volatile.Read(ref frames) - before > 20, "the settings microphone test runs alongside voice chat");
            }
        }
        var after = Volatile.Read(ref frames); await Task.Delay(400);
        check(Volatile.Read(ref frames) == after, "leaving voice closes the microphone");
    }

    static async Task CheckPlayback(VoiceAudio audio, VoiceMixer mixer, Action<bool, string> check)
    {
        check(audio.OutputName is not null, $"voice plays on the Windows default output ({audio.OutputName}), at zero session volume for this check");
        var clock = Stopwatch.StartNew(); var played = audio.SamplesPlayed;
        await Task.Delay(1000);
        var pulled = (audio.SamplesPlayed - played) * 1000.0 / clock.ElapsedMilliseconds;
        check(pulled is > 44000 and < 52000, $"the output pulls the voice mix in real time ({pulled:F0} samples/s)");
        check(audio.PeakPlayed < 0.001f, "the output is silent while nobody talks");
        var tone = Encode(440, 0.3f, 50);
        for (var i = 0; i < tone.Count; i++) { mixer.Receive(9, (ushort)i, 0, tone[i]); await Task.Delay(20); }
        await Task.Delay(200);
        check(audio.PeakPlayed > 0.2f, $"a remote voice reaches the audio device (peak {audio.PeakPlayed:F2})");
    }
    static List<byte[]> Encode(double hz, float amplitude, int count)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var result = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var pcm = Enumerable.Range(i * 960, 960).Select(n => (short)(Math.Sin(2 * Math.PI * hz * n / 48000) * amplitude * 32767)).ToArray();
            var buffer = new byte[600]; result.Add(buffer[..encoder.Encode(pcm, 960, buffer, 600)]);
        }
        return result;
    }
}
