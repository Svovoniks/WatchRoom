using System.IO;
using System.Runtime.InteropServices;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Exceptions;
using Watchroom.Core;

namespace Watchroom.Desktop;

/// <summary>
/// Reads picture subtitles (PGS, VobSub) with the bundled Tesseract and its script models, offline. The models carry no
/// word lists: the text only has to be close enough for CLD3 to name its language, and a dictionary would lean it to one.
/// </summary>
public static class SubtitleOcr
{
    // Every script's model reads Latin letters too, but only the Latin model reads their accents, so it's tried first.
    private static readonly string[] Scripts = ["Latin", "Cyrillic", "Greek", "Arabic", "Hebrew", "Devanagari", "Thai", "Hangul", "Japanese", "HanS", "HanT", "Vietnamese"];
    private const int TrialImages = 6;
    private const float LatinConfident = 0.8f;
    // Below this, even the best script's reading is mostly guesswork: wrong-script text that CLD3 could still name.
    private const float Readable = 0.4f;
    private static readonly SemaphoreSlim gate = new(1, 1);

    public static string DataPath => Path.Combine(AppContext.BaseDirectory, "Models", "tessdata");
    public static bool Available => File.Exists(Path.Combine(DataPath, "Latin.traineddata"));

    /// <summary>
    /// The text of a track's pictures, one line per picture. The script is the one whose model reads the first few
    /// pictures most confidently; the rest are read with it. Null when no model reads them with confidence.
    /// </summary>
    /// <param name="trace">Receives each script's confidence, for diagnostics.</param>
    public static string? Read(IReadOnlyList<SubtitleImage> images, CancellationToken ct, Action<string>? trace = null)
    {
        if (images.Count == 0 || !Available) return null;
        gate.Wait(ct);
        try
        {
            var trial = images.Take(TrialImages).ToArray();
            string? best = null; var bestConfidence = 0f; List<string> bestText = [];
            foreach (var script in Scripts)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(DataPath, script + ".traineddata"))) continue;
                using var engine = new Engine(DataPath, script, EngineMode.LstmOnly);
                var (confidence, text) = Recognize(engine, trial, ct);
                trace?.Invoke($"{script} {confidence:0.00}: {string.Join(" | ", text.Take(2))}");
                if (confidence > bestConfidence) { best = script; bestConfidence = confidence; bestText = text; }
                if (script == "Latin" && confidence >= LatinConfident) break;
            }
            if (best is null || bestConfidence < Readable) return null;
            if (images.Count <= trial.Length) return Join(bestText);
            using (var engine = new Engine(DataPath, best, EngineMode.LstmOnly))
                bestText.AddRange(Recognize(engine, images.Skip(trial.Length).ToArray(), ct).Text);
            return Join(bestText);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TesseractException) { return null; }
        finally { gate.Release(); }
    }

    private static string? Join(List<string> lines) => lines.Count == 0 ? null : string.Join('\n', lines);

    private static (float Confidence, List<string> Text) Recognize(Engine engine, IReadOnlyList<SubtitleImage> images, CancellationToken ct)
    {
        var text = new List<string>(); var confidence = 0f;
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            using var pix = Pixels(image);
            using var page = engine.Process(pix, PageSegMode.SingleBlock);
            confidence += page.MeanConfidence;
            // A picture is one subtitle, however many lines it has.
            var line = string.Join(' ', page.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (line.Length > 0) text.Add(line);
        }
        return (images.Count == 0 ? 0 : confidence / images.Count, text);
    }

    // Leptonica keeps 8-bit pixels four to a 32-bit word, the first in the highest byte.
    private static TesseractOCR.Pix.Image Pixels(SubtitleImage image)
    {
        var pix = TesseractOCR.Pix.Image.Create(image.Width, image.Height, 8);
        var data = pix.GetData();
        var words = new int[data.WordsPerLine];
        for (var y = 0; y < image.Height; y++)
        {
            Array.Clear(words);
            var row = y * image.Width;
            for (var x = 0; x < image.Width; x++) words[x >> 2] |= image.Pixels[row + x] << (24 - 8 * (x & 3));
            Marshal.Copy(words, 0, (IntPtr)data.PixData + y * data.WordsPerLine * 4, words.Length);
        }
        return pix;
    }
}
