using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using static BlackPearl.Core.SubtitleText;

namespace BlackPearl.Core;

// MP4 and QuickTime: ftyp, then moov → mvhd (the movie), trak per track (tkhd, tref, mdia → mdhd, hdlr, minf → stbl →
// stsd, stts, stsz), and udta/meta (iTunes and QuickTime tags, Nero chapters). QuickTime chapter titles are a text track
// the other tracks reference; they are listed as chapters.
public static partial class VideoFileDetails
{
    private const int Mp4MostEntries = 500_000;
    private static readonly DateTime Mp4Epoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Mp4Track
    {
        public uint Id, Timescale;
        public ulong Duration;
        public bool Enabled = true;
        public int AlternateGroup;
        public double Width, Height;
        public string? Language, HandlerName, Name;
        public string Handler = "";
        public List<(string Type, long Start, long End)> Table = [];
        public uint[] Chapters = [];
    }

    private static void ReadMp4(FileStream file, Details details, CancellationToken ct)
    {
        details.Format = "MPEG-4";
        var top = Mp4Children(file, 0, file.Length, ct);
        if (Find(top, "ftyp") is { } ftyp && ftyp.End - ftyp.Start >= 8)
        {
            var data = SubtitleText.Read(file, ftyp.Start, (int)Math.Min(ftyp.End - ftyp.Start, 256));
            var major = Encoding.ASCII.GetString(data, 0, 4).Trim();
            details.Format = major == "qt" ? "QuickTime" : major.StartsWith("3g", StringComparison.Ordinal) ? "3GPP" : "MPEG-4";
            var compatible = Enumerable.Range(0, (data.Length - 8) / 4).Select(i => Encoding.ASCII.GetString(data, 8 + i * 4, 4).Trim())
                .Where(brand => brand.Length > 0 && brand != major).Distinct().ToArray();
            details.Container.Add("Brand", major + (compatible.Length == 0 ? "" : " (compatible: " + string.Join(", ", compatible) + ")"));
        }
        if (top.Any(box => box.Type == "moof")) details.Container.Add("Fragmented", true);
        if (Find(top, "moov") is not { } moov) return;
        var movie = Mp4Children(file, moov.Start, moov.End, ct);
        if (Find(movie, "mvhd") is { } mvhd && mvhd.End - mvhd.Start >= 20)
        {
            var header = SubtitleText.Read(file, mvhd.Start, (int)Math.Min(32, mvhd.End - mvhd.Start));
            var wide = header[0] == 1 && header.Length >= 32;
            ulong created = wide ? BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            ulong modified = wide ? BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var timescale = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(wide ? 20 : 12));
            ulong duration = wide ? BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
            if (timescale > 0 && duration is > 0 and not uint.MaxValue and not ulong.MaxValue) details.Duration = TimeSpan.FromSeconds(duration / (double)timescale);
            details.Container.Add("Encoded", Mp4Date(created));
            if (modified != created) details.Container.Add("Tagged", Mp4Date(modified));
        }

        var tracks = movie.Where(box => box.Type == "trak").Select(box => ReadMp4Track(file, box, ct)).ToList();
        foreach (var box in movie)
            if (box.Type == "udta") ReadMp4UserData(file, box, details, ct);
            else if (box.Type == "meta") ReadMp4Meta(file, box, details.Container, ct);
        var chapters = tracks.SelectMany(track => track.Chapters).ToHashSet();
        foreach (var track in tracks)
            if (chapters.Contains(track.Id) && track.Handler is "text" or "sbtl" or "subt") ReadMp4ChapterTrack(file, track, details, ct);
            else AddMp4Track(file, track, details, ct);
    }

    private static string? Mp4Date(ulong seconds)
    {
        if (seconds == 0 || seconds > 200UL * 365 * 86400) return null;
        var date = Mp4Epoch.AddSeconds(seconds);
        return date.Year < 1971 ? null : UtcDate(date);
    }

    private static Mp4Track ReadMp4Track(FileStream file, (string Type, long Start, long End) trak, CancellationToken ct)
    {
        var track = new Mp4Track();
        string? extended = null;
        foreach (var box in Mp4Children(file, trak.Start, trak.End, ct))
            switch (box.Type)
            {
                case "tkhd":
                {
                    var header = SubtitleText.Read(file, box.Start, (int)Math.Min(96, box.End - box.Start));
                    var wide = header.Length > 0 && header[0] == 1;
                    if (header.Length < (wide ? 96 : 84)) break;
                    track.Enabled = (header[3] & 1) != 0;
                    track.Id = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(wide ? 20 : 12));
                    track.AlternateGroup = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(wide ? 46 : 34));
                    track.Width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(wide ? 88 : 76)) / 65536.0;
                    track.Height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(wide ? 92 : 80)) / 65536.0;
                    break;
                }
                case "tref":
                    if (Find(Mp4Children(file, box.Start, box.End, ct), "chap") is { } chap)
                    {
                        var ids = SubtitleText.Read(file, chap.Start, (int)Math.Min(256, chap.End - chap.Start));
                        track.Chapters = Enumerable.Range(0, ids.Length / 4).Select(i => BinaryPrimitives.ReadUInt32BigEndian(ids.AsSpan(i * 4))).ToArray();
                    }
                    break;
                case "udta":
                    if (Find(Mp4Children(file, box.Start, box.End, ct), "name") is { } name)
                        track.Name = Decode(SubtitleText.Read(file, name.Start, (int)Math.Min(1024, name.End - name.Start)));
                    break;
                case "mdia":
                    foreach (var part in Mp4Children(file, box.Start, box.End, ct))
                        switch (part.Type)
                        {
                            case "mdhd":
                            {
                                var header = SubtitleText.Read(file, part.Start, (int)Math.Min(36, part.End - part.Start));
                                var wide = header.Length > 0 && header[0] == 1;
                                if (header.Length < (wide ? 34 : 22)) break;
                                track.Timescale = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(wide ? 20 : 12));
                                track.Duration = wide ? BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
                                track.Language = Mp4Language(BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(wide ? 32 : 20)));
                                break;
                            }
                            case "elng":
                                extended = Encoding.ASCII.GetString(SubtitleText.Read(file, part.Start + 4, (int)Math.Min(64, part.End - part.Start - 4))).TrimEnd('\0');
                                break;
                            case "hdlr":
                            {
                                var handler = SubtitleText.Read(file, part.Start, (int)Math.Min(512, part.End - part.Start));
                                if (handler.Length < 12) break;
                                track.Handler = Encoding.ASCII.GetString(handler, 8, 4);
                                if (handler.Length > 24)
                                {
                                    // ISO writes a C string; QuickTime a Pascal string, its length first.
                                    var text = handler[24] == handler.Length - 25 && handler[24] > 0 ? handler[25..] : handler[24..];
                                    track.HandlerName = Decode(text);
                                }
                                break;
                            }
                            case "minf":
                                if (Find(Mp4Children(file, part.Start, part.End, ct), "stbl") is { } table) track.Table = Mp4Children(file, table.Start, table.End, ct);
                                break;
                        }
                    break;
            }
        if (!string.IsNullOrEmpty(extended)) track.Language = extended;
        return track;
    }

    private static void AddMp4Track(FileStream file, Mp4Track t, Details details, CancellationToken ct)
    {
        var kind = t.Handler switch { "vide" => Kind.Video, "soun" => Kind.Audio, "sbtl" or "subt" or "text" or "clcp" or "subp" => Kind.Subtitles, _ => Kind.Other };
        var track = details.Track(kind, t.Handler switch
        {
            "tmcd" => "Timecode", "meta" or "mdta" => "Metadata", "hint" => "Hint", "data" => "Data", "" => "Track", var other => "Track (" + other.Trim() + ")"
        });
        var entry = Find(t.Table, "stsd") is { } stsd ? SubtitleText.Read(file, stsd.Start, (int)Math.Min(stsd.End - stsd.Start, 1 << 20)) : [];
        string? format = null; var encrypted = false;
        double? averageBitrate = null, maximumBitrate = null;
        if (entry.Length >= 16)
        {
            var c = 16; var end = (int)Math.Min(entry.Length, 8L + BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8)));
            format = Encoding.ASCII.GetString(entry, 12, 4);
            IEnumerable<(string Type, int Start, int End)> children = [];
            if (kind == Kind.Video && end >= c + 78)
            {
                children = Mp4Boxes(entry, c + 78, end).ToList();
                encrypted = format == "encv";
                if (encrypted) format = OriginalFormat(entry, children) ?? format;
                double width = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(c + 24)), height = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(c + 26));
                var (h, v) = FindIn(children, "pasp") is { } pasp && pasp.End - pasp.Start >= 8
                    ? (BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(pasp.Start)), BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(pasp.Start + 4))) : (1u, 1u);
                var displayWidth = t.Width > 0 ? t.Width : h > 0 && v > 0 ? width * h / v : width;
                var displayHeight = t.Height > 0 ? t.Height : height;
                track.Add("Codec", Mp4Codec(format, entry, children));
                foreach (var config in new[] { "avcC", "hvcC", "av1C", "vpcC" })
                    if (FindIn(children, config) is { } box) AddCodecProfile(track, config, entry[box.Start..box.End]);
                track.Add("Codec ID", format.Trim() + (entry[c + 42] is > 0 and < 32 && Decode(entry[(c + 43)..(c + 43 + entry[c + 42])]) is { Length: > 0 } compressor ? " (" + compressor + ")" : ""));
                AddPicture(track, (long)width, (long)height, (long)Math.Round(displayWidth), (long)Math.Round(displayHeight));
                if (h > 0 && v > 0 && h != v) track.Add("Pixel aspect ratio", $"{h}:{v}");
                AddMp4Color(track, entry, children, format);
                if (FindIn(children, "fiel") is { } fiel && fiel.End > fiel.Start) track.Add("Scan type", entry[fiel.Start] == 2 ? "Interlaced" : "Progressive");
                if (FindIn(children, "btrt") is { } btrt && btrt.End - btrt.Start >= 12)
                { maximumBitrate = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(btrt.Start + 4)); averageBitrate = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(btrt.Start + 8)); }
            }
            else if (kind == Kind.Audio && end >= c + 28)
            {
                var version = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(c + 8));
                var first = version switch { 1 => c + 44, 2 => c + 64, _ => c + 28 };
                children = first <= end ? Mp4Boxes(entry, first, end).ToList() : [];
                // QuickTime keeps the decoder's configuration in a 'wave' box.
                if (FindIn(children, "wave") is { } wave) children = children.Concat(Mp4Boxes(entry, wave.Start, wave.End)).ToList();
                encrypted = format == "enca";
                if (encrypted) format = OriginalFormat(entry, children) ?? format;
                long channels = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(c + 16)); long bits = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(c + 18));
                double rate = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(c + 24)) >> 16;
                if (version == 2 && end >= c + 52)
                {
                    rate = BinaryPrimitives.ReadDoubleBigEndian(entry.AsSpan(c + 32));
                    channels = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(c + 40)); bits = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(c + 48));
                }
                var esds = FindIn(children, "esds") is { } box ? Esds(entry[box.Start..box.End]) : null;
                // MP4 writes 2 channels for any AAC; its own configuration has the real layout.
                if (format == "mp4a" && esds is { ObjectType: 0x40 or 0x66 or 0x67 or 0x68 } && AacChannels(esds.Config) is { } aacChannels) channels = aacChannels;
                track.Add("Codec", Mp4Codec(format, entry, children, esds));
                if (FindIn(children, "dac3") is { } dac3 && dac3.End - dac3.Start >= 3)
                {
                    var (ac3Channels, ac3Bitrate) = Ac3(entry.AsSpan(dac3.Start, 3));
                    if (ac3Channels > 0) channels = ac3Channels;
                    averageBitrate = ac3Bitrate;
                }
                if (FindIn(children, "dec3") is { } dec3 && dec3.End - dec3.Start >= 5)
                {
                    var (ec3Channels, ec3Bitrate, atmos) = Ec3(entry[dec3.Start..dec3.End]);
                    if (ec3Channels > 0) channels = ec3Channels;
                    averageBitrate = ec3Bitrate;
                    if (atmos) track.Add("Dolby Atmos", true);
                }
                if (FindIn(children, "dOps") is { } dops && dops.End - dops.Start >= 8)
                { channels = entry[dops.Start + 1]; rate = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(dops.Start + 4)) is var input and > 0 ? input : rate; }
                track.Add("Codec ID", format.Trim() + (esds is { ObjectType: > 0 } ? $" (object type 0x{esds.ObjectType:X2})" : ""));
                AddAudio(track, channels, rate, format is "sowt" or "twos" or "lpcm" or "ipcm" or "in24" or "in32" or "fl32" or "fl64" or "alac" or "fLaC" && bits > 0 ? bits : null);
                if (esds is not null) { averageBitrate ??= esds.Average > 0 ? esds.Average : null; maximumBitrate = esds.Maximum > 0 ? esds.Maximum : null; }
                if (FindIn(children, "btrt") is { } btrt && btrt.End - btrt.Start >= 12)
                { maximumBitrate ??= BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(btrt.Start + 4)); averageBitrate ??= BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(btrt.Start + 8)); }
            }
            else
            {
                track.Add("Codec", Mp4Codec(format, entry, []));
                track.Add("Codec ID", format.Trim());
            }
        }
        var seconds = t.Timescale > 0 && t.Duration is > 0 and not uint.MaxValue and not ulong.MaxValue ? t.Duration / (double)t.Timescale : 0;
        var (samples, bytes) = Find(t.Table, "stsz") is { } stsz ? SampleSizes(file, stsz, ct) : (0, 0);
        if (bytes > 0 && seconds > 0) averageBitrate = bytes * 8 / seconds;
        track.Add("Bitrate", averageBitrate is > 0 ? Bitrate(averageBitrate.Value) : null);
        if (maximumBitrate > averageBitrate * 1.01) track.Add("Maximum bitrate", Bitrate(maximumBitrate.Value));
        if (kind == Kind.Video && Find(t.Table, "stts") is { } stts && t.Timescale > 0 && FrameTiming(file, stts, ct) is { } timing && timing.Ticks > 0)
        {
            track.Add("Frame rate", FrameRate(timing.Frames * (double)t.Timescale / timing.Ticks));
            track.Add("Frame rate mode", timing.Constant ? "Constant" : "Variable");
        }
        if (seconds > 0) track.Add("Duration", Duration(TimeSpan.FromSeconds(seconds), true));
        if (kind == Kind.Video && samples > 0) track.Add("Frames", samples.ToString("N0", CultureInfo.CurrentCulture));
        if (kind == Kind.Subtitles && samples > 0) track.Add("Events", samples.ToString("N0", CultureInfo.CurrentCulture));
        if (bytes > 0) track.Add("Stream size", Size(bytes));
        AddLanguage(track, t.Language);
        track.Add("Name", t.Name);
        if (!string.Equals(t.HandlerName, t.Name, StringComparison.Ordinal)) track.Add("Handler name", t.HandlerName);
        track.Add("Enabled", t.Enabled);
        if (t.AlternateGroup > 0) track.Add("Alternate group", t.AlternateGroup.ToString(CultureInfo.InvariantCulture));
        if (encrypted) track.Add("Encrypted", true);
        track.Add("Track ID", t.Id > 0 ? t.Id.ToString(CultureInfo.InvariantCulture) : null);
    }

    private static string? OriginalFormat(byte[] entry, IEnumerable<(string Type, int Start, int End)> children) =>
        children.FirstOrDefault(box => box.Type == "sinf") is { Type: "sinf" } sinf && Mp4Boxes(entry, sinf.Start, sinf.End).FirstOrDefault(box => box.Type == "frma") is { Type: "frma" } frma &&
        frma.End - frma.Start >= 4 ? Encoding.ASCII.GetString(entry, frma.Start, 4) : null;
    private static (string Type, int Start, int End)? FindIn(IEnumerable<(string Type, int Start, int End)> boxes, string type) =>
        boxes.FirstOrDefault(box => box.Type == type) is { Type: not null } box && box.Type == type ? box : null;

    private sealed record EsDescriptor(int ObjectType, uint Maximum, uint Average, byte[] Config);
    // ES_Descriptor (tag 3) → DecoderConfigDescriptor (tag 4: object type, bitrates) → DecoderSpecificInfo (tag 5).
    private static EsDescriptor? Esds(byte[] data)
    {
        var position = 4; int objectType = 0; uint maximum = 0, average = 0; byte[] config = [];
        while (position + 2 <= data.Length)
        {
            var tag = data[position++]; var length = 0;
            for (var i = 0; i < 4 && position < data.Length; i++) { var b = data[position++]; length = length << 7 | b & 0x7f; if ((b & 0x80) == 0) break; }
            if (tag == 3)
            {
                if (position + 3 > data.Length) break;
                var flags = data[position + 2]; position += 3;
                if ((flags & 0x80) != 0) position += 2;
                if ((flags & 0x40) != 0 && position < data.Length) position += 1 + data[position];
                if ((flags & 0x20) != 0) position += 2;
            }
            else if (tag == 4)
            {
                if (position + 13 > data.Length) break;
                objectType = data[position];
                maximum = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 5)); average = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 9));
                position += 13;
            }
            else if (tag == 5) { config = data[position..Math.Min(data.Length, position + length)]; break; }
            else position += length;
        }
        return objectType == 0 && config.Length == 0 ? null : new(objectType, maximum, average, config);
    }
    private static readonly int[] Ac3Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
    private static readonly int[] AcmodChannels = [2, 1, 2, 3, 3, 4, 4, 5];
    private static (int Channels, double? Bitrate) Ac3(ReadOnlySpan<byte> data)
    {
        // fscod 2, bsid 5, bsmod 3, acmod 3, lfeon 1, bit_rate_code 5.
        var bits = data[0] << 16 | data[1] << 8 | data[2];
        var acmod = bits >> 11 & 7; var lfe = bits >> 10 & 1; var rate = bits >> 5 & 31;
        return (AcmodChannels[acmod] + lfe, rate < Ac3Rates.Length ? Ac3Rates[rate] * 1000.0 : null);
    }
    private static (int Channels, double? Bitrate, bool Atmos) Ec3(byte[] data)
    {
        var reader = new BitReader(data);
        var rate = reader.Read(13); var substreams = reader.Read(3) + 1; var channels = 0;
        for (var i = 0; i < substreams; i++)
        {
            reader.Read(2); reader.Read(5); reader.Read(1); reader.Read(1); reader.Read(3);
            var acmod = reader.Read(3); var lfe = reader.Read(1); reader.Read(3);
            var dependent = reader.Read(4);
            if (dependent > 0) reader.Read(9); else reader.Read(1);
            if (i == 0) channels = AcmodChannels[acmod] + lfe;
        }
        // Dolby Atmos in E-AC-3: flag_ec3_extension_type_a, after seven reserved bits.
        var atmos = reader.Remaining >= 8 && (reader.Read(8) & 1) == 1;
        return (channels, rate > 0 ? rate * 1000.0 : null, atmos);
    }
    private sealed class BitReader(byte[] data)
    {
        private int position;
        public int Remaining => data.Length * 8 - position;
        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, position++)
                value = value << 1 | (position < data.Length * 8 ? data[position >> 3] >> 7 - (position & 7) & 1 : 0);
            return value;
        }
    }

    private static string Mp4Codec(string format, byte[] entry, IEnumerable<(string Type, int Start, int End)> children, EsDescriptor? esds = null)
    {
        var dolby = FindIn(children, "dvcC") is not null || FindIn(children, "dvvC") is not null || FindIn(children, "dvwC") is not null;
        switch (format)
        {
            case "mp4a" when esds is not null:
                return esds.ObjectType switch
                {
                    0x40 or 0x66 or 0x67 or 0x68 => AacProfile(esds.Config) is { } profile ? "AAC " + profile : "AAC",
                    0x69 or 0x6b => "MP3", 0xa5 => "AC-3 (Dolby Digital)", 0xa6 => "E-AC-3 (Dolby Digital Plus)", 0xa9 => "DTS", 0xad => "Opus", 0xdd => "Vorbis",
                    _ => "MPEG-4 audio"
                };
            case "mp4v" when FindIn(children, "esds") is { } box && Esds(entry[box.Start..box.End]) is { } video:
                return video.ObjectType switch { 0x20 => "MPEG-4 Visual", >= 0x60 and <= 0x65 => "MPEG-2 Video", 0x6a => "MPEG-1 Video", 0x6c => "JPEG", _ => "MPEG-4 Visual" };
        }
        return format switch
        {
            "avc1" or "avc2" or "avc3" or "avc4" => "H.264 (AVC)", "hvc1" or "hev1" => dolby ? "H.265 (HEVC), Dolby Vision" : "H.265 (HEVC)",
            "dvh1" or "dvhe" => "H.265 (HEVC), Dolby Vision", "dva1" or "dvav" => "H.264 (AVC), Dolby Vision", "dav1" => "AV1, Dolby Vision",
            "vvc1" or "vvi1" => "H.266 (VVC)", "av01" => "AV1", "vp09" => "VP9", "vp08" => "VP8", "mp4v" => "MPEG-4 Visual", "s263" or "h263" => "H.263",
            "jpeg" or "mjpa" or "mjpb" => "Motion JPEG", "apch" or "apcn" or "apcs" or "apco" or "ap4h" or "ap4x" => "Apple ProRes", "mp2v" or "mx5p" or "xdvc" => "MPEG-2 Video",
            "mp4a" => "AAC", "ac-3" => "AC-3 (Dolby Digital)", "ec-3" => "E-AC-3 (Dolby Digital Plus)", "ac-4" => "AC-4", "Opus" => "Opus", "fLaC" => "FLAC",
            "alac" => "Apple Lossless (ALAC)", "mlpa" => "Dolby TrueHD", "dtsc" => "DTS", "dtsh" => "DTS-HD", "dtsl" => "DTS-HD Master Audio", "dtse" => "DTS Express",
            "dtsx" => "DTS:X", ".mp3" => "MP3", "sowt" or "twos" or "lpcm" or "ipcm" or "in24" or "in32" or "raw " or "NONE" => "PCM", "fl32" or "fl64" => "PCM (floating point)",
            "samr" => "AMR", "sawb" => "AMR-WB", "ulaw" => "µ-law", "alaw" => "A-law", "ima4" => "IMA ADPCM",
            "tx3g" => "Timed Text (tx3g)", "mp4s" => "VobSub (DVD pictures)", "text" => "QuickTime text", "wvtt" => "WebVTT", "stpp" => "TTML", "c608" => "CEA-608 captions", "c708" => "CEA-708 captions",
            "tmcd" => "Timecode", "mett" or "metx" or "mebx" => "Timed metadata",
            _ => format.Trim()
        };
    }

    private static void AddMp4Color(Section track, byte[] entry, IEnumerable<(string Type, int Start, int End)> children, string format)
    {
        ulong? primaries = null, transfer = null, matrix = null, range = null, maxCll = null, maxFall = null;
        if (FindIn(children, "colr") is { } colr && colr.End - colr.Start >= 4)
        {
            var type = Encoding.ASCII.GetString(entry, colr.Start, 4);
            if (type is "nclx" or "nclc" && colr.End - colr.Start >= 10)
            {
                primaries = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(colr.Start + 4));
                transfer = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(colr.Start + 6));
                matrix = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(colr.Start + 8));
                if (type == "nclx" && colr.End - colr.Start >= 11) range = (entry[colr.Start + 10] & 0x80) != 0 ? 2UL : 1UL;
            }
            else if (type is "prof" or "rICC") track.Add("Color profile", "Embedded ICC profile");
        }
        if (FindIn(children, "clli") is { } clli && clli.End - clli.Start >= 4)
        { maxCll = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(clli.Start)); maxFall = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(clli.Start + 2)); }
        var dolby = FindIn(children, "dvcC") is not null || FindIn(children, "dvvC") is not null || FindIn(children, "dvwC") is not null || format is "dvh1" or "dvhe" or "dva1" or "dvav" or "dav1";
        AddColor(track, primaries, transfer, matrix, range, maxCll, maxFall, FindIn(children, "mdcv") is not null, dolby);
    }

    private static (long Samples, long Bytes) SampleSizes(FileStream file, (string Type, long Start, long End) stsz, CancellationToken ct)
    {
        if (stsz.End - stsz.Start < 12) return (0, 0);
        var header = SubtitleText.Read(file, stsz.Start, 12);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)), count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        if (size != 0) return (count, size * count);
        count = Math.Min(count, (stsz.End - stsz.Start - 12) / 4);
        long total = 0; var buffer = new byte[64 << 10];
        file.Position = stsz.Start + 12;
        for (var remaining = count * 4; remaining > 0;)
        {
            ct.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, remaining);
            file.ReadExactly(buffer, 0, read);
            for (var i = 0; i + 4 <= read; i += 4) total += BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i));
            remaining -= read;
        }
        return (count, total);
    }
    private static (long Frames, long Ticks, bool Constant)? FrameTiming(FileStream file, (string Type, long Start, long End) stts, CancellationToken ct)
    {
        if (stts.End - stts.Start < 8) return null;
        var entries = (int)Math.Min(Math.Min(BinaryPrimitives.ReadUInt32BigEndian(SubtitleText.Read(file, stts.Start + 4, 4)), Mp4MostEntries), (stts.End - stts.Start - 8) / 8);
        var table = SubtitleText.Read(file, stts.Start + 8, entries * 8);
        ct.ThrowIfCancellationRequested();
        long frames = 0, ticks = 0; uint? first = null; var constant = true;
        for (var i = 0; i < entries; i++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 8)); var delta = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 8 + 4));
            // The last frame often lasts a different time: it doesn't make the rate variable.
            if (first is null) first = delta; else if (delta != first && !(i == entries - 1 && count == 1)) constant = false;
            frames += count; ticks += (long)count * delta;
        }
        return frames > 0 ? (frames, ticks, constant) : null;
    }

    // QuickTime chapter titles: one text sample per chapter, each starting where the previous ends.
    private static void ReadMp4ChapterTrack(FileStream file, Mp4Track t, Details details, CancellationToken ct)
    {
        if (t.Timescale == 0) return;
        var (offsets, sizes) = Mp4SampleTable(file, t.Table);
        var deltas = new List<(uint Count, uint Delta)>();
        if (Find(t.Table, "stts") is { } stts && stts.End - stts.Start >= 8)
        {
            var entries = (int)Math.Min(Math.Min(BinaryPrimitives.ReadUInt32BigEndian(SubtitleText.Read(file, stts.Start + 4, 4)), MostChapters), (stts.End - stts.Start - 8) / 8);
            var table = SubtitleText.Read(file, stts.Start + 8, entries * 8);
            for (var i = 0; i < entries; i++) deltas.Add((BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 8)), BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 8 + 4))));
        }
        var section = details.Group("Chapters");
        long time = 0; var run = 0; var used = 0u;
        for (var i = 0; i < offsets.Length && i < MostChapters; i++)
        {
            ct.ThrowIfCancellationRequested();
            string? title = null;
            if (sizes[i] is >= 2 and <= 4096 && offsets[i] + sizes[i] <= file.Length)
            {
                var sample = SubtitleText.Read(file, offsets[i], sizes[i]);
                var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(sample), sample.Length - 2);
                if (length > 0)
                    title = length >= 2 && sample[2] == 0xfe && sample[3] == 0xff ? Encoding.BigEndianUnicode.GetString(sample, 4, length - 2) : Decode(sample[2..(2 + length)]);
            }
            section.Add(Duration(TimeSpan.FromSeconds(time / (double)t.Timescale), true), string.IsNullOrWhiteSpace(title) ? "Untitled" : title);
            if (run < deltas.Count) { time += deltas[run].Delta; if (++used >= deltas[run].Count) { run++; used = 0; } }
        }
    }

    // udta: iTunes-style meta → ilst, Nero chapters (chpl), and QuickTime's own ©-named text boxes.
    private static void ReadMp4UserData(FileStream file, (string Type, long Start, long End) udta, Details details, CancellationToken ct)
    {
        foreach (var box in Mp4Children(file, udta.Start, udta.End, ct))
        {
            var raw = Encoding.Latin1.GetString(SubtitleText.Read(file, box.Start - 4, 4));
            if (box.Type == "meta") ReadMp4Meta(file, box, details.Container, ct);
            else if (box.Type == "chpl") ReadNeroChapters(file, box, details, ct);
            else if (raw[0] == '©' && box.End - box.Start >= 4)
            {
                var data = SubtitleText.Read(file, box.Start, (int)Math.Min(box.End - box.Start, 64 << 10));
                var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data), data.Length - 4);
                if (length > 0) details.Container.Add(ItunesName(raw), Decode(data[4..(4 + length)]));
            }
        }
    }
    private static void ReadNeroChapters(FileStream file, (string Type, long Start, long End) chpl, Details details, CancellationToken ct)
    {
        var data = SubtitleText.Read(file, chpl.Start, (int)Math.Min(chpl.End - chpl.Start, 1 << 20));
        if (data.Length < 5) return;
        var position = data[0] == 1 ? 8 : 4;
        if (position >= data.Length) return;
        var count = data[position++];
        var section = details.Group("Chapters");
        for (var i = 0; i < count && position + 9 <= data.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var start = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(position)); var length = data[position + 8]; position += 9;
            if (position + length > data.Length) break;
            section.Add(Duration(TimeSpan.FromTicks(Math.Max(0, start)), true), Decode(data[position..(position + length)]) is { Length: > 0 } title ? title : "Untitled");
            position += length;
        }
    }
    private static void ReadMp4Meta(FileStream file, (string Type, long Start, long End) meta, Section target, CancellationToken ct)
    {
        // ISO's meta is a full box, four bytes of version and flags first; QuickTime's starts with its children.
        var start = meta.Start;
        if (meta.End - start >= 8 && Encoding.ASCII.GetString(SubtitleText.Read(file, start + 4, 4)) != "hdlr") start += 4;
        var children = Mp4Children(file, start, meta.End, ct);
        var keys = new List<string>();
        if (SubtitleText.Find(children, "keys") is { } keyBox && keyBox.End - keyBox.Start >= 8)
        {
            var data = SubtitleText.Read(file, keyBox.Start, (int)Math.Min(keyBox.End - keyBox.Start, 1 << 20));
            for (var position = 8; position + 8 <= data.Length;)
            {
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
                if (size < 8 || position + size > data.Length) break;
                keys.Add(Decode(data[(position + 8)..(position + size)]));
                position += size;
            }
        }
        if (SubtitleText.Find(children, "ilst") is not { } ilst) return;
        foreach (var item in Mp4Children(file, ilst.Start, ilst.End, ct))
        {
            var raw = SubtitleText.Read(file, item.Start - 4, 4);
            var index = BinaryPrimitives.ReadUInt32BigEndian(raw);
            var name = keys.Count > 0 && index >= 1 && index <= keys.Count ? keys[(int)index - 1] : Encoding.Latin1.GetString(raw);
            string? freeform = null;
            foreach (var part in Mp4Children(file, item.Start, item.End, ct))
            {
                if (part.Type == "name" && part.End - part.Start > 4) freeform = Decode(SubtitleText.Read(file, part.Start + 4, (int)Math.Min(part.End - part.Start - 4, 1024)));
                if (part.Type != "data" || part.End - part.Start < 8) continue;
                var head = SubtitleText.Read(file, part.Start, 8);
                var type = BinaryPrimitives.ReadUInt32BigEndian(head) & 0xffffff;
                var length = part.End - part.Start - 8;
                var label = name == "----" ? freeform is null ? "Custom tag" : TagName(freeform) : keys.Count > 0 ? KeyName(name) : ItunesName(name);
                if (type is 13 or 14 or 27 || name == "covr") { target.Add(label, (type == 14 ? "PNG image" : type == 27 ? "BMP image" : "JPEG image") + ", " + Size(length)); continue; }
                var value = SubtitleText.Read(file, part.Start + 8, (int)Math.Min(length, 64 << 10));
                target.Add(label, ItunesValue(name, type, value));
            }
        }
    }
    private static string? ItunesValue(string name, uint type, byte[] value)
    {
        if (name is "trkn" or "disk" && value.Length >= 6)
        {
            var number = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2)); var total = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(4));
            return number + (total > 0 ? " of " + total : "");
        }
        if (type is 1 or 4) return Decode(value);
        if (type is 2 or 5) return Encoding.BigEndianUnicode.GetString(value).TrimEnd('\0');
        if (type is 21 or 22 or 0 && value.Length is 1 or 2 or 3 or 4 or 8)
        {
            long number = value.Length switch
            {
                1 => type == 21 ? (sbyte)value[0] : value[0], 2 => BinaryPrimitives.ReadInt16BigEndian(value), 3 => value[0] << 16 | value[1] << 8 | value[2],
                4 => type == 21 ? BinaryPrimitives.ReadInt32BigEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value), _ => BinaryPrimitives.ReadInt64BigEndian(value)
            };
            return name switch
            {
                "stik" => number switch { 0 => "Movie", 1 => "Music", 2 => "Audiobook", 6 => "Music video", 9 => "Movie", 10 => "TV show", 11 => "Booklet", 14 => "Ringtone", 21 => "Podcast", _ => number.ToString(CultureInfo.InvariantCulture) },
                "hdvd" => number switch { 0 => "No", 1 => "720p", 2 => "1080p", 3 => "4K", _ => number.ToString(CultureInfo.InvariantCulture) },
                "rtng" => number switch { 0 => "None", 1 or 4 => "Explicit", 2 => "Clean", _ => number.ToString(CultureInfo.InvariantCulture) },
                "pgap" or "cpil" or "pcst" => number != 0 ? "Yes" : "No",
                _ => number.ToString(CultureInfo.InvariantCulture)
            };
        }
        if (type == 23 && value.Length == 4) return BinaryPrimitives.ReadSingleBigEndian(value).ToString(CultureInfo.InvariantCulture);
        if (type == 24 && value.Length == 8) return BinaryPrimitives.ReadDoubleBigEndian(value).ToString(CultureInfo.InvariantCulture);
        return Decode(value) is { Length: > 0 } text && text.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)) ? text : $"Binary data ({Size(value.Length)})";
    }
    private static string ItunesName(string name) => name switch
    {
        "©nam" => "Title", "©ART" => "Artist", "aART" => "Album artist", "©alb" => "Album", "©day" => "Date", "©gen" or "gnre" => "Genre",
        "©cmt" => "Comment", "desc" => "Description", "ldes" => "Long description", "sdes" => "Store description", "©too" => "Writing application",
        "©enc" => "Encoded by", "©wrt" => "Composer", "cprt" or "©cpy" => "Copyright", "tvsh" => "Show", "tvsn" => "Season", "tves" => "Episode",
        "tven" => "Episode ID", "tvnn" => "Network", "stik" => "Media kind", "hdvd" => "HD video", "trkn" => "Track", "disk" => "Disc",
        "©lyr" => "Lyrics", "©grp" => "Grouping", "purd" => "Purchase date", "rtng" => "Content rating", "sonm" => "Sort title", "sosn" => "Sort show",
        "soal" => "Sort album", "soar" => "Sort artist", "©dir" => "Director", "©prd" => "Producer", "©st3" => "Subtitle", "catg" => "Category",
        "keyw" => "Keywords", "©xyz" => "Location", "©mak" => "Make", "©mod" => "Model", "©swr" => "Software", "©inf" => "Information",
        "©req" => "Requirements", "©fmt" => "Format", "©src" => "Source", "©prf" => "Performers", "©des" => "Description", "©dis" => "Disclaimer",
        "©hst" => "Host computer", "©wrn" => "Warning", "©ed1" => "Edit date", "©chp" => "Chapter", "©com" => "Composer", "©dir " => "Director",
        "©aut" => "Author", "©PRD" => "Product", "©snm" => "Sort title", "©pub" => "Publisher", "©key" => "Keywords", "©mal" => "Make and model",
        "pcst" => "Podcast", "cpil" => "Compilation", "pgap" => "Gapless playback", "tmpo" => "Tempo", "apID" => "Account", "cnID" => "Catalog ID",
        "atID" => "Artist ID", "plID" => "Playlist ID", "geID" => "Genre ID", "sfID" => "Store country", "akID" => "Account kind", "ownr" => "Owner",
        "xid " => "Vendor ID", "©mvn" => "Movement", "shwm" => "Show work and movement", "©wrk" => "Work", "©cmn" => "Comment",
        _ => name.Trim().TrimStart('©')
    };
    private static string KeyName(string key)
    {
        var last = key[(key.LastIndexOf('.') + 1)..];
        var words = new StringBuilder();
        foreach (var c in last) { if (char.IsUpper(c) && words.Length > 0) words.Append(' '); words.Append(words.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); }
        return words.Length == 0 ? key : words.ToString();
    }
}
