using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using Panlingo.LanguageIdentification.CLD3;
using Watchroom.Core;
using Whisper.net;

namespace Watchroom.Desktop;

/// <summary>
/// Identifies the language of untagged tracks with bundled models: Whisper tiny (speech) and CLD3 (subtitle text).
/// Both run offline on the CPU. Results are ISO 639-1 codes, or null when the evidence is too weak to say.
/// </summary>
public static class LanguageDetection
{
    private const int SampleRate = 16000;
    private const int WindowSeconds = 30;
    // Audio is decoded from the start (VLC's stream output can't seek), so listen within the first minutes.
    private const int ListenSeconds = 720;
    private static readonly SemaphoreSlim whisper = new(1, 1);

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "ggml-tiny-q5_1.bin");
    public static bool SpeechAvailable => File.Exists(ModelPath);

    /// <summary>The language of dialogue text, when CLD3 is confident.</summary>
    public static string? Written(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 40) return null;
        try
        {
            using var detector = new CLD3Detector(0, 8192);
            var prediction = detector.PredictLanguage(text);
            if (prediction is null || prediction.IsUnknown() || !prediction.IsReliable || prediction.Probability < 0.7) return null;
            return Code(prediction.Language);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// The spoken language of one audio track: the track's first minutes are decoded to 16 kHz mono and Whisper
    /// listens to three windows of them. Silent windows are skipped; the languages heard must agree on average.
    /// </summary>
    public static async Task<string?> SpokenAsync(string path, int trackId, long durationMs, CancellationToken ct)
    {
        if (!SpeechAvailable) return null;
        var listen = durationMs > 0 ? Math.Min(ListenSeconds, durationMs / 1000.0) : ListenSeconds;
        var samples = await DecodeAsync(path, trackId, listen, ct);
        if (samples is null || samples.Length < SampleRate * 5) return null;
        ct.ThrowIfCancellationRequested();
        var windows = Windows(samples.Length).Select(w => samples.AsMemory(w.Start, w.Length)).Where(w => Loudness(w.Span) > 0.005).ToArray();
        if (windows.Length == 0) return null;
        await whisper.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var factory = WhisperFactory.FromPath(ModelPath);
                using var processor = factory.CreateBuilder().WithLanguage("auto").WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4)).Build();
                var votes = new Dictionary<string, float>();
                foreach (var window in windows)
                {
                    ct.ThrowIfCancellationRequested();
                    var (language, probability) = processor.DetectLanguageWithProbability(window.ToArray());
                    if (Code(language) is { } code) votes[code] = votes.GetValueOrDefault(code) + probability;
                }
                if (votes.Count == 0) return null;
                var best = votes.MaxBy(vote => vote.Value);
                return best.Value / windows.Length >= 0.5 ? best.Key : null;
            }, ct);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException or WhisperModelLoadException) { return null; }
        finally { whisper.Release(); }
    }

    /// <summary>Three windows spread over the decoded audio, the last one at its end; one window for short clips.</summary>
    internal static IEnumerable<(int Start, int Length)> Windows(int samples)
    {
        var window = SampleRate * WindowSeconds;
        if (samples <= window * 3 / 2) { yield return (0, Math.Min(samples, window)); yield break; }
        foreach (var end in new[] { samples * 0.4, samples * 0.7, samples })
        {
            var start = Math.Max(0, (int)end - window);
            yield return (start, Math.Min(window, samples - start));
        }
    }
    private static double Loudness(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (var sample in samples) energy += sample * sample;
        return samples.Length == 0 ? 0 : Math.Sqrt(energy / samples.Length);
    }
    private static string? Code(string? language) => language?.ToLowerInvariant() switch
    {
        null or "" or "und" or "unknown" => null,
        "iw" => "he", "jw" => "jv", "in" => "id", "ji" => "yi",
        // CLD3 tags romanized text ("ja-Latn"); it isn't the language's own subtitles, so don't claim it.
        var code when code.EndsWith("-latn", StringComparison.Ordinal) => null,
        var code => TrackLanguages.Normalize(code)
    };

    // VLC's stream output decodes faster than real time but ignores start positions, so the track is decoded
    // from the beginning to a WAV file and stopped once playback time passes the listening span.
    private static async Task<float[]?> DecodeAsync(string path, int trackId, double seconds, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Watchroom");
        if (directory.Contains('\'') || directory.Contains('"')) directory = Path.Combine(App.DataDirectory, "temp");
        Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, "language-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            // A private engine: no shared state with the playback engine, and nothing rendered or heard.
            using (var engine = new LibVLC("--quiet", "--no-video", "--no-spu", "--no-sub-autodetect-file", "--no-stats", "--no-osd"))
            using (var media = new Media(engine, path, FromType.FromPath))
            using (var player = new MediaPlayer(engine))
            {
                media.AddOption(":sout-all");
                media.AddOption(":no-sout-video");
                media.AddOption(":no-sout-spu");
                media.AddOption(":sout=#duplicate{dst=\"transcode{acodec=s16l,channels=1,samplerate=" + SampleRate +
                    "}:std{access=file,mux=wav,dst='" + wav + "'}\",select=\"es=" + trackId.ToString(CultureInfo.InvariantCulture) + "\"}");
                // Continuations must leave VLC's event thread: stopping from it deadlocks.
                var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                player.EndReached += (_, _) => ended.TrySetResult();
                player.EncounteredError += (_, _) => ended.TrySetResult();
                if (!player.Play(media)) return null;
                var until = (long)(seconds * 1000);
                var deadline = Environment.TickCount64 + 120_000;
                try
                {
                    while (!ended.Task.IsCompleted && player.Time < until && Environment.TickCount64 < deadline)
                        await Task.WhenAny(ended.Task, Task.Delay(25, ct));
                    ct.ThrowIfCancellationRequested();
                }
                finally { await Task.Run(player.Stop); }
            }
            return ReadWav(wav);
        }
        catch (IOException) { return null; }
        finally { try { File.Delete(wav); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static float[]? ReadWav(string path)
    {
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        // VLC writes a canonical 44-byte header; find "data" anyway in case a chunk precedes it.
        var offset = 12;
        while (offset + 8 <= bytes.Length && !(bytes[offset] == 'd' && bytes[offset + 1] == 'a' && bytes[offset + 2] == 't' && bytes[offset + 3] == 'a'))
        {
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (size < 0) return null;
            offset += 8 + size;
        }
        offset += 8;
        if (offset >= bytes.Length) return null;
        var samples = new float[(bytes.Length - offset) / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, offset + i * 2) / 32768f;
        return samples;
    }
}
