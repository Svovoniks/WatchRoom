using System.Text;

/// <summary>
/// Picture subtitle events for the Matroska fixture: an ink mask drawn as a bright fill with a dark outline on a transparent
/// background, encoded as a Blu-ray PGS display set or a DVD VobSub packet.
/// </summary>
internal static class PictureSubtitleFixture
{
    /// <summary>Vertical bars, bar pixels wide with gap pixels between them, leaning right by slant pixels per row up.</summary>
    public static bool[,] Bars(int count, int bar, int gap, int height, double slant = 0)
    {
        var lean = (int)Math.Ceiling(slant * (height - 1));
        var width = count * bar + (count - 1) * gap + lean + 2;
        var ink = new bool[height, width];
        for (var y = 0; y < height; y++)
        {
            var shift = 1 + (int)Math.Round(slant * (height - 1 - y));
            for (var i = 0; i < count; i++)
                for (var x = 0; x < bar; x++) ink[y, shift + i * (bar + gap) + x] = true;
        }
        return ink;
    }

    // Colour 1 fills the ink, colour 2 outlines it, colour 0 is the transparent background.
    private static byte[,] Colours(bool[,] ink)
    {
        int height = ink.GetLength(0), width = ink.GetLength(1);
        var colours = new byte[height + 2, width + 2];
        for (var y = 0; y < height + 2; y++)
            for (var x = 0; x < width + 2; x++)
            {
                bool Ink(int dy, int dx) => y + dy - 1 is var iy && x + dx - 1 is var ix && iy >= 0 && iy < height && ix >= 0 && ix < width && ink[iy, ix];
                colours[y, x] = Ink(0, 0) ? (byte)1 : Ink(-1, 0) || Ink(1, 0) || Ink(0, -1) || Ink(0, 1) ? (byte)2 : (byte)0;
            }
        return colours;
    }

    /// <summary>One display set: presentation, palette, the object, end.</summary>
    public static byte[] Pgs(bool[,] ink)
    {
        var colours = Colours(ink);
        int height = colours.GetLength(0), width = colours.GetLength(1);
        var rle = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width;)
            {
                var colour = colours[y, x]; var run = 1;
                while (x + run < width && colours[y, x + run] == colour && run < 16000) run++;
                rle.Add(0);
                if (colour == 0) { if (run < 64) rle.Add((byte)run); else { rle.Add((byte)(0x40 | run >> 8)); rle.Add((byte)run); } }
                else if (run < 64) { rle.Add((byte)(0x80 | run)); rle.Add(colour); }
                else { rle.Add((byte)(0xc0 | run >> 8)); rle.Add((byte)run); rle.Add(colour); }
                x += run;
            }
            rle.Add(0); rle.Add(0);
        }
        var length = rle.Count + 4;
        byte[] presentation = [0x07, 0x80, 0x04, 0x38, 0x10, 0, 1, 0x80, 0, 0, 1, 0, 0, 0, 0, 0, 0x64, 0x03, 0xe8];
        byte[] palette = [0, 0, 0, 16, 128, 128, 0, 1, 235, 128, 128, 255, 2, 16, 128, 128, 255];
        byte[] header = [0, 0, 0, 0xc0, (byte)(length >> 16), (byte)(length >> 8), (byte)length, (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height];
        return [.. Segment(0x16, presentation), .. Segment(0x14, palette), .. Segment(0x15, [.. header, .. rle]), .. Segment(0x80, [])];
    }
    private static byte[] Segment(byte type, byte[] data) => [type, (byte)(data.Length >> 8), (byte)data.Length, .. data];

    /// <summary>The .idx header VobSub tracks keep as CodecPrivate: black, white, dark grey, then unused entries.</summary>
    public static byte[] VobSubPrivate() =>
        Encoding.ASCII.GetBytes("size: 720x480\npalette: 000000, f0f0f0, 101010, " + string.Join(", ", Enumerable.Repeat("808080", 13)) + "\n");

    /// <summary>One packet: size, control offset, the two interlaced fields of 2-bit runs, then one control sequence.</summary>
    public static byte[] VobSub(bool[,] ink)
    {
        var colours = Colours(ink);
        int height = colours.GetLength(0), width = colours.GetLength(1);
        var nibbles = new List<int>();
        var fields = new List<byte[]>();
        foreach (var first in new[] { 0, 1 })
        {
            nibbles.Clear();
            for (var y = first; y < height; y += 2)
            {
                for (var x = 0; x < width;)
                {
                    var colour = colours[y, x]; var run = 1;
                    while (x + run < width && colours[y, x + run] == colour && run < 255) run++;
                    var value = run << 2 | colour;
                    var digits = run < 4 ? 1 : run < 16 ? 2 : run < 64 ? 3 : 4;
                    for (var d = digits - 1; d >= 0; d--) nibbles.Add(value >> (4 * d) & 0xf);
                    x += run;
                }
                if (nibbles.Count % 2 != 0) nibbles.Add(0);
            }
            var bytes = new byte[nibbles.Count / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(nibbles[2 * i] << 4 | nibbles[2 * i + 1]);
            fields.Add(bytes);
        }
        var top = 4; var bottom = top + fields[0].Length; var control = bottom + fields[1].Length;
        int x1 = 100, y1 = 400, x2 = x1 + width - 1, y2 = y1 + height - 1;
        byte[] sequence = [0, 0, (byte)(control >> 8), (byte)control, 0x01,
            0x03, 0x32, 0x10, // emphasis 2 → 3, emphasis 1 → 2, pattern → 1, background → 0
            0x04, 0xff, 0xf0, // all opaque but the background
            0x05, (byte)(x1 >> 4), (byte)((x1 & 0xf) << 4 | x2 >> 8), (byte)x2, (byte)(y1 >> 4), (byte)((y1 & 0xf) << 4 | y2 >> 8), (byte)y2,
            0x06, (byte)(top >> 8), (byte)top, (byte)(bottom >> 8), (byte)bottom, 0xff];
        var size = control + sequence.Length;
        return [(byte)(size >> 8), (byte)size, (byte)(control >> 8), (byte)control, .. fields[0], .. fields[1], .. sequence];
    }

    /// <summary>The dark runs along one row of a rendered picture: (start, length).</summary>
    public static List<(int Start, int Length)> DarkRuns(Watchroom.Core.SubtitleImage image, int y)
    {
        var runs = new List<(int, int)>();
        for (var x = 0; x < image.Width;)
        {
            if (image.Pixels[y * image.Width + x] >= 128) { x++; continue; }
            var start = x;
            while (x < image.Width && image.Pixels[y * image.Width + x] < 128) x++;
            runs.Add((start, x - start));
        }
        return runs;
    }
}
