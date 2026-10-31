using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Watchroom's own microphone processing; Windows' communications pipeline is never used. Every 10 ms of microphone:
// WebRTC's echo canceller (AEC3) removes what the speakers are playing, using a loopback recording of the output as
// its reference; a neural denoiser removes background noise (DeepFilterNet 3, or the lighter RNNoise); and WebRTC's
// digital gain control evens out the level. A stage whose library is missing or fails is skipped, so voice keeps
// working with whatever remains.
//
// Whether it is speech is decided by Silero VAD twice: on the microphone after echo cancellation, and on the same
// audio after noise removal. Both have to agree for 64 ms in a row. Each alone mistakes some knocks, drops, clicks and
// clock ticks for a syllable; they rarely make the same mistake, and a sound that short rarely lasts two chunks.
internal sealed class VoiceProcessor : IDisposable
{
    public const int BlockSamples = VoicePacket.SampleRate / 100;
    // DeepFilterNet gives way to RNNoise when it takes longer than this per 10 ms block on average.
    public const double SlowMilliseconds = 4;
    private readonly VoiceCapture capture;
    private readonly object gate = new(), referenceGate = new();
    private readonly float[] block = new float[BlockSamples], reference = new float[BlockSamples];
    private readonly short[] output = new short[BlockSamples];
    private readonly SpeechDecision decision = new();
    private readonly InputLevel level = new();
    private int filled, referenceFilled;
    private bool speaking;
    private double denoiseMilliseconds;
    private long denoised;
    private WebRtcApm? echo, gain;
    private IDenoiser? denoiser;
    private SpeechDetector? heard, cleaned;
    public bool EchoCancelling => echo is not null;
    public string? DenoiserName => denoiser?.Name;
    public bool DetectsSpeech => heard is not null;
    // Silero's latest speech probabilities before and after noise removal, for diagnostics and checks.
    internal (float Heard, float Cleaned) SpeechProbability => (heard?.Probability ?? 0, cleaned?.Probability ?? heard?.Probability ?? 0);

    public VoiceProcessor(VoiceCapture capture, VoiceAudio.Processing processing)
    {
        this.capture = capture;
        if (processing != VoiceAudio.Processing.Off)
        {
            echo = Try("echo", () => WebRtcApm.EchoCanceller());
            denoiser = processing == VoiceAudio.Processing.Standard ? Try<IDenoiser>("deepfilter", () => new DeepFilter()) : null;
            denoiser ??= Try<IDenoiser>("rnnoise", () => new RnNoise());
            gain = Try("gain", () => WebRtcApm.GainControl());
        }
        heard = Try("speech", () => new SpeechDetector());
        if (heard is not null && denoiser is not null) cleaned = Try("speech", () => new SpeechDetector());
        capture.SpeechDetected = heard is null ? null : false;
        Settle();
    }
    // DeepFilterNet's running normalisation and Silero's state need about a second of audio before they judge sounds
    // well; until then a knock can pass for speech. A second of silence brings them there before the microphone starts.
    private void Settle()
    {
        var silence = new float[BlockSamples];
        for (var i = 0; i < 100; i++)
        {
            Array.Clear(silence);
            denoiser?.Process(silence);
            heard?.Write(silence); cleaned?.Write(silence);
        }
    }
    private static T? Try<T>(string stage, Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or OnnxRuntimeException
            or InvalidOperationException or FileNotFoundException or TypeInitializationException)
        {
            PlaybackDiagnostics.Record("voice-processing-unavailable", new { stage, error = ex.GetType().Name, ex.Message });
            return null;
        }
    }

    // Microphone samples, from the capture thread.
    public void Write(ReadOnlySpan<short> samples)
    {
        lock (gate)
        {
            while (samples.Length > 0)
            {
                var count = Math.Min(samples.Length, BlockSamples - filled);
                for (var i = 0; i < count; i++) block[filled + i] = samples[i] / 32768f;
                filled += count; samples = samples[count..];
                if (filled < BlockSamples) continue;
                filled = 0; Process();
            }
        }
    }
    private void Process()
    {
        echo?.ProcessCapture(block);
        var chunks = heard?.Chunks ?? 0;
        if (heard is { } before && !before.Write(block)) StopDetecting();
        if (denoiser is not null)
        {
            level.Learn(block, heard?.Probability ?? 0);
            level.Apply(block);
        }
        Denoise();
        if (cleaned is { } after && !after.Write(block)) { cleaned = null; after.Dispose(); }
        gain?.ProcessCapture(block);
        for (var i = 0; i < BlockSamples; i++) output[i] = (short)Math.Clamp(MathF.Round(block[i] * 32768f), short.MinValue, short.MaxValue);
        // Speech is decided before the samples reach the gate, so the gate acts on what it is about to encode.
        // Only changes are passed on.
        if (heard is { } detector && detector.Chunks != chunks)
        {
            var now = decision.Next(Math.Min(detector.Probability, cleaned?.Probability ?? 1));
            if (now != speaking) capture.SpeechDetected = speaking = now;
        }
        capture.Write(output);
    }
    private void Denoise()
    {
        if (denoiser is not { } active) return;
        var started = Stopwatch.GetTimestamp();
        if (!active.Process(block))
        {
            PlaybackDiagnostics.Record("voice-processing-failed", new { stage = active.Name });
            active.Dispose(); denoiser = null;
            return;
        }
        if (active is not DeepFilter) return;
        // A computer too slow for DeepFilterNet would fall behind the microphone; RNNoise needs a fraction of the time.
        denoiseMilliseconds = denoiseMilliseconds * 0.99 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.01;
        if (++denoised < 300 || denoiseMilliseconds < SlowMilliseconds) return;
        PlaybackDiagnostics.Record("voice-processing-slow", new { stage = active.Name, ms = Math.Round(denoiseMilliseconds, 2) });
        active.Dispose();
        denoiser = Try<IDenoiser>("rnnoise", () => new RnNoise());
    }
    private void StopDetecting()
    {
        heard?.Dispose(); cleaned?.Dispose(); heard = cleaned = null;
        capture.SpeechDetected = null;
    }

    // What the speakers are playing, from the loopback thread: the echo canceller's reference.
    public void Reference(ReadOnlySpan<short> samples)
    {
        lock (referenceGate)
        {
            if (echo is not { } canceller) return;
            while (samples.Length > 0)
            {
                var count = Math.Min(samples.Length, BlockSamples - referenceFilled);
                for (var i = 0; i < count; i++) reference[referenceFilled + i] = samples[i] / 32768f;
                referenceFilled += count; samples = samples[count..];
                if (referenceFilled < BlockSamples) continue;
                referenceFilled = 0; canceller.ProcessRender(reference);
            }
        }
    }

    public void Dispose()
    {
        lock (gate) lock (referenceGate)
        {
            echo?.Dispose(); gain?.Dispose(); denoiser?.Dispose(); heard?.Dispose(); cleaned?.Dispose();
            echo = gain = null; denoiser = null; heard = cleaned = null;
            capture.SpeechDetected = null;
        }
    }

    // Quiet microphones (a virtual cable, an unboosted or distant microphone) are brought up to a normal speech level
    // before noise removal, which otherwise takes speech much below -35 dBFS for noise and removes it. The level is
    // learned only from blocks the speech detector is confident about, so noise never sets it, and the gain only ever
    // raises a quiet voice, by at most 30 dB. The last level learned carries over to the next time voice opens.
    internal sealed class InputLevel
    {
        public const double TargetDb = -26, MaxGainDb = 30;
        public const float Confident = 0.6f;
        private static double remembered = double.NaN;
        private double speechDb = remembered;
        private float current = float.NaN;
        public double GainDb => double.IsNaN(speechDb) ? 0 : Math.Clamp(TargetDb - speechDb, 0, MaxGainDb);
        public void Learn(float[] samples, float probability)
        {
            if (probability < Confident) return;
            double sum = 0;
            foreach (var sample in samples) sum += sample * sample;
            var db = 10 * Math.Log10(sum / samples.Length + 1e-12);
            // Pauses inside speech are not its level.
            if (db < -80 || !double.IsNaN(speechDb) && db < speechDb - 20) return;
            speechDb = double.IsNaN(speechDb) ? db : speechDb + (db - speechDb) * 0.05;
            remembered = speechDb;
        }
        // Forgets the level carried over between sessions, so checks start from the same place.
        internal static void Forget() => remembered = double.NaN;
        // Never lifts a block past -1 dBFS. Lower gain applies at once, so a loud sound is never boosted even briefly;
        // higher gain rises across the block, so changes never click.
        public void Apply(float[] samples)
        {
            var target = (float)Math.Pow(10, GainDb / 20);
            var peak = 0f;
            foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
            if (peak * target > 0.89f) target = Math.Max(1, 0.89f / peak);
            if (float.IsNaN(current) || target < current) current = target;
            if (current == 1 && target == 1) return;
            for (var i = 0; i < samples.Length; i++) samples[i] *= current + (target - current) * (i + 1) / samples.Length;
            current = target;
        }
    }

    // Speech starts once the probability has been at least 0.5 for two 32 ms chunks in a row, and lasts until it falls
    // below 0.35, as Silero recommends; the voice gate's pre-roll and hangover cover both ends.
    internal sealed class SpeechDecision
    {
        public const float Start = 0.5f, Stop = 0.35f;
        public const int Confirm = 2;
        private bool above;
        private int run;
        public bool Next(float probability)
        {
            above = above ? probability >= Stop : probability >= Start;
            run = above ? run + 1 : 0;
            return run >= Confirm;
        }
    }

    // One 10 ms mono block at a time, in place, samples in [-1, 1]. False when the denoiser has failed.
    internal interface IDenoiser : IDisposable
    {
        string Name { get; }
        bool Process(float[] samples);
    }

    // RNNoise 0.2 (Xiph, BSD-3-Clause) with its built-in model. It works on 10 ms at 48 kHz in 16-bit scale.
    internal sealed class RnNoise : IDenoiser
    {
        private IntPtr state;
        private readonly float[] scaled = new float[BlockSamples];
        public string Name => "rnnoise";
        public RnNoise()
        {
            if (rnnoise_get_frame_size() != BlockSamples) throw new InvalidOperationException("Unexpected RNNoise frame size");
            state = rnnoise_create(IntPtr.Zero);
            if (state == IntPtr.Zero) throw new InvalidOperationException("RNNoise could not start");
        }
        public bool Process(float[] samples)
        {
            for (var i = 0; i < BlockSamples; i++) scaled[i] = samples[i] * 32768f;
            rnnoise_process_frame(state, scaled, scaled);
            for (var i = 0; i < BlockSamples; i++) samples[i] = scaled[i] / 32768f;
            return true;
        }
        public void Dispose() { if (state != IntPtr.Zero) rnnoise_destroy(state); state = IntPtr.Zero; }
        [DllImport("rnnoise")] private static extern int rnnoise_get_frame_size();
        [DllImport("rnnoise")] private static extern IntPtr rnnoise_create(IntPtr model);
        [DllImport("rnnoise")] private static extern void rnnoise_destroy(IntPtr state);
        [DllImport("rnnoise")] private static extern float rnnoise_process_frame(IntPtr state, float[] output, float[] input);
    }

    // DeepFilterNet 3 (MIT or Apache-2.0) through src/native/deepfilter, with the model compiled in. It removes much more
    // than RNNoise, impacts included, at about three times the processor time and 30 ms more delay.
    internal sealed class DeepFilter : IDenoiser
    {
        private IntPtr state;
        private readonly float[] enhanced = new float[BlockSamples];
        public string Name => "deepfilter";
        public DeepFilter()
        {
            state = wr_df_create(100);
            if (state == IntPtr.Zero) throw new InvalidOperationException("DeepFilterNet could not start");
            if ((int)wr_df_frame_length(state) != BlockSamples) { Dispose(); throw new InvalidOperationException("Unexpected DeepFilterNet frame size"); }
        }
        public bool Process(float[] samples)
        {
            if (wr_df_process(state, samples, enhanced) != 0) return false;
            enhanced.CopyTo(samples, 0);
            return true;
        }
        public void Dispose() { if (state != IntPtr.Zero) wr_df_free(state); state = IntPtr.Zero; }
        [DllImport("deepfilter")] private static extern IntPtr wr_df_create(float attenuationLimitDb);
        [DllImport("deepfilter")] private static extern nuint wr_df_frame_length(IntPtr state);
        [DllImport("deepfilter")] private static extern int wr_df_process(IntPtr state, float[] input, float[] output);
        [DllImport("deepfilter")] private static extern void wr_df_free(IntPtr state);
    }

    // WebRTC's audio processing module (BSD-3-Clause) through the C interface of SoundFlow's build. One instance
    // cancels echo, another only controls gain, so the denoiser sits between them and the gain never lifts noise
    // that is about to be removed. Render and capture may run on different threads, as WebRTC intends.
    internal sealed class WebRtcApm : IDisposable
    {
        private IntPtr apm, stream, samples, channels;
        public static WebRtcApm EchoCanceller() => new(config =>
        {
            webrtc_apm_config_set_echo_canceller(config, 1, 0);
            webrtc_apm_config_set_high_pass_filter(config, 1);
        });
        // WebRTC's adaptive digital gain: aims 3 dB below full scale, at most 9 dB of gain, with a limiter.
        public static WebRtcApm GainControl() => new(config => webrtc_apm_config_set_gain_controller1(config, 1, 1, 3, 9, 1));
        private WebRtcApm(Action<IntPtr> configure)
        {
            apm = webrtc_apm_create();
            if (apm == IntPtr.Zero) throw new InvalidOperationException("WebRTC audio processing could not start");
            var config = webrtc_apm_config_create();
            try
            {
                webrtc_apm_config_set_noise_suppression(config, 0, 0);
                webrtc_apm_config_set_pipeline(config, VoicePacket.SampleRate, 0, 0, 0);
                configure(config);
                Check(webrtc_apm_apply_config(apm, config));
            }
            finally { webrtc_apm_config_destroy(config); }
            Check(webrtc_apm_initialize(apm));
            stream = webrtc_apm_stream_config_create(VoicePacket.SampleRate, 1);
            samples = Marshal.AllocHGlobal(BlockSamples * sizeof(float));
            channels = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(channels, samples);
        }
        private void Check(int error) { if (error != 0) { Dispose(); throw new InvalidOperationException("WebRTC audio processing error " + error); } }
        public void ProcessCapture(float[] block)
        {
            Marshal.Copy(block, 0, samples, BlockSamples);
            // A typical capture plus playback delay; AEC3 measures the real one itself.
            webrtc_apm_set_stream_delay_ms(apm, 50);
            if (webrtc_apm_process_stream(apm, channels, stream, stream, channels) == 0) Marshal.Copy(samples, block, 0, BlockSamples);
        }
        private IntPtr renderSamples, renderChannels;
        public void ProcessRender(float[] block)
        {
            if (renderChannels == IntPtr.Zero)
            {
                renderSamples = Marshal.AllocHGlobal(BlockSamples * sizeof(float));
                renderChannels = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(renderChannels, renderSamples);
            }
            Marshal.Copy(block, 0, renderSamples, BlockSamples);
            webrtc_apm_process_reverse_stream(apm, renderChannels, stream, stream, renderChannels);
        }
        public void Dispose()
        {
            if (apm != IntPtr.Zero) webrtc_apm_destroy(apm);
            if (stream != IntPtr.Zero) webrtc_apm_stream_config_destroy(stream);
            foreach (var memory in new[] { samples, channels, renderSamples, renderChannels }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
            apm = stream = samples = channels = renderSamples = renderChannels = IntPtr.Zero;
        }
        private const string Library = "webrtc-apm";
        [DllImport(Library)] private static extern IntPtr webrtc_apm_create();
        [DllImport(Library)] private static extern void webrtc_apm_destroy(IntPtr apm);
        [DllImport(Library)] private static extern IntPtr webrtc_apm_config_create();
        [DllImport(Library)] private static extern void webrtc_apm_config_destroy(IntPtr config);
        [DllImport(Library)] private static extern void webrtc_apm_config_set_echo_canceller(IntPtr config, int enabled, int mobileMode);
        [DllImport(Library)] private static extern void webrtc_apm_config_set_noise_suppression(IntPtr config, int enabled, int level);
        [DllImport(Library)] private static extern void webrtc_apm_config_set_gain_controller1(IntPtr config, int enabled, int mode, int targetLevelDbfs, int compressionGainDb, int limiter);
        [DllImport(Library)] private static extern void webrtc_apm_config_set_high_pass_filter(IntPtr config, int enabled);
        [DllImport(Library)] private static extern void webrtc_apm_config_set_pipeline(IntPtr config, int maxInternalRate, int multiChannelRender, int multiChannelCapture, int downmix);
        [DllImport(Library)] private static extern int webrtc_apm_apply_config(IntPtr apm, IntPtr config);
        [DllImport(Library)] private static extern int webrtc_apm_initialize(IntPtr apm);
        [DllImport(Library)] private static extern IntPtr webrtc_apm_stream_config_create(int sampleRateHz, nuint channels);
        [DllImport(Library)] private static extern void webrtc_apm_stream_config_destroy(IntPtr config);
        [DllImport(Library)] private static extern void webrtc_apm_set_stream_delay_ms(IntPtr apm, int delay);
        [DllImport(Library)] private static extern int webrtc_apm_process_stream(IntPtr apm, IntPtr source, IntPtr input, IntPtr output, IntPtr destination);
        [DllImport(Library)] private static extern int webrtc_apm_process_reverse_stream(IntPtr apm, IntPtr source, IntPtr input, IntPtr output, IntPtr destination);
    }
}

// Silero VAD 6 (MIT) on ONNX Runtime: 32 ms chunks at 16 kHz, each with the previous 64 samples as context.
internal sealed class SpeechDetector : IDisposable
{
    public const int Chunk = 512, Context = 64;
    private static readonly string[] InputNames = ["input", "state", "sr"], OutputNames = ["output", "stateN"];
    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private readonly float[] input = new float[Context + Chunk], state = new float[2 * 128];
    private readonly OrtValue inputValue, stateValue, rateValue;
    // 48 kHz to 16 kHz: a fourth-order low-pass below the new Nyquist rate, then every third sample.
    private readonly Biquad first = new(7000), second = new(7000);
    private int filled, phase;
    public float Probability { get; private set; }
    // Chunks heard so far; it changes whenever Probability is updated.
    public long Chunks { get; private set; }
    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "silero_vad.onnx");

    public SpeechDetector()
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        session = new InferenceSession(ModelPath, options);
        inputValue = OrtValue.CreateTensorValueFromMemory(input, [1, input.Length]);
        stateValue = OrtValue.CreateTensorValueFromMemory(state, [2, 1, 128]);
        rateValue = OrtValue.CreateTensorValueFromMemory(new long[] { 16000 }, []);
    }

    // 48 kHz samples in [-1, 1]. False when the model fails, after which the caller stops using it.
    public bool Write(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            var low = second.Run(first.Run(sample));
            if (++phase < 3) continue;
            phase = 0;
            input[Context + filled++] = low;
            if (filled < Chunk) continue;
            filled = 0;
            try { Infer(); }
            catch (OnnxRuntimeException ex)
            {
                PlaybackDiagnostics.Record("voice-processing-failed", new { stage = "speech", ex.Message });
                return false;
            }
        }
        return true;
    }
    private void Infer()
    {
        using (var outputs = session.Run(run, InputNames, [inputValue, stateValue, rateValue], OutputNames))
        {
            Probability = outputs[0].GetTensorDataAsSpan<float>()[0];
            outputs[1].GetTensorDataAsSpan<float>().CopyTo(state);
        }
        input.AsSpan(Chunk, Context).CopyTo(input);
        Chunks++;
    }
    public void Dispose()
    {
        inputValue.Dispose(); stateValue.Dispose(); rateValue.Dispose();
        run.Dispose(); session.Dispose();
    }
    // A second-order Butterworth low-pass at 48 kHz (transposed direct form II).
    private sealed class Biquad
    {
        private readonly float b0, b1, b2, a1, a2;
        private float z1, z2;
        public Biquad(double hz)
        {
            var w = 2 * Math.PI * hz / VoicePacket.SampleRate;
            var cos = Math.Cos(w); var alpha = Math.Sin(w) / Math.Sqrt(2); var a0 = 1 + alpha;
            b0 = b2 = (float)((1 - cos) / 2 / a0); b1 = (float)((1 - cos) / a0);
            a1 = (float)(-2 * cos / a0); a2 = (float)((1 - alpha) / a0);
        }
        public float Run(float x)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
