using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using static BlackPearl.Core.SubtitleText;

namespace BlackPearl.Core;

// Matroska and WebM: the EBML header, then the Segment's Info, Tracks, Tags, Chapters and Attachments. They usually come
// before the first Cluster; those written after it (ffmpeg puts Tags at the end) are found through the SeekHead.
public static partial class VideoFileDetails
{
    private const ulong SegmentId = 0x18538067, ClusterId = 0x1f43b675, SeekHeadId = 0x114d9b74, InfoId = 0x1549a966, TracksId = 0x1654ae6b,
        TagsId = 0x1254c367, ChaptersId = 0x1043a770, AttachmentsId = 0x1941a469;
    private const int MostChapters = 500, MostAttachments = 200;

    private sealed class MatroskaTrack
    {
        public ulong Number, Uid, Type, DefaultDuration;
        public string Codec = "";
        public string? CodecName, Name, Language, Bcp47;
        public bool Enabled = true, Default = true, Forced, HearingImpaired, VisualImpaired, TextDescriptions, Original, Commentary, Lacing = true;
        public byte[] Private = [];
        public ulong? Width, Height, DisplayWidth, DisplayHeight, DisplayUnit, Interlaced, StereoMode, Channels, BitDepth;
        public ulong? ColorBits, Matrix, Transfer, Primaries, Range, MaxCll, MaxFall, Projection;
        public bool Mastering, DolbyVision, Encrypted, Compressed;
        public ulong CropTop, CropBottom, CropLeft, CropRight;
        public double? SampleRate, OutputSampleRate;
        public readonly List<(string Name, string Value)> Tags = [];
    }

    private static void ReadMatroska(FileStream file, Details details, CancellationToken ct)
    {
        details.Format = "Matroska";
        if (Vint(file, true) != 0x1a45dfa3) return;
        var headerEnd = End(file, Vint(file, false), file.Length);
        foreach (var (id, length, _) in Children(file, headerEnd, ct))
            if (id == 0x4282 && Ascii(file, length) == "webm") details.Format = "WebM";
            else if (id == 0x4287) details.Container.Add("Matroska version", UInt(file, length).ToString(CultureInfo.InvariantCulture));
        file.Position = headerEnd;
        if (Vint(file, true) != SegmentId) return;
        var segmentSize = Vint(file, false);
        var segment = file.Position;
        var end = segmentSize == ulong.MaxValue ? file.Length : End(file, segmentSize, file.Length);

        // The top-level elements before the first Cluster, and every element the SeekHeads point to.
        var found = new Dictionary<ulong, long>();
        var seekHeads = new Queue<long>(); var visited = new HashSet<long>();
        file.Position = segment;
        for (var count = 0; count < 512 && file.Position < end; count++)
        {
            ct.ThrowIfCancellationRequested();
            var start = file.Position; var id = Vint(file, true); var length = Vint(file, false);
            if (id == ClusterId || length == ulong.MaxValue) break;
            var next = End(file, length, end);
            found.TryAdd(id, start);
            if (id == SeekHeadId) { visited.Add(start); ReadSeekHead(file, next, segment, found, seekHeads, ct); }
            file.Position = next;
        }
        while (seekHeads.TryDequeue(out var position))
            if (visited.Add(position) && Open(file, position, SeekHeadId, end) is { } next) ReadSeekHead(file, next, segment, found, seekHeads, ct);

        if (found.TryGetValue(InfoId, out var info) && Open(file, info, InfoId, end) is { } infoEnd) ReadInfo(file, details, infoEnd, ct);
        var tracks = found.TryGetValue(TracksId, out var trackList) && Open(file, trackList, TracksId, end) is { } tracksEnd ? ReadTrackList(file, tracksEnd, ct) : [];
        if (found.TryGetValue(TagsId, out var tags) && Open(file, tags, TagsId, end) is { } tagsEnd) ReadTags(file, details, tracks, tagsEnd, ct);
        // mkvmerge stamps every track's statistics with the same program and time: said once for the file instead.
        var statistics = new List<string>();
        foreach (var (key, label) in new[] { ("_STATISTICS_WRITING_APP", "Statistics written by"), ("_STATISTICS_WRITING_DATE_UTC", "Statistics written") })
        {
            var values = tracks.Select(track => track.Tags.FirstOrDefault(tag => LanguageSuffix().Replace(tag.Name, "").ToUpperInvariant() == key).Value).Distinct().ToArray();
            if (tracks.Count == 0 || values is not [{ } value]) continue;
            details.Container.Add(label, StatisticsValue(key, value));
            statistics.Add(key);
        }
        foreach (var track in tracks) AddMatroskaTrack(details, track, [.. statistics]);
        if (found.TryGetValue(ChaptersId, out var chapters) && Open(file, chapters, ChaptersId, end) is { } chaptersEnd) ReadChapters(file, details, chaptersEnd, ct);
        if (found.TryGetValue(AttachmentsId, out var attachments) && Open(file, attachments, AttachmentsId, end) is { } attachmentsEnd) ReadAttachments(file, details, attachmentsEnd, ct);
    }

    // The children of a master element: id, content length, and where the next one starts. The stream is at the content.
    private static IEnumerable<(ulong Id, ulong Length, long Next)> Children(Stream file, long end, CancellationToken ct)
    {
        for (var count = 0; count < 100_000 && file.Position < end; count++)
        {
            ct.ThrowIfCancellationRequested();
            var id = Vint(file, true); var length = Vint(file, false);
            if (length == ulong.MaxValue) yield break;
            var next = End(file, length, end);
            yield return (id, length, next);
            file.Position = next;
        }
    }
    private static long End(Stream file, ulong length, long limit) => length > (ulong)(limit - file.Position) ? limit : file.Position + (long)length;
    // Positions the stream at the content of the element at position when it has the expected id; returns its end.
    private static long? Open(Stream file, long position, ulong id, long limit)
    {
        if (position < 0 || position >= limit) return null;
        file.Position = position;
        if (Vint(file, true) != id) return null;
        var length = Vint(file, false);
        return length == ulong.MaxValue ? limit : End(file, length, limit);
    }
    private static void ReadSeekHead(Stream file, long end, long segment, Dictionary<ulong, long> found, Queue<long> seekHeads, CancellationToken ct)
    {
        foreach (var (id, _, seekEnd) in Children(file, end, ct))
        {
            if (id != 0x4dbb) continue;
            ulong target = 0, position = ulong.MaxValue;
            foreach (var (field, length, _) in Children(file, seekEnd, ct))
                if (field == 0x53ab) target = UInt(file, Math.Min(length, 8));
                else if (field == 0x53ac) position = UInt(file, length);
            if (target == 0 || position >= (ulong)(long.MaxValue - segment)) continue;
            if (target == SeekHeadId) seekHeads.Enqueue(segment + (long)position);
            else found.TryAdd(target, segment + (long)position);
        }
    }
    private static double Float(Stream file, ulong length)
    {
        var bytes = Bytes(file, length);
        return length switch { 4 => BinaryPrimitives.ReadSingleBigEndian(bytes), 8 => BinaryPrimitives.ReadDoubleBigEndian(bytes), _ => 0 };
    }
    private static string Text(Stream file, ulong length) => length > 1 << 20 ? "" : Decode(Bytes(file, length));

    private static void ReadInfo(Stream file, Details details, long end, CancellationToken ct)
    {
        ulong scale = 1_000_000; double? duration = null;
        foreach (var (id, length, _) in Children(file, end, ct))
            switch (id)
            {
                case 0x2ad7b1: scale = UInt(file, length) is var value and > 0 ? value : scale; break;
                case 0x4489: duration = Float(file, length); break;
                case 0x7ba9: details.Container.Add("Title", Text(file, length)); break;
                case 0x4461:
                    var nanoseconds = (long)UInt(file, length);
                    details.Container.Add("Encoded", UtcDate(new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(nanoseconds / 100)));
                    break;
                case 0x4d80: details.Container.Add("Muxing application", Text(file, length)); break;
                case 0x5741: details.Container.Add("Writing application", Text(file, length)); break;
                case 0x7384: details.Container.Add("Segment file name", Text(file, length)); break;
                case 0x73a4: details.Container.Add("Segment UID", Convert.ToHexString(Bytes(file, Math.Min(length, 16)))); break;
                case 0x3cb923: details.Container.Add("Previous segment", Convert.ToHexString(Bytes(file, Math.Min(length, 16)))); break;
                case 0x3eb923: details.Container.Add("Next segment", Convert.ToHexString(Bytes(file, Math.Min(length, 16)))); break;
            }
        if (duration is > 0 and < 1e20) details.Duration = TimeSpan.FromTicks((long)Math.Min(duration.Value * scale / 100, TimeSpan.MaxValue.Ticks / 2));
    }

    private static List<MatroskaTrack> ReadTrackList(Stream file, long end, CancellationToken ct)
    {
        var tracks = new List<MatroskaTrack>();
        foreach (var (entry, _, entryEnd) in Children(file, end, ct))
        {
            if (entry != 0xae) continue;
            var track = new MatroskaTrack();
            foreach (var (id, length, next) in Children(file, entryEnd, ct))
                switch (id)
                {
                    case 0xd7: track.Number = UInt(file, length); break;
                    case 0x73c5: track.Uid = UInt(file, length); break;
                    case 0x83: track.Type = UInt(file, length); break;
                    case 0xb9: track.Enabled = UInt(file, length) != 0; break;
                    case 0x88: track.Default = UInt(file, length) != 0; break;
                    case 0x55aa: track.Forced = UInt(file, length) != 0; break;
                    case 0x55ab: track.HearingImpaired = UInt(file, length) != 0; break;
                    case 0x55ac: track.VisualImpaired = UInt(file, length) != 0; break;
                    case 0x55ad: track.TextDescriptions = UInt(file, length) != 0; break;
                    case 0x55ae: track.Original = UInt(file, length) != 0; break;
                    case 0x55af: track.Commentary = UInt(file, length) != 0; break;
                    case 0x9c: track.Lacing = UInt(file, length) != 0; break;
                    case 0x23e383: track.DefaultDuration = UInt(file, length); break;
                    case 0x536e: track.Name = Text(file, length); break;
                    case 0x22b59c: track.Language = Ascii(file, length); break;
                    case 0x22b59d: track.Bcp47 = Ascii(file, length); break;
                    case 0x86: track.Codec = Ascii(file, length); break;
                    case 0x258688: track.CodecName = Text(file, length); break;
                    case 0x63a2: if (length <= 1 << 20) track.Private = Bytes(file, length); break;
                    case 0x41e4: // BlockAdditionMapping: Dolby Vision's configuration
                        foreach (var (field, size, _) in Children(file, next, ct))
                            if (field == 0x41e7 && UInt(file, size) is 0x64766343 or 0x64767643 or 0x64767743) track.DolbyVision = true;
                        break;
                    case 0x6d80:
                        foreach (var (encoding, _, encodingEnd) in Children(file, next, ct))
                            if (encoding == 0x6240)
                                foreach (var (kind, _, _) in Children(file, encodingEnd, ct))
                                    if (kind == 0x5035) track.Encrypted = true; else if (kind == 0x5034) track.Compressed = true;
                        break;
                    case 0xe0: ReadVideo(file, track, next, ct); break;
                    case 0xe1:
                        foreach (var (field, size, _) in Children(file, next, ct))
                            if (field == 0xb5) track.SampleRate = Float(file, size);
                            else if (field == 0x78b5) track.OutputSampleRate = Float(file, size);
                            else if (field == 0x9f) track.Channels = UInt(file, size);
                            else if (field == 0x6264) track.BitDepth = UInt(file, size);
                        break;
                }
            tracks.Add(track);
        }
        return tracks;
    }
    private static void ReadVideo(Stream file, MatroskaTrack track, long end, CancellationToken ct)
    {
        foreach (var (id, length, next) in Children(file, end, ct))
            switch (id)
            {
                case 0xb0: track.Width = UInt(file, length); break;
                case 0xba: track.Height = UInt(file, length); break;
                case 0x54b0: track.DisplayWidth = UInt(file, length); break;
                case 0x54ba: track.DisplayHeight = UInt(file, length); break;
                case 0x54b2: track.DisplayUnit = UInt(file, length); break;
                case 0x54aa: track.CropBottom = UInt(file, length); break;
                case 0x54bb: track.CropTop = UInt(file, length); break;
                case 0x54cc: track.CropLeft = UInt(file, length); break;
                case 0x54dd: track.CropRight = UInt(file, length); break;
                case 0x9a: track.Interlaced = UInt(file, length); break;
                case 0x53b8: track.StereoMode = UInt(file, length); break;
                case 0x7670:
                    foreach (var (field, size, _) in Children(file, next, ct)) if (field == 0x7671) track.Projection = UInt(file, size);
                    break;
                case 0x55b0:
                    foreach (var (field, size, _) in Children(file, next, ct))
                        switch (field)
                        {
                            case 0x55b2: track.ColorBits = UInt(file, size); break;
                            case 0x55b1: track.Matrix = UInt(file, size); break;
                            case 0x55b9: track.Range = UInt(file, size); break;
                            case 0x55ba: track.Transfer = UInt(file, size); break;
                            case 0x55bb: track.Primaries = UInt(file, size); break;
                            case 0x55bc: track.MaxCll = UInt(file, size); break;
                            case 0x55bd: track.MaxFall = UInt(file, size); break;
                            case 0x55d0: track.Mastering = true; break;
                        }
                    break;
            }
    }

    // Tag → Targets (TagTrackUID, TargetTypeValue, TargetType) and SimpleTags (TagName, TagString or TagBinary, nested SimpleTags).
    // A tag for tracks goes to those tracks; one for the whole file to the file, named with its level when it isn't the file's own.
    private static void ReadTags(Stream file, Details details, List<MatroskaTrack> tracks, long end, CancellationToken ct)
    {
        foreach (var (id, _, tagEnd) in Children(file, end, ct))
        {
            if (id != 0x7373) continue;
            var uids = new List<ulong>(); ulong level = 50; string? levelName = null; var other = false;
            var values = new List<(string Name, string Value)>();
            foreach (var (field, length, next) in Children(file, tagEnd, ct))
                if (field == 0x63c0)
                    foreach (var (target, size, _) in Children(file, next, ct))
                        switch (target)
                        {
                            case 0x63c5: if (UInt(file, size) is var uid and > 0) uids.Add(uid); break;
                            case 0x68ca: level = UInt(file, size); break;
                            case 0x63ca: levelName = Ascii(file, size); break;
                            case 0x63c4 or 0x63c9 or 0x63c6: if (UInt(file, size) > 0) other = true; break;
                        }
                else if (field == 0x67c8) ReadSimpleTag(file, next, "", values, ct);
            if (other && uids.Count == 0) continue; // tags of a chapter, edition or attachment
            if (uids.Count > 0)
            {
                foreach (var track in tracks.Where(track => uids.Contains(track.Uid))) track.Tags.AddRange(values);
                continue;
            }
            var suffix = level == 50 && levelName is null ? "" : " (" + (string.IsNullOrWhiteSpace(levelName) ? LevelName(level) : TagName(levelName)).ToLowerInvariant() + ")";
            foreach (var (name, value) in values) details.Container.Add(TagName(name) + suffix, value);
        }
    }
    private static void ReadSimpleTag(Stream file, long end, string parent, List<(string Name, string Value)> values, CancellationToken ct)
    {
        string? name = null, value = null; var nested = new List<(long Start, long End)>();
        foreach (var (id, length, next) in Children(file, end, ct))
            switch (id)
            {
                case 0x45a3: name = Text(file, length); break;
                case 0x4487: value = Text(file, length); break;
                case 0x4485: value = $"Binary data ({Size((long)length)})"; break;
                case 0x67c8: nested.Add((file.Position, next)); break;
            }
        if (string.IsNullOrWhiteSpace(name)) return;
        var full = parent.Length == 0 ? name : parent + " / " + name;
        if (!string.IsNullOrWhiteSpace(value)) values.Add((full, value));
        foreach (var (start, stop) in nested)
        {
            file.Position = start;
            ReadSimpleTag(file, stop, full, values, ct);
        }
    }

    private static string LevelName(ulong level) => level switch
    {
        70 => "Collection", 60 => "Season", 50 => "Episode", 40 => "Part", 30 => "Chapter", 20 => "Scene", 10 => "Shot", _ => "Level " + level
    };

    private static void AddMatroskaTrack(Details details, MatroskaTrack t, string[] statistics)
    {
        var kind = t.Type switch { 1 => Kind.Video, 2 => Kind.Audio, 0x11 => Kind.Subtitles, _ => Kind.Other };
        var track = details.Track(kind, t.Type switch { 0x10 => "Logo", 0x12 => "Buttons", 0x20 => "Control", 0x21 => "Metadata", 3 => "Complex", _ => "Track" });
        track.Add("Codec", MatroskaCodec(t));
        AddCodecProfile(track, t.Codec, t.Private);
        track.Add("Codec ID", t.Codec + (string.IsNullOrWhiteSpace(t.CodecName) ? "" : " (" + t.CodecName.Trim() + ")"));
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in t.Tags) tags.TryAdd(LanguageSuffix().Replace(name, "").ToUpperInvariant(), value);
        string? Tag(string name) => tags.TryGetValue(name, out var value) ? value : null;
        if (kind == Kind.Video)
        {
            var width = (long)(t.Width ?? 0) - (long)(t.CropLeft + t.CropRight); var height = (long)(t.Height ?? 0) - (long)(t.CropTop + t.CropBottom);
            long? displayWidth = t.DisplayUnit is null or 0 ? (long?)t.DisplayWidth : null, displayHeight = t.DisplayUnit is null or 0 ? (long?)t.DisplayHeight : null;
            AddPicture(track, (long)(t.Width ?? 0), (long)(t.Height ?? 0), displayWidth, displayHeight);
            // DisplayUnit 3: the display size is the aspect ratio itself.
            if (t.DisplayUnit == 3 && t.DisplayWidth is > 0 && t.DisplayHeight is > 0 && track.Rows.FindIndex(row => row.Label == "Aspect ratio") is var index and >= 0)
                track.Rows[index] = new("Aspect ratio", AspectRatio((double)t.DisplayWidth.Value / t.DisplayHeight.Value));
            if (t.CropTop + t.CropBottom + t.CropLeft + t.CropRight > 0) track.Add("Cropped to", $"{width} × {height}");
            if (t.DefaultDuration > 0) track.Add("Frame rate", FrameRate(1e9 / t.DefaultDuration));
            track.Add("Scan type", t.Interlaced switch { 1 => "Interlaced", 2 => "Progressive", _ => null });
            AddOnce(track, "Bit depth", t.ColorBits is > 0 ? $"{t.ColorBits} bits" : null);
            AddColor(track, t.Primaries, t.Transfer, t.Matrix, t.Range, t.MaxCll, t.MaxFall, t.Mastering, t.DolbyVision);
            track.Add("Stereoscopic 3D", t.StereoMode is > 0 ? StereoMode(t.StereoMode.Value) : null);
            track.Add("Projection", t.Projection switch { 1 => "360° (equirectangular)", 2 => "Cube map", 3 => "Mesh", _ => null });
        }
        else if (kind == Kind.Audio)
        {
            // Matroska's defaults: one channel at 8 kHz.
            // HE-AAC's core runs at half the rate it plays at: the rate heard is the output's.
            var core = t.SampleRate ?? 8000;
            AddAudio(track, (long)(t.Channels ?? 1), t.OutputSampleRate is > 0 ? t.OutputSampleRate.Value : core, (long?)t.BitDepth);
            if (t.OutputSampleRate is > 0 && t.OutputSampleRate != core) track.Add("Core sample rate", SampleRate(core));
        }
        if (long.TryParse(Tag("BPS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps)) track.Add("Bitrate", Bitrate(bps));
        if (Tag("DURATION") is { } duration && TimeSpan.TryParse(duration.Split('.')[0], CultureInfo.InvariantCulture, out var time) && time > TimeSpan.Zero)
            track.Add("Duration", Duration(time + TimeSpan.FromMilliseconds(Milliseconds(duration)), true));
        if (long.TryParse(Tag("NUMBER_OF_FRAMES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
            track.Add(kind == Kind.Subtitles ? "Events" : "Frames", frames.ToString("N0", CultureInfo.CurrentCulture));
        if (long.TryParse(Tag("NUMBER_OF_BYTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)) track.Add("Stream size", Size(bytes));
        AddLanguage(track, t.Bcp47 ?? t.Language);
        track.Add("Name", t.Name);
        track.Add("Default", t.Default);
        track.Add("Forced", t.Forced);
        if (!t.Enabled) track.Add("Enabled", false);
        if (t.HearingImpaired) track.Add("For the hard of hearing", true);
        if (t.VisualImpaired) track.Add("For the visually impaired", true);
        if (t.TextDescriptions) track.Add("Describes the picture", true);
        if (t.Original) track.Add("Original language", true);
        if (t.Commentary) track.Add("Commentary", true);
        if (t.Encrypted) track.Add("Encrypted", true);
        if (t.Compressed) track.Add("Compressed in the file", true);
        track.Add("Track number", t.Number > 0 ? t.Number.ToString(CultureInfo.InvariantCulture) : null);
        string[] shown = ["BPS", "DURATION", "NUMBER_OF_FRAMES", "NUMBER_OF_BYTES", "_STATISTICS_TAGS", .. statistics];
        foreach (var (name, value) in t.Tags)
        {
            var key = LanguageSuffix().Replace(name, "").ToUpperInvariant();
            if (shown.Contains(key)) continue;
            track.Add(key switch { "_STATISTICS_WRITING_APP" => "Statistics written by", "_STATISTICS_WRITING_DATE_UTC" => "Statistics written", _ => TagName(name) },
                StatisticsValue(key, value));
        }
    }
    private static string StatisticsValue(string key, string value) =>
        key == "_STATISTICS_WRITING_DATE_UTC" && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? UtcDate(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : value;
    private static double Milliseconds(string duration)
    {
        var dot = duration.IndexOf('.');
        if (dot < 0) return 0;
        var digits = new string(duration[(dot + 1)..].TakeWhile(char.IsDigit).Take(3).ToArray()).PadRight(3, '0');
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
    private static string StereoMode(ulong mode) => mode switch
    {
        1 => "Side by side (left eye first)", 11 => "Side by side (right eye first)", 2 => "Top and bottom (right eye first)", 3 => "Top and bottom (left eye first)",
        4 or 5 => "Checkerboard", 6 or 7 => "Row interleaved", 8 or 9 => "Column interleaved", 10 or 12 => "Anaglyph", 13 or 14 => "Both eyes laced in one block",
        _ => "Mode " + mode
    };

    private static string MatroskaCodec(MatroskaTrack t)
    {
        var id = t.Codec;
        if (id == "V_MS/VFW/FOURCC" && t.Private.Length >= 20) return FourccCodec(Encoding.ASCII.GetString(t.Private, 16, 4).TrimEnd('\0', ' '));
        if (id == "A_MS/ACM" && t.Private.Length >= 2) return WaveCodec(BinaryPrimitives.ReadUInt16LittleEndian(t.Private));
        if (id.StartsWith("A_AAC", StringComparison.Ordinal))
        {
            var profile = id switch
            {
                _ when id.EndsWith("/LC", StringComparison.Ordinal) => "LC", _ when id.EndsWith("/SBR", StringComparison.Ordinal) => "HE-AAC",
                _ when id.EndsWith("/MAIN", StringComparison.Ordinal) => "Main", _ when id.EndsWith("/LTP", StringComparison.Ordinal) => "LTP",
                _ when id.EndsWith("/SSR", StringComparison.Ordinal) => "SSR", _ => AacProfile(t.Private)
            };
            if (profile == "LC" && t.OutputSampleRate is > 0 && t.SampleRate is > 0 && t.OutputSampleRate >= t.SampleRate * 2) profile = "HE-AAC";
            return profile is null ? "AAC" : "AAC " + profile;
        }
        return id switch
        {
            "V_MPEG4/ISO/AVC" => "H.264 (AVC)", "V_MPEGH/ISO/HEVC" => "H.265 (HEVC)", "V_MPEGI/ISO/VVC" => "H.266 (VVC)", "V_AV1" => "AV1", "V_VP9" => "VP9", "V_VP8" => "VP8",
            "V_MPEG4/ISO/ASP" or "V_MPEG4/ISO/SP" or "V_MPEG4/ISO/AP" => "MPEG-4 Visual", "V_MPEG4/MS/V3" => "Microsoft MPEG-4 v3 (DivX 3)",
            "V_MPEG1" => "MPEG-1 Video", "V_MPEG2" => "MPEG-2 Video", "V_THEORA" => "Theora", "V_PRORES" => "Apple ProRes", "V_FFV1" => "FFV1",
            "V_MJPEG" => "Motion JPEG", "V_UNCOMPRESSED" => "Uncompressed", "V_DIRAC" => "Dirac", "V_QUICKTIME" => "QuickTime video",
            _ when id.StartsWith("V_REAL/", StringComparison.Ordinal) => "RealVideo",
            "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10" => "AC-3 (Dolby Digital)", "A_EAC3" => "E-AC-3 (Dolby Digital Plus)", "A_TRUEHD" => "Dolby TrueHD",
            "A_MLP" => "MLP", "A_DTS" => "DTS", "A_DTS/EXPRESS" => "DTS Express", "A_DTS/LOSSLESS" => "DTS-HD Master Audio", "A_FLAC" => "FLAC",
            "A_OPUS" => "Opus", "A_VORBIS" => "Vorbis", "A_MPEG/L3" => "MP3", "A_MPEG/L2" => "MPEG Audio Layer II", "A_MPEG/L1" => "MPEG Audio Layer I",
            "A_PCM/INT/LIT" or "A_PCM/INT/BIG" => "PCM", "A_PCM/FLOAT/IEEE" => "PCM (floating point)", "A_ALAC" => "Apple Lossless (ALAC)",
            "A_TTA1" => "True Audio", "A_WAVPACK4" => "WavPack", "A_QUICKTIME/QDMC" or "A_QUICKTIME/QDM2" => "QDesign Music",
            _ when id.StartsWith("A_REAL/", StringComparison.Ordinal) => "RealAudio",
            "S_TEXT/UTF8" => "SubRip (SRT)", "S_TEXT/ASCII" => "Plain text", "S_TEXT/SSA" or "S_SSA" => "SubStation Alpha (SSA)",
            "S_TEXT/ASS" or "S_ASS" => "Advanced SubStation Alpha (ASS)", "S_TEXT/WEBVTT" => "WebVTT", "S_TEXT/USF" => "Universal Subtitle Format",
            "S_HDMV/PGS" => "PGS (Blu-ray pictures)", "S_HDMV/TEXTST" => "Blu-ray text subtitles", "S_VOBSUB" => "VobSub (DVD pictures)",
            "S_DVBSUB" => "DVB subtitles (pictures)", "S_KATE" => "Kate", "S_IMAGE/BMP" => "Bitmap pictures", "S_ARIBSUB" => "ARIB captions",
            "B_VOBBTN" => "DVD menu buttons",
            _ => id
        };
    }

    // EditionEntry → ChapterAtom (ChapterTimeStart, ChapterFlagHidden, ChapterDisplay → ChapString, nested atoms). One group
    // per edition when there are several.
    private static void ReadChapters(Stream file, Details details, long end, CancellationToken ct)
    {
        var editions = new List<(Section Section, bool Ordered, bool Default)>();
        var count = 0;
        foreach (var (id, _, editionEnd) in Children(file, end, ct))
        {
            if (id != 0x45b9) continue;
            var section = new Section("Chapters"); bool ordered = false, isDefault = false; string? name = null;
            foreach (var (field, length, next) in Children(file, editionEnd, ct))
                if (field == 0x45dd) ordered = UInt(file, length) != 0;
                else if (field == 0x45db) isDefault = UInt(file, length) != 0;
                else if (field == 0x4520) { foreach (var (display, size, _) in Children(file, next, ct)) if (display == 0x4521) name ??= Text(file, size); }
                else if (field == 0xb6) ReadChapter(file, section, next, 0, ref count, ct);
            if (name is not null) section.Title = "Chapters · " + name;
            editions.Add((section, ordered, isDefault));
        }
        for (var i = 0; i < editions.Count; i++)
        {
            var (section, ordered, isDefault) = editions[i];
            if (editions.Count > 1 && !section.Title.Contains('·')) section.Title = $"Chapters · edition {i + 1}" + (isDefault ? " (default)" : "");
            if (ordered) section.Rows.Insert(0, new("Ordered", "Yes: plays the chapters in this order, possibly from other files"));
            if (section.Rows.Count > 0) details.Extra.Add(section);
        }
    }
    private static void ReadChapter(Stream file, Section section, long end, int depth, ref int count, CancellationToken ct)
    {
        ulong start = 0; var hidden = false; var enabled = true; string? title = null; var linked = false;
        var nested = new List<(long Start, long End)>();
        foreach (var (id, length, next) in Children(file, end, ct))
            switch (id)
            {
                case 0x91: start = UInt(file, length); break;
                case 0x98: hidden = UInt(file, length) != 0; break;
                case 0x4598: enabled = UInt(file, length) != 0; break;
                case 0x6e67: linked = true; break;
                case 0x80: foreach (var (display, size, _) in Children(file, next, ct)) if (display == 0x85) title ??= Text(file, size); break;
                case 0xb6: nested.Add((file.Position, next)); break;
            }
        if (++count > MostChapters) return;
        var label = Duration(TimeSpan.FromTicks((long)Math.Min(start / 100, (ulong)(TimeSpan.MaxValue.Ticks / 2))), true);
        section.Add(new string(' ', depth * 3) + label, (string.IsNullOrWhiteSpace(title) ? "Untitled" : title) + (hidden ? " (hidden)" : "") +
            (enabled ? "" : " (disabled)") + (linked ? " (from another file)" : ""));
        foreach (var (childStart, childEnd) in nested)
        {
            file.Position = childStart;
            ReadChapter(file, section, childEnd, depth + 1, ref count, ct);
        }
    }

    private static void ReadAttachments(Stream file, Details details, long end, CancellationToken ct)
    {
        var section = details.Group("Attachments");
        foreach (var (id, _, fileEnd) in Children(file, end, ct))
        {
            if (id != 0x61a7) continue;
            if (section.Rows.Count >= MostAttachments) { section.Add("…", "More attachments are not listed"); break; }
            string? name = null, type = null, description = null; ulong size = 0;
            foreach (var (field, length, _) in Children(file, fileEnd, ct))
                switch (field)
                {
                    case 0x466e: name = Text(file, length); break;
                    case 0x4660: type = Ascii(file, length); break;
                    case 0x467e: description = Text(file, length); break;
                    case 0x465c: size = length; break;
                }
            section.Add(string.IsNullOrWhiteSpace(name) ? "Unnamed" : name, string.Join(" · ", new[] { AttachmentType(type), size > 0 ? Size((long)size) : null, description }
                .Where(part => !string.IsNullOrWhiteSpace(part))));
        }
    }
    private static string? AttachmentType(string? type) => type?.ToLowerInvariant() switch
    {
        null or "" => null,
        "application/x-truetype-font" or "font/ttf" or "application/x-font-ttf" or "font/sfnt" => "TrueType font",
        "application/vnd.ms-opentype" or "font/otf" or "application/x-font-otf" => "OpenType font",
        "font/collection" => "Font collection", "image/jpeg" => "JPEG image", "image/png" => "PNG image", "image/webp" => "WebP image",
        "text/plain" => "Text", _ => type
    };
}
