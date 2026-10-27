using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

/// <summary>Writes small, playable Matroska files: 16-bit PCM audio tracks and text subtitle tracks, with or without language tags.</summary>
internal static class MatroskaFixture
{
    public sealed record Audio(short[] Samples, int SampleRate, string? Language = null, string? Name = null);
    /// <summary>
    /// Compression: null, "zlib" or "strip" (header stripping of the first bytes of every event). Payloads replace the lines
    /// as raw events, for picture subtitles; Private is their CodecPrivate.
    /// </summary>
    public sealed record Subtitle(string Codec, string[] Lines, string? Language = null, string? Compression = null, string? Name = null, bool Forced = false,
        byte[][]? Payloads = null, byte[]? Private = null);

    /// <summary>Checksums: every cluster starts with a CRC-32 element, as ffmpeg writes them.</summary>
    public static void Write(string path, IReadOnlyList<Audio> audio, IReadOnlyList<Subtitle> subtitles, bool checksums = false)
    {
        var seconds = audio.Count == 0 ? 20 : audio.Max(a => (a.Samples.Length + a.SampleRate - 1) / a.SampleRate);
        var tracks = new List<byte[]>();
        var number = 1;
        foreach (var track in audio)
            tracks.Add(Element(0xae, UInt(0xd7, (ulong)number), UInt(0x73c5, (ulong)number++), UInt(0x83, 2), Text(0x86, "A_PCM/INT/LIT"),
                track.Language is null ? [] : Text(0x22b59c, track.Language), track.Name is null ? [] : Text(0x536e, track.Name),
                Element(0xe1, Float(0xb5, track.SampleRate), UInt(0x9f, 1), UInt(0x6264, 16))));
        foreach (var track in subtitles)
        {
            var encoding = track.Compression switch
            {
                "zlib" => Element(0x6d80, Element(0x6240, Element(0x5034, UInt(0x4254, 0)))),
                "strip" => Element(0x6d80, Element(0x6240, Element(0x5034, UInt(0x4254, 3), Binary(0x4255, Encoding.UTF8.GetBytes(Prefix(track)))))),
                _ => []
            };
            var codecPrivate = track.Codec == "S_TEXT/ASS" ? Binary(0x63a2, Encoding.UTF8.GetBytes(
                "[Script Info]\nScriptType: v4.00+\n\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,40\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"))
                : track.Private is { } raw ? Binary(0x63a2, raw) : [];
            tracks.Add(Element(0xae, UInt(0xd7, (ulong)number), UInt(0x73c5, (ulong)number++), UInt(0x83, 0x11), Text(0x86, track.Codec),
                track.Language is null ? [] : Text(0x22b59c, track.Language), track.Name is null ? [] : Text(0x536e, track.Name),
                track.Forced ? UInt(0x55aa, 1) : [], codecPrivate, encoding));
        }
        var clusters = new List<byte[]>();
        for (var second = 0; second < seconds; second++)
        {
            var blocks = new List<byte[]> { UInt(0xe7, (ulong)(second * 1000)) };
            if (checksums) blocks.Insert(0, Binary(0xbf, [0x12, 0x34, 0x56, 0x78]));
            for (var i = 0; i < audio.Count; i++)
            {
                var track = audio[i];
                var samples = track.Samples.AsSpan().Slice(Math.Min(track.Samples.Length, second * track.SampleRate));
                samples = samples[..Math.Min(samples.Length, track.SampleRate)];
                var pcm = new byte[track.SampleRate * 2]; // silence pads the last second
                for (var s = 0; s < samples.Length; s++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(s * 2), samples[s]);
                blocks.Add(Binary(0xa3, [(byte)(0x80 | (i + 1)), 0, 0, 0x80, .. pcm]));
            }
            for (var i = 0; i < subtitles.Count; i++)
            {
                var track = subtitles[i];
                // One event every other second, cycling through the lines.
                if (second % 2 != 0) continue;
                var index = second / 2;
                var line = track.Lines.Length == 0 ? "" : track.Lines[index % track.Lines.Length];
                var payload = track.Codec == "S_TEXT/ASS" ? $"{index},0,Default,,0,0,0,,{line}" : line;
                var bytes = track.Payloads is { } payloads ? payloads[index % payloads.Length] : Encoding.UTF8.GetBytes(payload);
                bytes = track.Compression switch
                {
                    "zlib" => Zlib(bytes),
                    "strip" => bytes[Encoding.UTF8.GetByteCount(Prefix(track))..],
                    _ => bytes
                };
                blocks.Add(Element(0xa0, Binary(0xa1, [(byte)(0x80 | (audio.Count + i + 1)), 0, 0, 0, .. bytes]), UInt(0x9b, 1500)));
            }
            clusters.Add(Element(0x1f43b675, [.. blocks]));
        }
        var header = Element(0x1a45dfa3, Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2));
        var segment = Element(0x18538067, [Element(0x1549a966, UInt(0x2ad7b1, 1_000_000), Float(0x4489, seconds * 1000.0), Text(0x4d80, "Watchroom fixture")),
            Element(0x1654ae6b, [.. tracks]), .. clusters]);
        File.WriteAllBytes(path, [.. header, .. segment]);
    }
    // Header stripping removes bytes every event starts with; ASS events here all start with their read order.
    private static string Prefix(Subtitle track) => track.Codec == "S_TEXT/ASS" ? "" : track.Lines[0][..2];

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize)) zlib.Write(data);
        return output.ToArray();
    }
    private static byte[] Id(uint id) => id switch
    {
        <= 0xff => [(byte)id],
        <= 0xffff => [(byte)(id >> 8), (byte)id],
        <= 0xffffff => [(byte)(id >> 16), (byte)(id >> 8), (byte)id],
        _ => [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
    };
    private static byte[] Binary(uint id, byte[] data)
    {
        var size = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)data.Length); size[0] = 0x01;
        return [.. Id(id), .. size, .. data];
    }
    private static byte[] Element(uint id, params byte[][] children) => Binary(id, children.SelectMany(c => c).ToArray());
    private static byte[] UInt(uint id, ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); return Binary(id, bytes); }
    private static byte[] Float(uint id, double value) { var bytes = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(bytes, value); return Binary(id, bytes); }
    private static byte[] Text(uint id, string value) => Binary(id, Encoding.UTF8.GetBytes(value));
}
