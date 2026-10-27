using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchroom.Core;

/// <summary>
/// A Matroska or MP4 track in file order. Number is the container's own track number. Language is null when the file
/// doesn't name one; Text marks text subtitles. Name is the track's own title, such as "Commentary" (Matroska only).
/// </summary>
public record ContainerTrack(int Number, ContainerTrackType Type, string Codec, string? Language, bool Text, string? Name = null, bool Forced = false)
{
    /// <summary>
    /// Matroska defaults an absent language to English, and players report it so, but muxers omit it for unknown
    /// languages too; "und" says so outright. Either way the file doesn't really say.
    /// </summary>
    public bool Untagged => TrackLanguages.Normalize(Language) is null;
}
public enum ContainerTrackType { Other, Video, Audio, Subtitle }

// Plain dialogue text from subtitles, for identifying their language when the file doesn't say.
// Matroska is read sparsely: the track list, then a few short windows of clusters across the file.
// MP4 is read from its sample tables (SubtitleText.Mp4.cs).
public static partial class SubtitleText
{
    private const int Windows = 6;
    private const int EnoughCharacters = 2000;

    /// <summary>Every track of a Matroska or MP4 file, in the order players number them. Empty for other containers.</summary>
    public static IReadOnlyList<ContainerTrack> Tracks(string path, CancellationToken ct = default)
    {
        if (!IsMatroska(path) && !IsMp4(path)) return [];
        try
        {
            using var file = Open(path);
            if (IsMp4(path)) return ReadMp4Tracks(file, ct).Select(t => t.Entry).ToArray();
            return ReadTracks(file, ct)?.Tracks.Select(t => t.Entry).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException) { return []; }
    }

    /// <summary>Samples dialogue of the given text subtitle tracks. Tracks without text are absent from the result.</summary>
    public static IReadOnlyDictionary<int, string> Sample(string path, IReadOnlyCollection<int> numbers, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        if (numbers.Count == 0) return result;
        if (IsMp4(path)) return SampleMp4(path, numbers, ct);
        if (!IsMatroska(path)) return result;
        try
        {
            using var file = Open(path);
            if (ReadTracks(file, ct) is not { } header) return result;
            var wanted = header.Tracks.Where(t => t.Entry.Text && numbers.Contains(t.Entry.Number)).ToDictionary(t => t.Entry.Number);
            if (wanted.Count == 0) return result;
            var text = wanted.Keys.ToDictionary(n => n, _ => new StringBuilder());
            var length = file.Length - header.ClustersStart;
            var window = Math.Max(4L << 20, length / 40);
            var scanned = header.ClustersStart; // windows of small files overlap; read each part once
            for (var i = 0; i < Windows && scanned < file.Length && text.Values.Any(b => b.Length < EnoughCharacters); i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = header.ClustersStart + length * (2 * i + 1) / (2 * Windows) - window / 2;
                if (FindCluster(file, Math.Max(scanned, start), ct) is not { } cluster) continue;
                scanned = Math.Min(file.Length, cluster + window);
                ScanBlocks(file, cluster, scanned, wanted, text, ct);
            }
            foreach (var (number, builder) in text)
                if (builder.Length > 0) result[number] = builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException) { }
        return result;
    }

    /// <summary>Dialogue from a subtitle file (SRT, ASS/SSA, WebVTT or MicroDVD), or null when it can't be read.</summary>
    public static string? FromFile(string path, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16 << 20) return null;
            var raw = Decode(File.ReadAllBytes(path));
            ct.ThrowIfCancellationRequested();
            var text = new StringBuilder();
            var ass = raw.Contains("[Events]", StringComparison.OrdinalIgnoreCase);
            foreach (var line in raw.Split('\n'))
            {
                var value = line.Trim();
                if (ass)
                {
                    if (!value.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
                    // Files: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text.
                    Append(text, AssText(value["Dialogue:".Length..], 10));
                }
                else if (value.Length == 0 || value.All(char.IsDigit) || value.Contains("-->") || value.StartsWith("WEBVTT", StringComparison.Ordinal) ||
                    value.StartsWith("NOTE", StringComparison.Ordinal) || value.StartsWith("STYLE", StringComparison.Ordinal)) continue;
                else Append(text, Clean(MicroDvdTiming().Replace(value, "")));
                if (text.Length >= EnoughCharacters * 2) break;
            }
            return text.Length > 0 ? text.ToString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The dialogue of one subtitle event, without styling, positioning or drawing commands.</summary>
    public static string EventText(string codec, string payload) => codec switch
    {
        // Matroska ASS/SSA events: ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, Text.
        "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" => AssText(payload, 9),
        _ => Clean(payload)
    };

    private static string AssText(string fields, int count)
    {
        var parts = fields.Split(',', count);
        if (parts.Length < count) return "";
        var text = parts[^1];
        // Vector drawings ({\p1}...) are shapes, not words.
        if (DrawingMode().IsMatch(text)) return "";
        return Clean(OverrideBlock().Replace(text, "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " "));
    }
    // SRT and Matroska text subtitles carry ASS-style override tags too, such as {\an8} to place a line at the top. Some
    // carry HTML entities such as &lrm; as text, and right-to-left subtitles invisible direction marks; neither is a word.
    private static string Clean(string text)
    {
        text = MarkupTag().Replace(OverrideTag().Replace(text, ""), " ");
        if (text.Contains('&')) text = System.Net.WebUtility.HtmlDecode(text);
        return Whitespace().Replace(DirectionMark().Replace(text, ""), " ").Trim();
    }
    // Karaoke splits song lyrics into one event per syllable ("sa", "些"), layered several times over, and typesetting
    // repeats a sign for each layer: they say little about the language and can crowd out the dialogue. Events shorter
    // than four characters, and repeats of either of the two previous ones, are left out.
    private static void Append(StringBuilder text, string value)
    {
        if (value.Length < 4 || Recent(text, value)) return;
        if (text.Length > 0) text.Append('\n');
        text.Append(value);
    }
    private static bool Recent(StringBuilder text, string value)
    {
        var end = text.Length;
        for (var line = 0; line < 2 && end > 0; line++)
        {
            var start = end;
            while (start > 0 && text[start - 1] != '\n') start--;
            if (end - start == value.Length && text.ToString(start, end - start) == value) return true;
            end = start - 1;
        }
        return false;
    }
    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex OverrideBlock();
    [GeneratedRegex(@"\{[^}]*\\p[1-9]")] private static partial Regex DrawingMode();
    [GeneratedRegex(@"<[^>]*>")] private static partial Regex MarkupTag();
    [GeneratedRegex(@"\{\\[^}]*\}")] private static partial Regex OverrideTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[‎‏‪-‮⁦-⁩]")] private static partial Regex DirectionMark();
    [GeneratedRegex(@"^(\{\d+\}){1,2}")] private static partial Regex MicroDvdTiming();

    private sealed record Track(ContainerTrack Entry, int Compression, byte[] Stripped);
    private sealed record Header(List<Track> Tracks, long ClustersStart);

    private static bool IsMatroska(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mka" or ".mks" or ".webm";
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);

    private static ulong Vint(Stream file, bool id)
    {
        var first = file.ReadByte(); if (first <= 0) throw new InvalidDataException("Invalid EBML header.");
        var mask = 0x80; var length = 1; while ((first & mask) == 0) { mask >>= 1; length++; }
        if (length > (id ? 4 : 8)) throw new InvalidDataException("Invalid EBML length.");
        ulong value = (ulong)(id ? first : first & (mask - 1));
        for (var i = 1; i < length; i++) { var next = file.ReadByte(); if (next < 0) throw new EndOfStreamException(); value = value << 8 | (byte)next; }
        return !id && value == (1UL << (7 * length)) - 1 ? ulong.MaxValue : value;
    }
    private static ulong UInt(Stream file, ulong length)
    {
        if (length > 8) throw new InvalidDataException("Invalid EBML integer.");
        ulong value = 0;
        for (var i = 0UL; i < length; i++) { var next = file.ReadByte(); if (next < 0) throw new EndOfStreamException(); value = value << 8 | (byte)next; }
        return value;
    }
    private static byte[] Bytes(Stream file, ulong length)
    {
        if (length > 1 << 20) throw new InvalidDataException("EBML element too large.");
        var bytes = new byte[length]; file.ReadExactly(bytes); return bytes;
    }
    private static string Ascii(Stream file, ulong length) => Encoding.UTF8.GetString(Bytes(file, length)).TrimEnd('\0');

    // EBML header, then the Segment's children until the first Cluster: Tracks is always among them in practice.
    private static Header? ReadTracks(FileStream file, CancellationToken ct)
    {
        if (Vint(file, true) != 0x1a45dfa3) return null;
        var size = Vint(file, false); if (size == ulong.MaxValue) return null;
        file.Position += (long)size;
        if (Vint(file, true) != 0x18538067) return null;
        var segmentSize = Vint(file, false);
        var end = segmentSize == ulong.MaxValue ? file.Length : Math.Min(file.Length, checked(file.Position + (long)segmentSize));
        List<Track>? tracks = null;
        for (var count = 0; count < 256 && file.Position < end; count++)
        {
            ct.ThrowIfCancellationRequested();
            var start = file.Position; var id = Vint(file, true); var length = Vint(file, false);
            if (id == 0x1f43b675) return tracks is null ? null : new(tracks, start);
            if (length == ulong.MaxValue) return null;
            var next = checked(file.Position + (long)length);
            if (id == 0x1654ae6b) tracks = ReadTrackEntries(file, next, ct);
            file.Position = next;
        }
        return tracks is null ? null : new(tracks, file.Position);
    }
    private static List<Track> ReadTrackEntries(Stream file, long end, CancellationToken ct)
    {
        var tracks = new List<Track>();
        while (file.Position < end)
        {
            ct.ThrowIfCancellationRequested();
            var id = Vint(file, true); var length = Vint(file, false); var next = checked(file.Position + (long)length);
            if (id == 0xae)
            {
                int number = 0; ulong type = 0; string codec = ""; string? language = null, bcp47 = null, name = null; var forced = false; var compression = -1; byte[] stripped = [];
                while (file.Position < next)
                {
                    var field = Vint(file, true); var size = Vint(file, false); var fieldEnd = checked(file.Position + (long)size);
                    switch (field)
                    {
                        case 0xd7: number = (int)UInt(file, size); break;
                        case 0x83: type = UInt(file, size); break;
                        case 0x86: codec = Ascii(file, size); break;
                        case 0x22b59c: language = Ascii(file, size); break;
                        case 0x22b59d: bcp47 = Ascii(file, size); break;
                        case 0x536e: name = Ascii(file, size).Trim(); break;
                        case 0x55aa: forced = UInt(file, size) != 0; break;
                        case 0x6d80: (compression, stripped) = ReadEncodings(file, fieldEnd); break;
                    }
                    file.Position = fieldEnd;
                }
                var kind = type switch { 1 => ContainerTrackType.Video, 2 => ContainerTrackType.Audio, 0x11 => ContainerTrackType.Subtitle, _ => ContainerTrackType.Other };
                var text = kind == ContainerTrackType.Subtitle && (codec.StartsWith("S_TEXT/", StringComparison.Ordinal) || codec is "S_ASS" or "S_SSA");
                tracks.Add(new(new(number, kind, codec, bcp47 ?? language, text, string.IsNullOrEmpty(name) ? null : name, forced), compression, stripped));
            }
            file.Position = next;
        }
        return tracks;
    }
    // ContentEncodings → ContentEncoding → ContentCompression → ContentCompAlgo / ContentCompSettings. Encryption isn't readable.
    private static (int Algorithm, byte[] Settings) ReadEncodings(Stream file, long end)
    {
        int algorithm = -1; byte[] settings = [];
        while (file.Position < end)
        {
            var id = Vint(file, true); var length = Vint(file, false); var next = checked(file.Position + (long)length);
            if (id == 0x6240) continue; // ContentEncoding: descend
            if (id == 0x5034) { algorithm = 0; continue; } // ContentCompression: zlib unless ContentCompAlgo says otherwise
            if (id == 0x4254) algorithm = (int)UInt(file, length);
            else if (id == 0x4255) settings = Bytes(file, length);
            else if (id == 0x5035) return (99, []); // ContentEncryption
            file.Position = next;
        }
        return (algorithm, settings);
    }

    private static long? FindCluster(Stream file, long from, CancellationToken ct)
    {
        var buffer = new byte[64 << 10];
        for (var position = from; position < file.Length; position += buffer.Length - 3)
        {
            ct.ThrowIfCancellationRequested();
            file.Position = position;
            var read = file.ReadAtLeast(buffer, buffer.Length, false);
            for (var i = 0; i + 3 < read; i++)
            {
                if (buffer[i] != 0x1f || buffer[i + 1] != 0x43 || buffer[i + 2] != 0xb6 || buffer[i + 3] != 0x75) continue;
                // Confirm the match: a sane size, then the cluster's Timestamp element, after a CRC-32 element when the muxer
                // writes one (ffmpeg does, in every cluster).
                try
                {
                    file.Position = position + i + 4; var size = Vint(file, false);
                    if (size != ulong.MaxValue && size >= 1UL << 30) continue;
                    var first = Vint(file, true);
                    if (first == 0xbf && Vint(file, false) == 4) { file.Position += 4; first = Vint(file, true); }
                    if (first == 0xe7) return position + i;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
            }
            if (read < buffer.Length) break;
        }
        return null;
    }
    // A flat walk: Segment, Cluster and BlockGroup are entered, every other element skipped. Their IDs don't collide.
    private static void ScanBlocks(FileStream file, long from, long until, Dictionary<int, Track> wanted, Dictionary<int, StringBuilder> text, CancellationToken ct)
    {
        file.Position = from;
        try
        {
            while (file.Position < until)
            {
                ct.ThrowIfCancellationRequested();
                var id = Vint(file, true); var length = Vint(file, false);
                if (id is 0x18538067 or 0x1f43b675 or 0xa0) continue;
                if (length == ulong.MaxValue) return;
                var next = checked(file.Position + (long)length);
                if (next > file.Length) return;
                if (id is 0xa3 or 0xa1 && length > 4)
                {
                    var track = (int)Vint(file, false);
                    if (wanted.TryGetValue(track, out var entry) && text[track].Length < EnoughCharacters)
                    {
                        file.Position += 2; var flags = file.ReadByte();
                        var size = next - file.Position;
                        if ((flags & 0x06) == 0 && size is > 0 and < 64 << 10)
                        {
                            var payload = new byte[size]; file.ReadExactly(payload);
                            if (Payload(entry, payload) is { } decoded)
                                Append(text[track], EventText(entry.Entry.Codec, Encoding.UTF8.GetString(decoded)));
                        }
                    }
                    if (wanted.Keys.All(n => text[n].Length >= EnoughCharacters)) return;
                }
                file.Position = next;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { }
    }
    private static byte[]? Payload(Track track, byte[] data)
    {
        switch (track.Compression)
        {
            case -1: return data;
            case 3: return [.. track.Stripped, .. data];
            case 0:
                try
                {
                    using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
                }
                catch (InvalidDataException) { return null; }
            default: return null;
        }
    }
}
