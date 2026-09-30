using System.Text;

internal static class VideoFixture
{
    // A generated 12-second, uncompressed AVI. No downloaded media or user files.
    public static void Write(string path)
    {
        const int width = 64, height = 48, frames = 120, size = width * height * 3;
        using var stream = File.Create(path); using var w = new BinaryWriter(stream);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        long Start(string id) { Four(id); w.Write(0); return stream.Position; }
        void End(long start) { var end = stream.Position; stream.Position = start - 4; w.Write((int)(end - start)); stream.Position = end; if ((end & 1) != 0) w.Write((byte)0); }
        var riff = Start("RIFF"); Four("AVI "); var hdrl = Start("LIST"); Four("hdrl");
        var avih = Start("avih"); foreach (var n in new[] { 100000, size * 10, 0, 16, frames, 0, 1, size, width, height, 0, 0, 0, 0 }) w.Write(n); End(avih);
        var strl = Start("LIST"); Four("strl"); var strh = Start("strh"); Four("vids"); Four("DIB "); w.Write(0); w.Write((short)0); w.Write((short)0);
        foreach (var n in new[] { 0, 1, 10, 0, frames, size, -1, 0 }) w.Write(n);
        w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height); End(strh);
        var strf = Start("strf"); w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); foreach (var n in new[] { 0, size, 0, 0, 0, 0 }) w.Write(n); End(strf); End(strl); End(hdrl);
        var movi = Start("LIST"); Four("movi"); var offsets = new List<int>();
        for (var f = 0; f < frames; f++)
        {
            offsets.Add((int)(stream.Position - movi)); var frame = Start("00db");
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { w.Write((byte)(f * 2)); w.Write((byte)(y * 5)); w.Write((byte)(x * 4)); }
            End(frame);
        }
        End(movi); var index = Start("idx1"); foreach (var offset in offsets) { Four("00db"); w.Write(16); w.Write(offset); w.Write(size); } End(index); End(riff);
    }
}
