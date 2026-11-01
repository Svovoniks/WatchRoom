using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;
using Windows.Media.SpeechSynthesis;

// BlackPearl's own microphone processing, without audio devices: the bundled models load, noise is removed, Silero
// decides what is speech, and the echo canceller removes what the speakers played. Speech comes from Windows'
// speech synthesizer; machines without a voice check everything else.
static class VoiceProcessingChecks
{
    const int Rate = VoicePacket.SampleRate, Frame = VoicePacket.FrameSamples;

    public static async Task Run(Action<bool, string> check)
    {
        using (var standard = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Standard))
            check(standard.EchoCancelling && standard.DenoiserName == "deepfilter" && standard.DetectsSpeech, "echo cancellation, DeepFilterNet 3 and Silero VAD load from the app folder");
        using (var light = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Light))
            check(light.EchoCancelling && light.DenoiserName == "rnnoise" && light.DetectsSpeech, "lighter processing loads RNNoise");
        using (var off = new VoiceProcessor(new VoiceCapture(), VoiceAudio.Processing.Off))
            check(!off.EchoCancelling && off.DenoiserName is null && off.DetectsSpeech, "with processing off only speech detection runs");

        // One chunk that sounds like a syllable is not speech; two in a row are, and speech lasts until it fades.
        var decision = new VoiceProcessor.SpeechDecision();
        check(!decision.Next(0.9f) && !decision.Next(0.1f) && !decision.Next(0.9f) && decision.Next(0.6f) && decision.Next(0.4f) && !decision.Next(0.3f),
            "speech needs two chunks in a row above 0.5, then holds until the probability falls below 0.35");

        var fan = Noise(Rate * 6, 0.03, 1);
        foreach (var (mode, name) in new[] { (VoiceAudio.Processing.Standard, "DeepFilterNet"), (VoiceAudio.Processing.Light, "RNNoise") })
        {
            var result = Process(mode, fan);
            var removed = Level(fan[(Rate * 3)..]) - result.Level(3, 6);
            check(removed > 15, $"{name} removes steady fan noise ({removed:F0} dB quieter)");
            check(result.Sent == 0, $"fan noise is never sent as speech with {name} ({result.Sent} frames)");
        }
        var raw = Process(VoiceAudio.Processing.Off, fan);
        check(Math.Abs(Level(fan[(Rate * 3)..]) - raw.Level(3, 6)) < 1, "with processing off the microphone is sent as it is");

        // Typing: loud, short clicks. The old level gate opened for anything louder than -30 dBFS.
        var typing = new short[Rate * 4];
        var random = new Random(7);
        for (var at = Rate / 10; at < typing.Length - 400; at += Rate / 8 + random.Next(Rate / 10))
            for (var i = 0; i < 300; i++) typing[at + i] = (short)(Math.Exp(-i / 40.0) * (random.NextDouble() * 2 - 1) * 20000);
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light, VoiceAudio.Processing.Off })
        {
            var clicks = Process(mode, typing);
            check(clicks.Sent == 0, $"typing is not taken for speech ({mode}: {clicks.Sent} frames sent)");
        }
        // Things dropped or knocked: thuds that bounce, slaps, knocks and keys, loud and close to the microphone.
        var impacts = Enumerable.Range(0, 24).Select(Impact).ToArray();
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light })
        {
            var sent = impacts.Select(sound => Process(mode, sound).Sent).ToArray();
            check(sent.All(s => s == 0), $"dropped and knocked objects are never sent as speech ({mode}: {sent.Count(s => s > 0)} of {sent.Length} sounds sent)");
        }

        var speech = await Speak();
        if (speech is null) { Console.WriteLine("SKIP: no Windows voice is installed, so the speech checks were skipped."); return; }
        var spoken = Process(VoiceAudio.Processing.Standard, speech);
        var talking = speech.Length / Frame;
        check(spoken.Sent > talking * 0.6, $"synthesized speech is detected and sent ({spoken.Sent} of {talking} frames)");
        var noisy = Mix(speech, Noise(speech.Length, 0.03, 2));
        var overFan = Process(VoiceAudio.Processing.Standard, noisy);
        check(overFan.Sent > talking * 0.6, $"speech over fan noise is still detected ({overFan.Sent} of {talking} frames)");
        var lightOverFan = Process(VoiceAudio.Processing.Light, noisy);
        check(lightOverFan.Sent > talking * 0.6, $"speech over fan noise is detected with lighter processing ({lightOverFan.Sent} of {talking} frames)");
        // A quiet microphone, such as a virtual cable: DeepFilterNet alone would remove speech this quiet as noise.
        var quietGain = Math.Pow(10, (-42 - ActiveDb(speech)) / 20);
        var quiet = speech.Select(s => (short)(s * quietGain)).ToArray();
        foreach (var mode in new[] { VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light })
        {
            var result = Process(mode, quiet);
            check(result.Sent > talking * 0.6, $"quiet speech at -42 dBFS is sent ({mode}: {result.Sent} of {talking} frames)");
            var raised = result.Level(4, quiet.Length / Rate) - Level(quiet[(Rate * 4)..]);
            check(raised > 10, $"quiet speech is raised towards a normal level ({mode}: {raised:F0} dB louder)");
        }
        // A learned high gain never pushes a sudden loud sound past full scale.
        var level = new VoiceProcessor.InputLevel();
        var faint = Enumerable.Range(0, VoiceProcessor.BlockSamples).Select(i => (float)(0.003 * Math.Sin(i * 0.1))).ToArray();
        for (var i = 0; i < 50; i++) level.Learn(faint, 1);
        level.Apply((float[])faint.Clone());
        float[] Shout() => Enumerable.Range(0, VoiceProcessor.BlockSamples).Select(i => (float)(0.7 * Math.Sin(i * 0.1))).ToArray();
        var shouts = new[] { Shout(), Shout() };
        foreach (var shout in shouts) level.Apply(shout);
        var loudest = shouts.Max(s => s.Max(Math.Abs));
        check(level.GainDb > 20 && loudest <= 0.9f, $"a loud sound after quiet speech is not clipped (gain {level.GainDb:F0} dB, peak {loudest:F2})");
        VoiceProcessor.InputLevel.Forget();
        // The first word is not cut off. The gate opens a little after speech starts and sends its pre-roll at once,
        // so the first burst begins earlier.
        var onset = speech.Select((s, i) => (s, i)).First(p => Math.Abs(p.s) > 1000).i / Frame;
        var opened = spoken.SentFrames.FirstOrDefault(-1);
        var from = opened - spoken.SentFrames.Count(f => f == opened) + 1;
        check(opened >= 0 && from <= onset, $"the start of the first word is sent (audio from frame {from}, speech from frame {onset})");

        // The movie's dialogue from the speakers reaches the microphone 40 ms later, quieter, with a room reflection.
        var movie = Repeat(speech, Rate * 12);
        var heard = new short[movie.Length];
        for (var i = 0; i < heard.Length; i++)
        {
            var direct = i >= 1920 ? movie[i - 1920] * 0.5 : 0;
            var reflection = i >= 3360 ? movie[i - 3360] * 0.2 : 0;
            heard[i] = (short)(direct + reflection);
        }
        var echo = Process(VoiceAudio.Processing.Standard, heard, movie);
        var cancelled = Level(heard[(Rate * 6)..]) - echo.Level(6, 12);
        check(cancelled > 20, $"the movie's sound from the speakers is removed from the microphone ({cancelled:F0} dB quieter)");
        var lateSent = echo.SentAfter(6);
        check(lateSent < Rate * 6 / Frame / 10, $"once the canceller has adapted the movie's dialogue is not sent as speech ({lateSent} frames in 6 s)");
        // Talking over the movie: from 6 s the person says something else while the dialogue keeps playing.
        var voice = speech[Rate..(Rate * 7)];
        var both = heard.Select((s, i) => i >= Rate * 6 ? (short)Math.Clamp(s + voice[i - Rate * 6], short.MinValue, short.MaxValue) : s).ToArray();
        var doubleTalk = Process(VoiceAudio.Processing.Standard, both, movie);
        var voiceFrames = voice.Length / Frame;
        check(doubleTalk.SentAfter(6) > voiceFrames * 0.6, $"speaking over the movie is still sent ({doubleTalk.SentAfter(6)} of {voiceFrames} frames)");
        var kept = doubleTalk.Level(6, 12) - Level(voice);
        check(kept > -10, $"the voice keeps its level while the canceller removes the movie ({kept:F1} dB)");
    }

    sealed record Result(double[] Levels, List<int> SentFrames)
    {
        public int Sent => SentFrames.Count;
        public int SentAfter(int seconds) => SentFrames.Count(f => f >= seconds * Rate / Frame);
        // Mean power of the processed frames between two times, in dBFS.
        public double Level(int from, int to)
        {
            var frames = Levels[(from * Rate / Frame)..Math.Min(Levels.Length, to * Rate / Frame)];
            return 10 * Math.Log10(frames.Average(db => Math.Pow(10, db / 10)) + 1e-10);
        }
    }
    // Runs samples through the processor in 20 ms writes, as the microphone delivers them, with an optional echo reference.
    static Result Process(VoiceAudio.Processing mode, short[] microphone, short[]? speakers = null)
    {
        var capture = new VoiceCapture { Transmitting = true };
        var sent = new List<int>(); var frame = 0;
        capture.Frame += _ => sent.Add(frame);
        VoiceProcessor.InputLevel.Forget();
        using var processor = new VoiceProcessor(capture, mode);
        var levels = new double[microphone.Length / Frame];
        for (frame = 0; frame < levels.Length; frame++)
        {
            if (speakers is not null) processor.Reference(speakers.AsSpan(frame * Frame, Frame));
            processor.Write(microphone.AsSpan(frame * Frame, Frame));
            levels[frame] = capture.LevelDb;
        }
        return new(levels, sent);
    }
    // The level of the loudest tenth of the samples: what a voice's level means, ignoring its pauses.
    static double ActiveDb(short[] samples)
    {
        var loud = samples.Select(s => Math.Abs((double)s)).OrderDescending().Take(Math.Max(1, samples.Length / 10)).ToArray();
        return 20 * Math.Log10(Math.Sqrt(loud.Average(v => v * v)) / 32768 + 1e-9);
    }
    static double Level(short[] samples) => 10 * Math.Log10(samples.Average(s => s / 32768.0 * (s / 32768.0)) + 1e-10);
    // Fan-like noise: white noise through a gentle low-pass, so most of its energy sits where speech does.
    static short[] Noise(int length, double amplitude, int seed)
    {
        var random = new Random(seed); var result = new short[length]; double low = 0;
        for (var i = 0; i < length; i++) { low = low * 0.7 + (random.NextDouble() * 2 - 1) * 0.3; result[i] = (short)(low * amplitude * 2.5 * 32767); }
        return result;
    }
    // One of a family of impact sounds, chosen by seed: a body ringing low, a sharp contact click, and bounces.
    static short[] Impact(int seed)
    {
        var random = new Random(seed);
        var result = new double[Rate * 3];
        var low = random.Next(4) switch { 0 => random.NextDouble() * 60 + 50, 1 => random.NextDouble() * 150 + 100, 2 => random.NextDouble() * 300 + 250, _ => random.NextDouble() * 3000 + 2000 };
        var amplitude = random.NextDouble() * 0.6 + 0.3; var click = random.NextDouble() * 2; var decay = random.NextDouble() * 40 + 15;
        var at = Rate / 2 + random.Next(Rate / 4);
        for (var bounce = 0; bounce < 1 + random.Next(4) && at < result.Length; bounce++)
        {
            var scale = Math.Pow(0.45, bounce);
            for (var i = 0; i < Rate / 2 && at + i < result.Length; i++)
            {
                var t = (double)i / Rate;
                result[at + i] += amplitude * scale * (Math.Sin(2 * Math.PI * low * t) * Math.Exp(-t * decay) + 0.5 * Math.Sin(2 * Math.PI * low * 2.7 * t) * Math.Exp(-t * decay * 1.6)
                    + (random.NextDouble() * 2 - 1) * click * Math.Exp(-t * 300));
            }
            at += (int)(Rate * (0.12 + random.NextDouble() * 0.25));
        }
        return result.Select(v => (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue)).ToArray()[..(result.Length / Frame * Frame)];
    }
    static short[] Mix(short[] a, short[] b) => a.Select((s, i) => (short)Math.Clamp(s + b[i], short.MinValue, short.MaxValue)).ToArray();
    static short[] Repeat(short[] samples, int length) => Enumerable.Range(0, length).Select(i => samples[i % samples.Length]).ToArray();

    // Synthesized speech at 48 kHz with a second of silence on each side: English where Windows has an English voice,
    // otherwise the first voice it has, speaking its own language. Null without any voice.
    static async Task<short[]?> Speak()
    {
        var voices = SpeechSynthesizer.AllVoices;
        var voice = voices.FirstOrDefault(v => v.Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) ?? voices.FirstOrDefault();
        if (voice is null) return null;
        Console.WriteLine($"Voice: {voice.DisplayName} ({voice.Language})");
        var text = voice.Language.StartsWith("ru-", StringComparison.OrdinalIgnoreCase)
            ? "Всем меня слышно? Давайте включим следующую серию, когда попкорн будет готов. По-моему, концовка прошлой была лучшей частью всего сезона."
            : "Can everyone hear me? Let's start the next episode once the popcorn is ready. I think the ending of the last one was the best part of the whole season.";
        using var synthesizer = new SpeechSynthesizer { Voice = voice };
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        using var input = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
        using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        int rate = 0, channels = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, offset + 10); rate = BitConverter.ToInt32(bytes, offset + 12); }
            if (id == "data" && rate > 0 && channels > 0)
            {
                var frames = Math.Min(size, bytes.Length - offset - 8) / 2 / channels;
                var length = (int)((long)frames * Rate / rate);
                var result = new short[length + 2 * Rate];
                for (var i = 0; i < length; i++)
                {
                    var position = (double)i * rate / Rate; var at = (int)position; var next = Math.Min(at + 1, frames - 1);
                    var a = BitConverter.ToInt16(bytes, offset + 8 + at * 2 * channels); var b = BitConverter.ToInt16(bytes, offset + 8 + next * 2 * channels);
                    result[Rate + i] = (short)(a + (b - a) * (position - at));
                }
                return result[..(result.Length / Frame * Frame)];
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}
