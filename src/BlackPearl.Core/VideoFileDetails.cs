using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

/// <summary>One fact a video file states about itself, such as "Frame rate" and "23.976 fps".</summary>
public sealed record FileDetail(string Label, string Value);
/// <summary>The facts about the file as a whole, about one of its tracks, or its chapters or attachments.</summary>
public sealed record FileDetailGroup(string Title, IReadOnlyList<FileDetail> Details);

// Everything a video file says about itself, for its page. Only headers are read, never the media: Matroska's segment info,
// tracks, tags, chapters and attachments (VideoFileDetails.Matroska.cs), MP4's movie and track boxes and their tags
// (VideoFileDetails.Mp4.cs), and AVI's stream headers and INFO list. TagLib reads the other formats it knows.
public static partial class VideoFileDetails
{
    private const int LongestValue = 4000;

    /// <summary>
    /// The file's details: "File" first, then each video, audio and subtitle track, then other tracks, chapters and
    /// attachments. Empty when the file can't be opened.
    /// </summary>
    public static IReadOnlyList<FileDetailGroup> Read(string path, CancellationToken ct = default)
    {
        FileInfo info;
        try { info = new FileInfo(path); if (!info.Exists) return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return []; }
        var details = new Details();
        var complete = true;
        try
        {
            using var file = SubtitleText.Open(path);
            var head = new byte[12];
            var read = file.ReadAtLeast(head, head.Length, false);
            file.Position = 0;
            if (read >= 4 && BinaryPrimitives.ReadUInt32BigEndian(head) == 0x1a45dfa3) ReadMatroska(file, details, ct);
            else if (read >= 8 && Encoding.ASCII.GetString(head, 4, 4) is "ftyp" or "moov" or "mdat" or "free" or "wide" or "skip" or "pnot") ReadMp4(file, details, ct);
            else if (read >= 12 && Encoding.ASCII.GetString(head, 0, 4) == "RIFF" && Encoding.ASCII.GetString(head, 8, 4) == "AVI ") ReadAvi(file, details, ct);
            else { file.Dispose(); ReadOther(path, details); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or ArgumentException or
            IndexOutOfRangeException or FormatException or TagLib.CorruptFileException or TagLib.UnsupportedFormatException) { complete = false; }

        var general = new Section("File");
        general.Add("Format", details.Format ?? FormatByExtension(path));
        general.Add("Size", Size(info.Length));
        if (details.Duration is { Ticks: > 0 } duration)
        {
            general.Add("Duration", Duration(duration, true));
            general.Add("Overall bitrate", Bitrate(info.Length * 8 / duration.TotalSeconds));
        }
        general.Rows.AddRange(details.Container.Rows);
        general.Add("Modified", Date(info.LastWriteTime));
        general.Add("Created", Date(info.CreationTime));
        if (!complete) general.Add("Note", "Part of this file couldn't be read, so some details may be missing.");

        var groups = new List<FileDetailGroup> { general.ToGroup() };
        foreach (var kind in new[] { Kind.Video, Kind.Audio, Kind.Subtitles, Kind.Other })
        {
            var tracks = details.Tracks.Where(track => track.Kind == kind && track.Rows.Count > 0).ToList();
            for (var i = 0; i < tracks.Count; i++)
            {
                var name = kind switch { Kind.Video => "Video", Kind.Audio => "Audio", Kind.Subtitles => "Subtitles", _ => tracks[i].Title };
                var title = tracks.Count > 1 && kind != Kind.Other ? $"{name} {i + 1}" : name;
                if (tracks[i].Language is { } language) title += " · " + language;
                groups.Add(new(title, tracks[i].Rows.Select((row, index) => (row, index)).OrderBy(entry => Order(entry.row.Label, kind)).ThenBy(entry => entry.index)
                    .Select(entry => entry.row).ToList()));
            }
        }
        groups.AddRange(details.Extra.Where(section => section.Rows.Count > 0).Select(section => section.ToGroup()));
        return groups;
    }

    /// <summary>The details as plain text, a heading and its lines per group, for copying.</summary>
    public static string Text(IEnumerable<FileDetailGroup> groups) =>
        string.Join(Environment.NewLine + Environment.NewLine, groups.Select(group =>
            group.Title + Environment.NewLine + string.Join(Environment.NewLine, group.Details.Select(detail => detail.Label + ": " + detail.Value))));

    private enum Kind { Video, Audio, Subtitles, Other }

    // A track's details in reading order: what it is, how it looks or sounds, its size, then how players treat it.
    private static readonly string[] TrackOrder = ["Codec", "Profile", "Codec ID", "Resolution", "Display size", "Cropped to", "Aspect ratio", "Pixel aspect ratio",
        "Frame rate", "Frame rate mode", "Scan type", "Bit depth", "Chroma subsampling", "HDR", "Color primaries", "Transfer characteristics", "Matrix coefficients",
        "Color range", "Maximum content light level", "Maximum frame-average light level", "Mastering display metadata", "Color profile", "Stereoscopic 3D", "Projection",
        "Channels", "Sample rate", "Core sample rate", "Dolby Atmos", "Bitrate", "Maximum bitrate", "Duration", "Frames", "Events", "Stream size",
        "Language", "Name", "Default", "Forced", "Enabled"];
    private static double Order(string label, Kind kind) =>
        kind == Kind.Audio && label == "Bit depth" ? Array.IndexOf(TrackOrder, "Sample rate") + 0.5 : Array.IndexOf(TrackOrder, label) is var index and >= 0 ? index : TrackOrder.Length;

    private sealed class Section(string title, Kind kind = Kind.Other)
    {
        public string Title = title;
        public readonly Kind Kind = kind;
        public string? Language;
        public readonly List<FileDetail> Rows = [];
        public void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            Rows.Add(new(label, value.Length > LongestValue ? value[..LongestValue] + "…" : value));
        }
        public void Add(string label, bool value) => Add(label, value ? "Yes" : "No");
        public FileDetailGroup ToGroup() => new(Title, Rows);
    }

    private sealed class Details
    {
        public string? Format;
        public TimeSpan? Duration;
        public readonly Section Container = new("File");
        public readonly List<Section> Tracks = [];
        public readonly List<Section> Extra = [];
        public Section Track(Kind kind, string title = "Track") { var track = new Section(title, kind); Tracks.Add(track); return track; }
        public Section Group(string title) { var section = new Section(title); Extra.Add(section); return section; }
    }

    // AVI: RIFF 'AVI ' → LIST 'hdrl' → avih (main header), LIST 'strl' per stream → strh, strf, strn; LIST 'odml' → dmlh
    // (OpenDML's frame count); LIST 'INFO' → text tags. The movie data ('movi') is skipped.
    private static void ReadAvi(FileStream file, Details details, CancellationToken ct)
    {
        details.Format = "AVI";
        var riffEnd = Math.Min(file.Length, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(SubtitleText.Read(file, 4, 4)));
        uint microsecondsPerFrame = 0, totalFrames = 0, odmlFrames = 0;
        double? videoRate = null; long videoLength = 0;
        foreach (var (id, start, end) in RiffChunks(file, 12, riffEnd, ct))
        {
            if (id != "LIST" || end - start < 4) continue;
            var list = Encoding.ASCII.GetString(SubtitleText.Read(file, start, 4));
            if (list == "hdrl")
                foreach (var (part, partStart, partEnd) in RiffChunks(file, start + 4, end, ct))
                {
                    if (part == "avih" && partEnd - partStart >= 40)
                    {
                        var header = SubtitleText.Read(file, partStart, 40);
                        microsecondsPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(header);
                        totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                    }
                    else if (part == "LIST" && partEnd - partStart >= 4)
                    {
                        var inner = Encoding.ASCII.GetString(SubtitleText.Read(file, partStart, 4));
                        if (inner == "strl") ReadAviStream(file, details, partStart + 4, partEnd, ref videoRate, ref videoLength, ct);
                        else if (inner == "odml")
                            foreach (var (dml, dmlStart, dmlEnd) in RiffChunks(file, partStart + 4, partEnd, ct))
                                if (dml == "dmlh" && dmlEnd - dmlStart >= 4) odmlFrames = BinaryPrimitives.ReadUInt32LittleEndian(SubtitleText.Read(file, dmlStart, 4));
                    }
                }
            else if (list == "INFO")
                foreach (var (tag, tagStart, tagEnd) in RiffChunks(file, start + 4, end, ct))
                    details.Container.Add(AviInfoName(tag), Decode(SubtitleText.Read(file, tagStart, (int)Math.Min(tagEnd - tagStart, 64 << 10))));
        }
        var frames = Math.Max(totalFrames, odmlFrames);
        if (videoRate is > 0 && videoLength > 0) details.Duration = TimeSpan.FromSeconds(Math.Max(videoLength, frames) / videoRate.Value);
        else if (microsecondsPerFrame > 0 && frames > 0) details.Duration = TimeSpan.FromTicks((long)frames * microsecondsPerFrame * 10);
        if (frames > 0 && details.Tracks.FirstOrDefault(track => track.Kind == Kind.Video) is { } video) video.Add("Frames", frames.ToString("N0", CultureInfo.CurrentCulture));
    }
    private static void ReadAviStream(FileStream file, Details details, long start, long end, ref double? videoRate, ref long videoLength, CancellationToken ct)
    {
        byte[]? header = null, format = null; string? name = null;
        foreach (var (id, chunkStart, chunkEnd) in RiffChunks(file, start, end, ct))
        {
            var length = (int)Math.Min(chunkEnd - chunkStart, 64 << 10);
            if (id == "strh") header = SubtitleText.Read(file, chunkStart, length);
            else if (id == "strf") format = SubtitleText.Read(file, chunkStart, length);
            else if (id == "strn") name = Decode(SubtitleText.Read(file, chunkStart, length));
        }
        if (header is null || header.Length < 36) return;
        var type = Encoding.ASCII.GetString(header, 0, 4);
        var handler = Encoding.ASCII.GetString(header, 4, 4).TrimEnd('\0', ' ');
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)); var rate = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        var streamLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        var kind = type switch { "vids" => Kind.Video, "auds" => Kind.Audio, "txts" => Kind.Subtitles, _ => Kind.Other };
        var track = details.Track(kind, type == "mids" ? "MIDI" : "Stream (" + type.Trim() + ")");
        if (kind == Kind.Video && format is { Length: >= 20 })
        {
            var fourcc = Encoding.ASCII.GetString(format, 16, 4).TrimEnd('\0', ' ');
            if (BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(16)) == 0) fourcc = "RGB";
            track.Add("Codec", FourccCodec(fourcc));
            track.Add("Codec ID", fourcc + (handler.Length > 0 && !handler.Equals(fourcc, StringComparison.OrdinalIgnoreCase) ? " (handler " + handler + ")" : ""));
            var width = BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(4)); var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(format.AsSpan(8)));
            AddPicture(track, width, height, null, null);
            if (scale > 0 && rate > 0) { videoRate = rate / (double)scale; videoLength = streamLength; track.Add("Frame rate", FrameRate(videoRate.Value)); }
            track.Add("Bits per pixel", BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) is var bits and > 0 ? bits.ToString(CultureInfo.CurrentCulture) : null);
        }
        else if (kind == Kind.Audio && format is { Length: >= 16 })
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
            if (tag == 0xfffe && format.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(24)); // WAVE_FORMAT_EXTENSIBLE's subformat
            track.Add("Codec", WaveCodec(tag));
            track.Add("Codec ID", "0x" + tag.ToString("X4", CultureInfo.InvariantCulture));
            AddAudio(track, BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2)), BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)),
                BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) is var bits and > 0 and not 0xffff ? bits : null);
            track.Add("Bitrate", Bitrate(BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8)) * 8.0));
        }
        else if (handler.Length > 0) track.Add("Codec ID", handler);
        if (header.Length >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)) is var language and > 0) track.Add("Language code", language.ToString(CultureInfo.InvariantCulture));
        track.Add("Name", name);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) is var flags && (flags & 1) != 0) track.Add("Disabled", true);
    }
    // A RIFF list's chunks: a four-letter id, a little-endian size, then the data, padded to an even length.
    private static List<(string Id, long Start, long End)> RiffChunks(Stream file, long from, long end, CancellationToken ct)
    {
        var chunks = new List<(string, long, long)>();
        var header = new byte[8];
        for (var position = from; position + 8 <= end && chunks.Count < 4096;)
        {
            ct.ThrowIfCancellationRequested();
            file.Position = position; file.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            var start = position + 8; var stop = Math.Min(end, start + size);
            chunks.Add((Encoding.ASCII.GetString(header, 0, 4), start, stop));
            position = start + size + (size & 1);
        }
        return chunks;
    }
    private static string AviInfoName(string id) => id switch
    {
        "INAM" => "Title", "ISFT" => "Writing application", "ICMT" => "Comment", "ICRD" => "Date", "IDIT" => "Recorded", "IART" => "Artist",
        "ICOP" => "Copyright", "IGNR" => "Genre", "ISBJ" => "Subject", "IENG" => "Engineer", "ISRC" => "Source", "ISRF" => "Source form",
        "ITCH" => "Encoded by", "ILNG" => "Language", "IPRD" => "Product", "IKEY" => "Keywords", "ISMP" => "SMPTE time code", "IDIM" => "Dimensions",
        "IDPI" => "Dots per inch", "IPRT" => "Part", "IFRM" => "Parts", "ISTR" => "Starring", "IDST" => "Distributed by", "IWRI" => "Written by",
        "IPRO" => "Produced by", "ICNM" => "Cinematographer", "IEDT" => "Edited by", "IMUS" => "Music by", "IRTD" => "Rating", "ICMS" => "Commissioned by",
        "IARL" => "Archival location", "ICRP" => "Cropped", "ILGT" => "Lightness", "IMED" => "Medium", "IPLT" => "Palette", "ISHP" => "Sharpness",
        "ISGN" => "Secondary genre", "ITRK" => "Track", "ISTD" => "Production studio", "ICAS" => "Default audio stream",
        _ => id.Trim()
    };

    // Formats TagLib knows that aren't read here: MPEG program streams, Windows Media, Ogg and others.
    private static void ReadOther(string path, Details details)
    {
        TagLib.File file;
        try { file = TagLib.File.Create(path, TagLib.ReadStyle.Average); }
        catch (TagLib.UnsupportedFormatException) { return; } // nothing reads it: the file's size and format are all there is
        using var _ = file;
        var properties = file.Properties;
        if (properties is not null)
        {
            if (properties.Duration > TimeSpan.Zero) details.Duration = properties.Duration;
            details.Format = file.MimeType?.StartsWith("taglib/", StringComparison.Ordinal) == true ? null : file.MimeType;
            foreach (var codec in properties.Codecs ?? [])
            {
                if (codec is null) continue;
                var kind = (codec.MediaTypes & TagLib.MediaTypes.Video) != 0 ? Kind.Video : (codec.MediaTypes & TagLib.MediaTypes.Audio) != 0 ? Kind.Audio : Kind.Other;
                var track = details.Track(kind);
                track.Add("Codec", codec.Description);
                if (codec is TagLib.IVideoCodec video) AddPicture(track, video.VideoWidth, video.VideoHeight, null, null);
                if (codec is TagLib.IAudioCodec audio)
                {
                    AddAudio(track, audio.AudioChannels, audio.AudioSampleRate, codec is TagLib.ILosslessAudioCodec lossless && lossless.BitsPerSample > 0 ? lossless.BitsPerSample : null);
                    if (audio.AudioBitrate > 0) track.Add("Bitrate", Bitrate(audio.AudioBitrate * 1000.0));
                }
            }
        }
        var tag = file.Tag;
        if (tag is null) return;
        details.Container.Add("Title", tag.Title);
        details.Container.Add("Subtitle", tag.Subtitle);
        details.Container.Add("Description", tag.Description);
        details.Container.Add("Comment", tag.Comment);
        details.Container.Add("Year", tag.Year > 0 ? tag.Year.ToString(CultureInfo.InvariantCulture) : null);
        details.Container.Add("Genre", tag.Genres?.Length > 0 ? string.Join(", ", tag.Genres) : null);
        details.Container.Add("Artist", tag.Performers?.Length > 0 ? string.Join(", ", tag.Performers) : null);
        details.Container.Add("Album", tag.Album);
        details.Container.Add("Copyright", tag.Copyright);
    }
    private static string FormatByExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".m2ts" or ".mts" => "MPEG transport stream", ".mpg" or ".mpeg" or ".vob" => "MPEG program stream", ".flv" => "Flash Video",
        ".wmv" or ".asf" => "Windows Media", ".ogv" or ".ogg" => "Ogg", var extension => extension.TrimStart('.').ToUpperInvariant()
    };

    // Shared by every format.
    private static void AddPicture(Section track, long width, long height, long? displayWidth, long? displayHeight)
    {
        if (width <= 0 || height <= 0) return;
        track.Add("Resolution", $"{width} × {height}");
        if (displayWidth is > 0 && displayHeight is > 0 && (displayWidth != width || displayHeight != height))
            track.Add("Display size", $"{displayWidth} × {displayHeight}");
        track.Add("Aspect ratio", AspectRatio((double)(displayWidth is > 0 && displayHeight is > 0 ? displayWidth.Value : width) /
            (displayWidth is > 0 && displayHeight is > 0 ? displayHeight.Value : height)));
    }
    private static void AddAudio(Section track, long channels, double sampleRate, long? bitDepth)
    {
        if (channels > 0) track.Add("Channels", Channels(channels));
        if (sampleRate > 0) track.Add("Sample rate", SampleRate(sampleRate));
        if (bitDepth is > 0) track.Add("Bit depth", $"{bitDepth} bits");
    }
    private static void AddLanguage(Section track, string? code)
    {
        var language = TrackLanguages.Normalize(code);
        if (language is null) { track.Add("Language", string.IsNullOrWhiteSpace(code) ? "Not set" : "Undetermined (" + code.Trim() + ")"); return; }
        string name;
        try { name = CultureInfo.GetCultureInfo(language).EnglishName; }
        catch (CultureNotFoundException) { name = language; }
        track.Language = name;
        track.Add("Language", name + (string.Equals(code, name, StringComparison.OrdinalIgnoreCase) ? "" : " (" + code!.Trim() + ")"));
    }

    internal static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes"
            : value.ToString(value >= 100 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit] + " (" + bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes)";
    }
    internal static string Duration(TimeSpan time, bool milliseconds = false)
    {
        var text = time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time:mm\\:ss}" : $"{(int)time.TotalMinutes}:{time:ss}";
        return milliseconds && time.Milliseconds > 0 ? text + "." + time.Milliseconds.ToString("000", CultureInfo.InvariantCulture) : text;
    }
    // Below 1 kb/s (text subtitles) a bitrate says nothing.
    internal static string? Bitrate(double bitsPerSecond) => bitsPerSecond < 1000 || double.IsNaN(bitsPerSecond) || double.IsInfinity(bitsPerSecond) ? null
        : bitsPerSecond >= 1_000_000 ? (bitsPerSecond / 1_000_000).ToString("0.##", CultureInfo.CurrentCulture) + " Mb/s"
        : Math.Round(bitsPerSecond / 1000).ToString("0", CultureInfo.CurrentCulture) + " kb/s";
    internal static string FrameRate(double fps) => fps.ToString("0.###", CultureInfo.CurrentCulture) + " fps";
    private static string SampleRate(double hertz) => (hertz / 1000).ToString("0.###", CultureInfo.CurrentCulture) + " kHz";
    private static string Channels(long count) => count switch
    {
        1 => "1 (mono)", 2 => "2 (stereo)", 6 => "6 (5.1)", 8 => "8 (7.1)", _ => count.ToString(CultureInfo.CurrentCulture)
    };
    private static string Date(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    private static string UtcDate(DateTime utc) => Date(utc.ToLocalTime());
    internal static string AspectRatio(double ratio)
    {
        (double Value, string Name)[] known = [(4 / 3.0, "4:3"), (16 / 9.0, "16:9"), (5 / 4.0, "5:4"), (3 / 2.0, "3:2"), (1.85, "1.85:1"), (2.0, "2:1"),
            (2.2, "2.20:1"), (2.35, "2.35:1"), (2.39, "2.39:1"), (2.4, "2.40:1"), (1.0, "1:1"), (9 / 16.0, "9:16")];
        foreach (var (value, name) in known) if (Math.Abs(ratio - value) < 0.006) return name;
        return ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1";
    }
    // Tag text: UTF-8 when it is valid, otherwise Latin-1, without trailing NULs.
    private static string Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }
    // "_STATISTICS_WRITING_APP" → "Statistics writing app"; "creation_time" → "Creation time"; "BPS-eng" → "BPS".
    private static string TagName(string name)
    {
        name = LanguageSuffix().Replace(name.Trim(), "");
        var words = name.Trim('_').Replace('_', ' ');
        if (words.Length == 0) return name;
        if (words.All(c => !char.IsLetter(c) || char.IsUpper(c)) || words.All(c => !char.IsLetter(c) || char.IsLower(c))) words = words.ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
    [GeneratedRegex(@"-[a-z]{3}$")] private static partial Regex LanguageSuffix();

    private static string FourccCodec(string fourcc) => fourcc.ToUpperInvariant() switch
    {
        "XVID" => "MPEG-4 Visual (Xvid)", "DIVX" or "DX50" or "DIV5" or "DIV6" => "MPEG-4 Visual (DivX)", "FMP4" or "MP4V" or "M4S2" or "3IV2" => "MPEG-4 Visual",
        "DIV3" or "DIV4" or "MP43" or "DVX3" => "Microsoft MPEG-4 v3 (DivX 3)", "MP42" => "Microsoft MPEG-4 v2", "MP4S" or "MPG4" => "Microsoft MPEG-4 v1",
        "H264" or "X264" or "AVC1" or "DAVC" or "VSSH" => "H.264 (AVC)", "HEVC" or "HVC1" or "HEV1" or "X265" or "H265" => "H.265 (HEVC)",
        "MJPG" or "AVRN" or "LJPG" => "Motion JPEG", "WMV1" => "Windows Media Video 7", "WMV2" => "Windows Media Video 8", "WMV3" => "Windows Media Video 9",
        "WVC1" => "VC-1", "VP80" => "VP8", "VP90" => "VP9", "AV01" => "AV1", "MPG1" => "MPEG-1 Video", "MPG2" or "MPEG" or "MMES" => "MPEG-2 Video",
        "DVSD" or "DV25" or "DV50" or "CDVC" => "DV", "CVID" => "Cinepak", "IV50" => "Indeo 5", "IV41" => "Indeo 4", "IV32" => "Indeo 3",
        "MSVC" or "CRAM" => "Microsoft Video 1", "HFYU" => "HuffYUV", "FFV1" => "FFV1", "UYVY" or "YUY2" or "YV12" or "I420" or "RGB" => "Uncompressed",
        "FLV1" => "Sorenson Spark", "H263" or "S263" => "H.263", "TSCC" => "TechSmith Screen Capture", "MSZH" or "ZLIB" => "LCL",
        _ => fourcc
    };
    private static string WaveCodec(ushort tag) => tag switch
    {
        0x0001 => "PCM", 0x0002 => "Microsoft ADPCM", 0x0003 => "PCM (floating point)", 0x0006 => "A-law", 0x0007 => "µ-law", 0x0011 => "IMA ADPCM",
        0x0031 => "GSM 6.10", 0x0050 => "MPEG Audio Layer 1/2", 0x0055 => "MP3", 0x00ff or 0x1610 or 0x706d or 0x4143 => "AAC",
        0x0160 => "Windows Media Audio 1", 0x0161 => "Windows Media Audio", 0x0162 => "Windows Media Audio Pro", 0x0163 => "Windows Media Audio Lossless",
        0x2000 => "AC-3 (Dolby Digital)", 0x2001 => "DTS", 0x674f or 0x6750 or 0x6751 or 0x676f or 0x6770 or 0x6771 => "Vorbis", 0xf1ac => "FLAC",
        0x0092 => "AC-3 (Dolby Digital, SPDIF)", 0x0401 => "Intel Music Coder", 0x0130 => "ACELP.net", 0x704f => "Opus",
        _ => "Unknown audio"
    };
}
