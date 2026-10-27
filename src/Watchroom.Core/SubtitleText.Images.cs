using System.Globalization;
using System.Text;

namespace Watchroom.Core;

/// <summary>One subtitle picture in 8-bit grey, dark text on white as text recognition expects it, cropped to its text.</summary>
public sealed record SubtitleImage(int Width, int Height, byte[] Pixels);

// Picture subtitles in Matroska, decoded for text recognition: Blu-ray PGS and DVD VobSub. Their pictures are drawn
// from a palette of colours with transparency; the text is the bright fill, usually inside a dark outline.
public static partial class SubtitleText
{
    private const int ImagesPerWindow = 6;

    public static bool ImageCodec(string codec) => codec is "S_HDMV/PGS" or "S_VOBSUB";

    /// <summary>Samples pictures of the given PGS and VobSub tracks across the file. Tracks without any are absent from the result.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<SubtitleImage>> SampleImages(string path, IReadOnlyCollection<int> numbers, CancellationToken ct = default)
    {
        var result = new Dictionary<int, IReadOnlyList<SubtitleImage>>();
        if (numbers.Count == 0 || !IsMatroska(path)) return result;
        try
        {
            using var file = Open(path);
            if (ReadTracks(file, ct) is not { } header) return result;
            var wanted = header.Tracks.Where(t => t.Entry.Image && numbers.Contains(t.Entry.Number)).ToDictionary(t => t.Entry.Number);
            if (wanted.Count == 0) return result;
            var images = wanted.Keys.ToDictionary(n => n, _ => new List<SubtitleImage>());
            var inWindow = wanted.Keys.ToDictionary(n => n, _ => 0);
            var palettes = wanted.Keys.ToDictionary(n => n, n => VobSubPalette(wanted[n]));
            // A few pictures from each window, so they come from across the film rather than one scene.
            ScanWindows(file, header, wanted, number => inWindow[number] < ImagesPerWindow, () => { foreach (var n in wanted.Keys) inWindow[n] = 0; },
                (entry, payload) =>
                {
                    var number = entry.Entry.Number;
                    var decoded = entry.Entry.Codec == "S_VOBSUB" ? VobSubImage(payload, palettes[number]) : PgsImages(payload);
                    foreach (var image in decoded.Take(ImagesPerWindow - inWindow[number])) { images[number].Add(image); inWindow[number]++; }
                }, 1 << 20, ct);
            foreach (var (number, list) in images)
                if (list.Count > 0) result[number] = list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException) { }
        return result;
    }

    /// <summary>
    /// The objects of one PGS display set (segments: type, 16-bit size, data). Each object is decoded with the palette of
    /// the same display set; sets that only clear the screen or reuse an earlier object have none.
    /// </summary>
    internal static List<SubtitleImage> PgsImages(byte[] data)
    {
        var images = new List<SubtitleImage>();
        var palette = new (byte Luma, byte Alpha)[256];
        var hasPalette = false;
        var objects = new List<(int Width, int Height, MemoryStream Rle)>();
        (int Width, int Height, MemoryStream Rle)? current = null;
        for (var i = 0; i + 3 <= data.Length;)
        {
            var type = data[i]; var size = data[i + 1] << 8 | data[i + 2];
            var start = i + 3; var end = start + size;
            if (end > data.Length) break;
            switch (type)
            {
                case 0x14: // palette: id, version, then id, Y, Cr, Cb, alpha per entry
                    for (var p = start + 2; p + 5 <= end; p += 5) palette[data[p]] = (data[p + 1], data[p + 4]);
                    hasPalette = true;
                    break;
                case 0x15 when size >= 4: // object: id, version, sequence flags; the first part has length, width, height
                    var flags = data[start + 3];
                    if ((flags & 0x80) != 0 && size >= 11)
                    {
                        current = (data[start + 7] << 8 | data[start + 8], data[start + 9] << 8 | data[start + 10], new MemoryStream());
                        current.Value.Rle.Write(data, start + 11, end - start - 11);
                    }
                    else current?.Rle.Write(data, start + 4, end - start - 4);
                    if ((flags & 0x40) != 0 && current is { } done) { objects.Add(done); current = null; }
                    break;
            }
            i = end;
        }
        if (!hasPalette) return images;
        foreach (var (width, height, rle) in objects)
        {
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096) continue;
            var indexes = PgsPixels(rle.ToArray(), width, height);
            var image = Render(width, height, index => palette[indexes[index]]);
            if (image is not null) images.Add(image);
        }
        return images;
    }
    // Runs: a non-zero byte is one pixel of that colour; 0 then a code byte: 0 ends the line, otherwise the top bits say
    // whether the length is 6 or 14 bits and whether a colour byte follows (else colour 0).
    private static byte[] PgsPixels(byte[] rle, int width, int height)
    {
        var pixels = new byte[width * height];
        int x = 0, y = 0;
        for (var i = 0; i < rle.Length && y < height;)
        {
            int count, colour;
            var value = rle[i++];
            if (value != 0) { count = 1; colour = value; }
            else
            {
                if (i >= rle.Length) break;
                var code = rle[i++];
                if (code == 0) { x = 0; y++; continue; }
                count = code & 0x3f;
                if ((code & 0x40) != 0) { if (i >= rle.Length) break; count = count << 8 | rle[i++]; }
                colour = 0;
                if ((code & 0x80) != 0) { if (i >= rle.Length) break; colour = rle[i++]; }
            }
            for (var n = 0; n < count && x < width; n++) pixels[y * width + x++] = (byte)colour;
        }
        return pixels;
    }

    /// <summary>A VobSub track's 16 colours from its CodecPrivate, the .idx header ("palette: 000000, f0f0f0, ..."), as luma.</summary>
    private static byte[] VobSubPalette(Track track)
    {
        var palette = new byte[16];
        if (track.Entry.Codec != "S_VOBSUB") return palette;
        foreach (var line in Encoding.ASCII.GetString(track.Private).Split('\n'))
        {
            if (!line.TrimStart().StartsWith("palette:", StringComparison.OrdinalIgnoreCase)) continue;
            var colours = line[(line.IndexOf(':') + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < Math.Min(16, colours.Length); i++)
                if (int.TryParse(colours[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    palette[i] = (byte)(((rgb >> 16 & 0xff) * 30 + (rgb >> 8 & 0xff) * 59 + (rgb & 0xff) * 11) / 100);
        }
        return palette;
    }

    /// <summary>
    /// One DVD subtitle packet: its size, the offset of its control sequences, which set the four colours, their
    /// transparency, the picture's area and where its two interlaced fields start; then 2-bit colour runs in nibbles.
    /// </summary>
    internal static List<SubtitleImage> VobSubImage(byte[] data, byte[] palette)
    {
        if (data.Length < 4) return [];
        var control = data[2] << 8 | data[3];
        int[] colours = [0, 0, 0, 0], alphas = [0, 0, 0, 0];
        int x1 = -1, x2 = -1, y1 = -1, y2 = -1, top = -1, bottom = -1;
        for (var sequence = control; sequence + 4 <= data.Length;)
        {
            var nextSequence = data[sequence + 2] << 8 | data[sequence + 3];
            var i = sequence + 4;
            while (i < data.Length)
            {
                var command = data[i++];
                if (command == 0xff) break;
                if (command == 0x03 && i + 2 <= data.Length)
                {
                    // Nibbles: emphasis 2, emphasis 1, pattern, background → colour indexes 3, 2, 1, 0.
                    colours = [data[i + 1] & 0xf, data[i + 1] >> 4, data[i] & 0xf, data[i] >> 4]; i += 2;
                }
                else if (command == 0x04 && i + 2 <= data.Length) { alphas = [data[i + 1] & 0xf, data[i + 1] >> 4, data[i] & 0xf, data[i] >> 4]; i += 2; }
                else if (command == 0x05 && i + 6 <= data.Length)
                {
                    x1 = data[i] << 4 | data[i + 1] >> 4; x2 = (data[i + 1] & 0xf) << 8 | data[i + 2];
                    y1 = data[i + 3] << 4 | data[i + 4] >> 4; y2 = (data[i + 4] & 0xf) << 8 | data[i + 5]; i += 6;
                }
                else if (command == 0x06 && i + 4 <= data.Length) { top = data[i] << 8 | data[i + 1]; bottom = data[i + 2] << 8 | data[i + 3]; i += 4; }
                else if (command is 0x00 or 0x01 or 0x02) { }
                else break; // unknown commands have unknown lengths
            }
            if (nextSequence <= sequence) break;
            sequence = nextSequence;
        }
        var width = x2 - x1 + 1; var height = y2 - y1 + 1;
        if (x1 < 0 || y1 < 0 || top < 4 || bottom < 4 || width <= 0 || height <= 0 || width > 1024 || height > 1024) return [];
        var pixels = new byte[width * height];
        VobSubField(data, top, control, pixels, width, height, 0);
        VobSubField(data, bottom, control, pixels, width, height, 1);
        var image = Render(width, height, index => (palette[colours[pixels[index]]], (byte)(alphas[pixels[index]] * 17)));
        return image is null ? [] : [image];
    }
    // Every other line from one field. A run is 4, 8, 12 or 16 bits: two bits of colour under the length; length 0 fills
    // the rest of the line. Lines start on a byte boundary.
    private static void VobSubField(byte[] data, int offset, int end, byte[] pixels, int width, int height, int first)
    {
        var nibble = offset * 2; var limit = Math.Min(end, data.Length) * 2;
        int Next() => nibble < limit ? (nibble % 2 == 0 ? data[nibble++ / 2] >> 4 : data[nibble++ / 2] & 0xf) : -1;
        for (var y = first; y < height; y += 2)
        {
            for (var x = 0; x < width;)
            {
                var value = Next(); if (value < 0) return;
                if (value < 0x4) { value = value << 4 | Next(); if (value < 0x10) { value = value << 4 | Next(); if (value < 0x40) value = value << 4 | Next(); } }
                if (value < 0) return;
                var count = value >> 2; var colour = value & 3;
                if (count == 0) count = width - x;
                for (var n = 0; n < count && x < width; n++) pixels[y * width + x++] = (byte)colour;
            }
            if (nibble % 2 != 0) nibble++;
        }
    }

    // Composited over black, then inverted: the bright fill becomes dark text on white, the outline and the transparent
    // background white. Cropped to the text and set upright, as text recognition reads italics badly (DVD Japanese most
    // of all), then scaled so its tallest line is about LineHeight pixels, where it reads best, with a margin.
    private static SubtitleImage? Render(int width, int height, Func<int, (byte Luma, byte Alpha)> colour)
    {
        var grey = new byte[width * height];
        int left = width, right = -1, top = height, bottom = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (luma, alpha) = colour(y * width + x);
                var value = luma * alpha / 255;
                grey[y * width + x] = (byte)(255 - value);
                if (value < Ink) continue;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        if (right < 0 || bottom - top < 6) return null;
        int textWidth = right - left + 1, textHeight = bottom - top + 1;
        var slant = Slant(grey, width, left, top, textWidth, textHeight);
        var uprightWidth = textWidth + (int)Math.Round(slant * (textHeight - 1));
        var upright = new byte[uprightWidth * textHeight];
        Array.Fill(upright, (byte)255);
        for (var y = 0; y < textHeight; y++)
        {
            // Lower rows move right: the tops of slanted letters come back over their feet.
            var shift = (int)Math.Round(slant * y);
            Array.Copy(grey, (top + y) * width + left, upright, y * uprightWidth + shift, textWidth);
        }
        var scale = Math.Clamp(LineHeight / (double)TallestLine(upright, uprightWidth, textHeight), 0.25, 3);
        int scaledWidth = Math.Max(1, (int)Math.Round(uprightWidth * scale)), scaledHeight = Math.Max(1, (int)Math.Round(textHeight * scale));
        const int margin = 12;
        var outWidth = scaledWidth + margin * 2; var outHeight = scaledHeight + margin * 2;
        var pixels = new byte[outWidth * outHeight];
        Array.Fill(pixels, (byte)255);
        // Each output pixel averages the source pixels it covers (at least the one under it, when enlarging).
        for (var y = 0; y < scaledHeight; y++)
        {
            int y0 = (int)(y / scale), y1 = Math.Max(y0 + 1, Math.Min(textHeight, (int)((y + 1) / scale)));
            for (var x = 0; x < scaledWidth; x++)
            {
                int x0 = (int)(x / scale), x1 = Math.Max(x0 + 1, Math.Min(uprightWidth, (int)((x + 1) / scale)));
                int sum = 0, count = 0;
                for (var sy = y0; sy < y1 && sy < textHeight; sy++)
                    for (var sx = x0; sx < x1 && sx < uprightWidth; sx++) { sum += upright[sy * uprightWidth + sx]; count++; }
                pixels[(y + margin) * outWidth + margin + x] = (byte)(count == 0 ? 255 : sum / count);
            }
        }
        return new SubtitleImage(outWidth, outHeight, pixels);
    }
    private const int LineHeight = 40;
    // The tallest run of rows with ink: one line of text. Ruby (furigana) above it is shorter.
    private static int TallestLine(byte[] pixels, int width, int height)
    {
        int tallest = 1, run = 0;
        for (var y = 0; y < height; y++)
        {
            var ink = false;
            for (var x = 0; x < width && !ink; x++) ink = 255 - pixels[y * width + x] >= Ink;
            run = ink ? run + 1 : 0;
            tallest = Math.Max(tallest, run);
        }
        return tallest;
    }
    private const int Ink = 96;
    // The shear that stands strokes most upright: the one that piles the ink into the fewest columns (the largest sum of
    // squared column counts). Upright text scores best unsheared.
    private static double Slant(byte[] grey, int width, int left, int top, int textWidth, int textHeight)
    {
        var best = 0.0; long bestScore = -1;
        for (var step = 0; step <= 10; step++)
        {
            var slant = step * 0.05;
            var columns = new int[textWidth + (int)Math.Round(slant * textHeight) + 1];
            for (var y = 0; y < textHeight; y++)
            {
                var shift = (int)Math.Round(slant * y);
                for (var x = 0; x < textWidth; x++)
                    if (255 - grey[(top + y) * width + left + x] >= Ink) columns[x + shift]++;
            }
            long score = 0;
            foreach (var count in columns) score += (long)count * count;
            if (score > bestScore) { bestScore = score; best = slant; }
        }
        return best;
    }
}
