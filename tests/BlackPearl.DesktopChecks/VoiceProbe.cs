using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Plays a folder of WAV recordings through the voice processing and reports what the gate would send, grouped by
// the part of each file name before "__": --voice-probe <folder> [Standard|Light|Off]. Set VOICE_PROBE_VERBOSE=1
// for every file with its peak speech probability. Used to tune speech detection against recorded sounds.
static class VoiceProbe
{
    const int Rate = VoicePacket.SampleRate, Frame = VoicePacket.FrameSamples;
    public static void Run(string folder, string? only)
    {
        var modes = only is null ? [VoiceAudio.Processing.Standard, VoiceAudio.Processing.Light] : new[] { Enum.Parse<VoiceAudio.Processing>(only) };
        var verbose = Environment.GetEnvironmentVariable("VOICE_PROBE_VERBOSE") == "1";
        foreach (var mode in modes)
        {
            Console.WriteLine($"== {mode}");
            var totals = new Dictionary<string, (int Files, int Triggered, int Frames)>();
            foreach (var file in Directory.GetFiles(folder, "*.wav").Order())
            {
                if (Read(file) is not { } samples) continue;
                var capture = new VoiceCapture { Transmitting = true };
                var sent = 0; capture.Frame += _ => sent++;
                VoiceProcessor.InputLevel.Forget();
                using var processor = new VoiceProcessor(capture, mode);
                float peak = 0, peakHeard = 0, peakCleaned = 0;
                for (var at = 0; at + Frame <= samples.Length; at += Frame)
                {
                    processor.Write(samples.AsSpan(at, Frame));
                    var (heard, cleaned) = processor.SpeechProbability;
                    peak = Math.Max(peak, Math.Min(heard, cleaned)); peakHeard = Math.Max(peakHeard, heard); peakCleaned = Math.Max(peakCleaned, cleaned);
                }
                var name = Path.GetFileNameWithoutExtension(file);
                var group = name.Split("__")[0];
                var total = totals.GetValueOrDefault(group);
                totals[group] = (total.Files + 1, total.Triggered + (sent > 0 ? 1 : 0), total.Frames + sent);
                if (verbose) Console.WriteLine($"{name,-40} sent {sent,5} peak {peak:F2} (heard {peakHeard:F2}, cleaned {peakCleaned:F2})");
            }
            foreach (var (group, total) in totals.OrderBy(t => t.Key)) Console.WriteLine($"{group,-24} sent from {total.Triggered}/{total.Files} files, {total.Frames} frames");
        }
    }
    // 16-bit PCM WAV, first channel, resampled to 48 kHz after a second of silence.
    static short[]? Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int rate = 0, channels = 0, bits = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, offset + 10); rate = BitConverter.ToInt32(bytes, offset + 12); bits = BitConverter.ToInt16(bytes, offset + 22); }
            if (id == "data" && bits == 16 && rate > 0 && channels > 0)
            {
                var frames = Math.Min(size, bytes.Length - offset - 8) / 2 / channels;
                var length = (int)((long)frames * Rate / rate);
                var result = new short[length + Rate];
                for (var i = 0; i < length; i++)
                {
                    var position = (double)i * rate / Rate; var at = (int)position; var next = Math.Min(at + 1, frames - 1);
                    var a = BitConverter.ToInt16(bytes, offset + 8 + at * 2 * channels); var b = BitConverter.ToInt16(bytes, offset + 8 + next * 2 * channels);
                    result[Rate + i] = (short)(a + (b - a) * (position - at));
                }
                return result;
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}
