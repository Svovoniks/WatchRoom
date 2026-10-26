using System.IO;
using System.Buffers.Binary;
using System.Text;

/// <summary>
/// Writes small, playable MP4 files: 16-bit PCM audio tracks and text subtitle tracks (tx3g or WebVTT), with or without
/// language tags. An untagged track is "und", as muxers write it.
/// </summary>
internal static class Mp4Fixture
{
    public sealed record Audio(short[] Samples, int SampleRate, string? Language = null);
    /// <summary>Codec: "tx3g" or "wvtt". One cue every other second, cycling through the lines.</summary>
    public sealed record Subtitle(string Codec, string[] Lines, string? Language = null);

    public static void Write(string path, IReadOnlyList<Audio> audio, IReadOnlyList<Subtitle> subtitles)
    {
        var seconds = audio.Count == 0 ? 20 : audio.Max(a => (a.Samples.Length + a.SampleRate - 1) / a.SampleRate);
        var ftyp = Box("ftyp", Ascii("isom"), U32(512), Ascii("isom"), Ascii("iso2"), Ascii("mp41"));
        var data = new MemoryStream();
        var traks = new List<byte[]>();
        var number = 1;
        long Offset() => ftyp.Length + 8 + data.Length;
        foreach (var track in audio)
        {
            var offset = Offset();
            var pcm = new byte[track.Samples.Length * 2];
            for (var i = 0; i < track.Samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), track.Samples[i]);
            data.Write(pcm);
            var entry = Box("sowt", new byte[6], U16(1), U16(0), U16(0), U32(0), U16(1), U16(16), U16(0), U16(0), U32((uint)track.SampleRate << 16));
            var table = Box("stbl", Box("stsd", U32(0), U32(1), entry), Box("stts", U32(0), U32(1), U32((uint)track.Samples.Length), U32(1)),
                Box("stsc", U32(0), U32(1), U32(1), U32((uint)track.Samples.Length), U32(1)), Box("stsz", U32(0), U32(2), U32((uint)track.Samples.Length)),
                Box("stco", U32(0), U32(1), U32((uint)offset)));
            traks.Add(Trak(number++, "soun", track.Language, (uint)track.SampleRate, (uint)track.Samples.Length, (uint)seconds * 1000, Box("smhd", U32(0), U32(0)), table));
        }
        foreach (var track in subtitles)
        {
            var offset = Offset();
            var sizes = new List<uint>();
            for (var second = 0; second < seconds; second++)
            {
                var line = second % 2 == 0 ? track.Lines[second / 2 % track.Lines.Length] : null;
                var sample = track.Codec == "wvtt"
                    ? line is null ? Box("vtte") : Box("vttc", Box("payl", Encoding.UTF8.GetBytes(line)))
                    : line is null ? U16(0) : [.. U16((ushort)Encoding.UTF8.GetByteCount(line)), .. Encoding.UTF8.GetBytes(line)];
                data.Write(sample); sizes.Add((uint)sample.Length);
            }
            var entry = track.Codec == "wvtt"
                ? Box("wvtt", new byte[6], U16(1), Box("vttC", Ascii("WEBVTT")))
                : Box("tx3g", new byte[6], U16(1), U32(0), [1, 0xff], U32(0), new byte[8], U16(0), U16(0), U16(1), [0, 18], U32(0xffffffff),
                    Box("ftab", U16(1), U16(1), [5], Ascii("Serif")));
            var table = Box("stbl", Box("stsd", U32(0), U32(1), entry), Box("stts", U32(0), U32(1), U32((uint)seconds), U32(1000)),
                Box("stsc", U32(0), U32(1), U32(1), U32((uint)seconds), U32(1)), Box("stsz", U32(0), U32(0), U32((uint)seconds), [.. sizes.SelectMany(U32)]),
                Box("stco", U32(0), U32(1), U32((uint)offset)));
            traks.Add(Trak(number++, "sbtl", track.Language, 1000, (uint)seconds * 1000, (uint)seconds * 1000, Box(track.Codec == "wvtt" ? "sthd" : "nmhd", U32(0)), table));
        }
        var mvhd = Box("mvhd", U32(0), U32(0), U32(0), U32(1000), U32((uint)seconds * 1000), U32(0x10000), U16(0x100), new byte[10], Matrix(), new byte[24], U32((uint)number));
        using var file = File.Create(path);
        file.Write(ftyp);
        file.Write(U32((uint)(8 + data.Length))); file.Write(Ascii("mdat")); data.Position = 0; data.CopyTo(file);
        file.Write(Box("moov", [mvhd, .. traks]));
    }

    private static byte[] Trak(int number, string handler, string? language, uint timescale, uint duration, uint movieDuration, byte[] header, byte[] table)
    {
        var code = language ?? "und";
        var packed = (ushort)((code[0] - 0x60) << 10 | (code[1] - 0x60) << 5 | (code[2] - 0x60));
        var tkhd = Box("tkhd", U32(3), U32(0), U32(0), U32((uint)number), U32(0), U32(movieDuration), new byte[8], U16(0), U16(0),
            U16(handler == "soun" ? (ushort)0x100 : (ushort)0), U16(0), Matrix(), U32(0), U32(0));
        var mdhd = Box("mdhd", U32(0), U32(0), U32(0), U32(timescale), U32(duration), U16(packed), U16(0));
        var hdlr = Box("hdlr", U32(0), U32(0), Ascii(handler), new byte[12], [0]);
        var dinf = Box("dinf", Box("dref", U32(0), U32(1), Box("url ", U32(1))));
        return Box("trak", tkhd, Box("mdia", mdhd, hdlr, Box("minf", header, dinf, table)));
    }
    private static byte[] Matrix() => [.. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x40000000)];
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(part => part).ToArray();
        return [.. U32((uint)(8 + body.Length)), .. Ascii(type), .. body];
    }
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static byte[] U16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return bytes; }
    private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
}
