using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace BlackPearl.Core;

// MP4 and QuickTime: the track list from moov, and subtitle samples located through each track's sample tables.
// Text samples are tx3g or QuickTime text (a length, then the text) or WebVTT cues (vttc boxes holding payl).
// Fragmented files keep their samples in fragments, outside the tables, so they yield tracks but no text.
public static partial class SubtitleText
{
    private const int Mp4TableLimit = 200_000; // subtitle tracks have a few thousand samples; more is a damaged table
    private const int Mp4Samples = 400;

    private sealed record Mp4Track(ContainerTrack Entry, long[] Offsets, int[] Sizes);

    private static bool IsMp4(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".m4a" or ".3gp" or ".3g2";

    private static IReadOnlyDictionary<int, string> SampleMp4(string path, IReadOnlyCollection<int> numbers, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var file = Open(path);
            foreach (var track in ReadMp4Tracks(file, ct, numbers))
            {
                if (!track.Entry.Text || !numbers.Contains(track.Entry.Number) || track.Sizes.Length == 0) continue;
                var text = new StringBuilder();
                // Spread over the whole track: openings are often songs or signs.
                var step = Math.Max(1.0, track.Sizes.Length / (double)Mp4Samples);
                for (var i = 0.0; i < track.Sizes.Length && text.Length < EnoughCharacters; i += step)
                {
                    ct.ThrowIfCancellationRequested();
                    var index = (int)i;
                    if (track.Sizes[index] is < 2 or > 64 << 10 || track.Offsets[index] + track.Sizes[index] > file.Length) continue;
                    var sample = new byte[track.Sizes[index]];
                    file.Position = track.Offsets[index]; file.ReadExactly(sample);
                    Append(text, Mp4SampleText(track.Entry.Codec, sample));
                }
                if (text.Length > 0) result[track.Entry.Number] = text.ToString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException) { }
        return result;
    }

    private static string Mp4SampleText(string codec, byte[] sample)
    {
        if (codec == "wvtt")
        {
            var text = new StringBuilder();
            foreach (var cue in Mp4Boxes(sample, 0, sample.Length))
                if (cue.Type == "vttc")
                    foreach (var part in Mp4Boxes(sample, cue.Start, cue.End))
                        if (part.Type == "payl") Append(text, Clean(Encoding.UTF8.GetString(sample, part.Start, part.End - part.Start)));
            return text.ToString();
        }
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sample), sample.Length - 2);
        if (length <= 0) return "";
        var utf16 = length >= 2 && sample[2] == 0xfe && sample[3] == 0xff;
        return Clean(utf16 ? Encoding.BigEndianUnicode.GetString(sample, 4, length - 2) : Encoding.UTF8.GetString(sample, 2, length));
    }
    private static IEnumerable<(string Type, int Start, int End)> Mp4Boxes(byte[] data, int from, int end)
    {
        for (var position = from; position + 8 <= end;)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position)), int.MaxValue);
            if (size < 8 || position + size > end) yield break;
            yield return (Encoding.ASCII.GetString(data, position + 4, 4), position + 8, position + size);
            position += size;
        }
    }

    // moov → trak → tkhd (track ID), tref → chap (chapter tracks), mdia → mdhd (language), elng (BCP 47 language), hdlr (kind),
    // minf → stbl. Sample tables are read only for the given track numbers' text tracks.
    private static List<Mp4Track> ReadMp4Tracks(FileStream file, CancellationToken ct, IReadOnlyCollection<int>? tables = null)
    {
        var tracks = new List<Mp4Track>();
        var chapters = new HashSet<int>();
        if (Find(Mp4Children(file, 0, file.Length, ct), "moov") is not { } moov) return tracks;
        foreach (var trak in Mp4Children(file, moov.Start, moov.End, ct))
        {
            if (trak.Type != "trak") continue;
            int number = 0; string? language = null, extended = null; var handler = ""; var codec = ""; (long Start, long End)? stbl = null;
            foreach (var box in Mp4Children(file, trak.Start, trak.End, ct))
            {
                if (box.Type == "tkhd") { var header = Read(file, box.Start, (int)Math.Min(24, box.End - box.Start)); if (header.Length < 16 || header[0] == 1 && header.Length < 24) continue; number = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(header[0] == 1 ? 20 : 12)); }
                if (box.Type == "tref" && Find(Mp4Children(file, box.Start, box.End, ct), "chap") is { } chap)
                {
                    var ids = Read(file, chap.Start, (int)Math.Min(64, chap.End - chap.Start));
                    for (var i = 0; i + 4 <= ids.Length; i += 4) chapters.Add((int)BinaryPrimitives.ReadUInt32BigEndian(ids.AsSpan(i)));
                }
                if (box.Type != "mdia") continue;
                foreach (var part in Mp4Children(file, box.Start, box.End, ct))
                    switch (part.Type)
                    {
                        case "mdhd": { var header = Read(file, part.Start, (int)Math.Min(36, part.End - part.Start)); if (header.Length < (header[0] == 1 ? 34 : 22)) break; language = Mp4Language(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(header[0] == 1 ? 32 : 20))); break; }
                        case "elng": extended = Encoding.ASCII.GetString(Read(file, part.Start + 4, (int)Math.Min(64, part.End - part.Start - 4))).TrimEnd('\0'); break;
                        case "hdlr": handler = Encoding.ASCII.GetString(Read(file, part.Start + 8, 4)); break;
                        case "minf":
                            if (Find(Mp4Children(file, part.Start, part.End, ct), "stbl") is { } table) stbl = (table.Start, table.End);
                            break;
                    }
            }
            var entries = stbl is { } s ? Mp4Children(file, s.Start, s.End, ct) : [];
            if (Find(entries, "stsd") is { } stsd && stsd.End - stsd.Start >= 16)
                codec = Encoding.ASCII.GetString(Read(file, stsd.Start + 12, 4));
            var kind = handler switch { "vide" => ContainerTrackType.Video, "soun" => ContainerTrackType.Audio, "sbtl" or "subt" or "text" => ContainerTrackType.Subtitle, _ => ContainerTrackType.Other };
            var entry = new ContainerTrack(number, kind, codec, string.IsNullOrEmpty(extended) ? language : extended, kind == ContainerTrackType.Subtitle && codec is "tx3g" or "text" or "wvtt");
            var (offsets, sizes) = entry.Text && tables?.Contains(number) == true ? Mp4SampleTable(file, entries) : ([], []);
            tracks.Add(new(entry, offsets, sizes));
        }
        // QuickTime chapter titles are text tracks too; players show them as chapters, never as subtitles.
        return tracks.Select(track => chapters.Contains(track.Entry.Number) && track.Entry.Type == ContainerTrackType.Subtitle
            ? new Mp4Track(track.Entry with { Type = ContainerTrackType.Other, Text = false }, [], []) : track).ToList();
    }

    // ISO 639-2/T packed as three 5-bit letters. Values below 0x400 are QuickTime's Macintosh codes, where 0 (English) is
    // also what tools write for "unknown", so they don't count as a language.
    private static string? Mp4Language(ushort code) => code < 0x400 || code == 0x7fff ? null :
        new string(new[] { (char)((code >> 10 & 31) + 0x60), (char)((code >> 5 & 31) + 0x60), (char)((code & 31) + 0x60) });

    private static (long[] Offsets, int[] Sizes) Mp4SampleTable(FileStream file, List<(string Type, long Start, long End)> stbl)
    {
        if (Find(stbl, "stsz") is not { } stsz || Find(stbl, "stsc") is not { } stsc || (Find(stbl, "stco") ?? Find(stbl, "co64")) is not { } chunkBox) return ([], []);

        var header = Read(file, stsz.Start, 12);
        var fixedSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)); var count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), Mp4TableLimit + 1);
        if (count > Mp4TableLimit) return ([], []);
        var sizes = new int[count];
        if (fixedSize != 0) Array.Fill(sizes, (int)Math.Min(fixedSize, int.MaxValue));
        else { var table = Read(file, stsz.Start + 12, count * 4); for (var i = 0; i < count; i++) sizes[i] = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 4)), int.MaxValue); }

        var wide = chunkBox.Type == "co64";
        var chunkCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(Read(file, chunkBox.Start + 4, 4)), Mp4TableLimit + 1);
        if (chunkCount > Mp4TableLimit) return ([], []);
        var chunkTable = Read(file, chunkBox.Start + 8, chunkCount * (wide ? 8 : 4));
        var chunks = new long[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            chunks[i] = wide ? (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(chunkTable.AsSpan(i * 8)), long.MaxValue) : BinaryPrimitives.ReadUInt32BigEndian(chunkTable.AsSpan(i * 4));

        var runCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(Read(file, stsc.Start + 4, 4)), Mp4TableLimit + 1);
        if (runCount > Mp4TableLimit) return ([], []);
        var runs = Read(file, stsc.Start + 8, runCount * 12);
        var offsets = new long[count]; var sample = 0;
        // Each run: the first chunk it applies to and the samples per chunk, until the next run's first chunk.
        for (var run = 0; run < runCount && sample < count; run++)
        {
            var first = BinaryPrimitives.ReadUInt32BigEndian(runs.AsSpan(run * 12)); var perChunk = BinaryPrimitives.ReadUInt32BigEndian(runs.AsSpan(run * 12 + 4));
            var last = run + 1 < runCount ? BinaryPrimitives.ReadUInt32BigEndian(runs.AsSpan(run * 12 + 12)) - 1 : (uint)chunkCount;
            for (var chunk = first; chunk >= 1 && chunk <= last && chunk <= chunkCount && sample < count; chunk++)
            {
                var offset = chunks[chunk - 1];
                for (var i = 0u; i < perChunk && sample < count; i++) { offsets[sample] = offset; offset += sizes[sample]; sample++; }
            }
        }
        return (offsets[..sample], sizes[..sample]);
    }

    private static (string Type, long Start, long End)? Find(List<(string Type, long Start, long End)> boxes, string type) =>
        boxes.FindIndex(box => box.Type == type) is var index and >= 0 ? boxes[index] : null;
    // The boxes inside [from, end), as their type and the extent of their contents.
    private static List<(string Type, long Start, long End)> Mp4Children(Stream file, long from, long end, CancellationToken ct)
    {
        var boxes = new List<(string, long, long)>();
        var header = new byte[16];
        for (var position = from; position + 8 <= end && boxes.Count < 4096;)
        {
            ct.ThrowIfCancellationRequested();
            file.Position = position; file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header); var start = position + 8;
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (large > long.MaxValue) break;
                size = (long)large; start += 8;
            }
            else if (size == 0) size = end - position;
            if (size < start - position || size > end - position) break;
            boxes.Add((Encoding.ASCII.GetString(header, 4, 4), start, position + size));
            position += size;
        }
        return boxes;
    }
    private static byte[] Read(Stream file, long position, int length)
    {
        if (length < 0 || length > 16 << 20) throw new InvalidDataException("MP4 table too large.");
        var bytes = new byte[length];
        file.Position = position; file.ReadExactly(bytes);
        return bytes;
    }
}
