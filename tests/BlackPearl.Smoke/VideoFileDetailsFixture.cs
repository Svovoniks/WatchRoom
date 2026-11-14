using System.Buffers.Binary;
using System.Text;
using BlackPearl.Core;

// The details a video page lists from the file's own headers: Matroska, MP4 and AVI, and files that can't be read whole.
static class VideoFileDetailsFixture
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "blackpearl-file-details-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            static string? Value(IReadOnlyList<FileDetailGroup> groups, string group, string label) =>
                groups.FirstOrDefault(g => g.Title == group)?.Details.FirstOrDefault(d => d.Label == label)?.Value;

            var mkv = Path.Combine(directory, "episode.mkv");
            MatroskaFixture.Write(mkv, [new(new short[16000 * 4], 16000), new(new short[16000 * 4], 16000, "jpn", "Commentary")],
                [new("S_TEXT/UTF8", ["Hello there."], "eng", Forced: true), new("S_HDMV/PGS", [], Payloads: [[0x80]])]);
            var details = VideoFileDetails.Read(mkv);
            check(details.Select(g => g.Title).SequenceEqual(["File", "Audio 1", "Audio 2 · Japanese", "Subtitles 1 · English", "Subtitles 2"]),
                "file details list the file, then each track in order, named by kind, number and language");
            check(Value(details, "File", "Format") == "Matroska" && Value(details, "File", "Duration") == "0:04" &&
                Value(details, "File", "Muxing application") == "BlackPearl fixture" && Value(details, "File", "Size")?.EndsWith(" bytes)") == true,
                "Matroska details give the container, its duration and the program that wrote it");
            check(Value(details, "Audio 2 · Japanese", "Codec") == "PCM" && Value(details, "Audio 2 · Japanese", "Channels") == "1 (mono)" &&
                Value(details, "Audio 2 · Japanese", "Sample rate") == "16 kHz" && Value(details, "Audio 2 · Japanese", "Bit depth") == "16 bits" &&
                Value(details, "Audio 2 · Japanese", "Language") == "Japanese (jpn)" && Value(details, "Audio 2 · Japanese", "Name") == "Commentary",
                "Matroska audio tracks list their codec, channels, sample rate, bit depth, language and name");
            check(Value(details, "Audio 1", "Language") == "Not set", "an untagged track says the file sets no language, rather than Matroska's English default");
            check(Value(details, "Subtitles 1 · English", "Codec") == "SubRip (SRT)" && Value(details, "Subtitles 1 · English", "Forced") == "Yes" &&
                Value(details, "Subtitles 2", "Codec") == "PGS (Blu-ray pictures)" && Value(details, "Subtitles 2", "Codec ID") == "S_HDMV/PGS",
                "Matroska subtitle tracks name their format and flags");
            var text = VideoFileDetails.Text(details);
            check(text.StartsWith("File" + Environment.NewLine + "Format: Matroska") && text.Contains("Audio 2 · Japanese" + Environment.NewLine + "Codec: PCM"),
                "copied file details read as headings and labelled lines");

            var mp4 = Path.Combine(directory, "film.mp4");
            Mp4Fixture.Write(mp4, [new(new short[8000 * 20], 8000, "rus")], [new("tx3g", ["Chapter 1", "Chapter 2"], Chapters: true), new("tx3g", ["Hallo."], "deu")]);
            details = VideoFileDetails.Read(mp4);
            check(Value(details, "File", "Format") == "MPEG-4" && Value(details, "File", "Duration") == "0:20" && Value(details, "File", "Brand")?.StartsWith("isom") == true,
                "MP4 details give the brand and the movie's duration");
            check(Value(details, "Audio · Russian", "Codec") == "PCM" && Value(details, "Audio · Russian", "Sample rate") == "8 kHz" &&
                Value(details, "Audio · Russian", "Channels") == "1 (mono)" && Value(details, "Audio · Russian", "Bitrate") == "128 kb/s",
                "MP4 audio lists its codec, sample rate, channels and the bitrate its sample sizes give");
            check(Value(details, "Subtitles · German", "Codec") == "Timed Text (tx3g)" && Value(details, "Subtitles · German", "Events") == "20",
                "MP4 subtitle tracks list their format and number of cues");
            check(details.FirstOrDefault(g => g.Title == "Chapters")?.Details.Select(d => d.Label + " " + d.Value).Take(3).SequenceEqual(["0:00 Chapter 1", "0:01 Untitled", "0:02 Chapter 2"]) == true &&
                details.All(g => !g.Title.StartsWith("Subtitles") || g.Title.Contains("German")),
                "a QuickTime chapter track is listed as chapters, not as subtitles");

            // ffmpeg and HandBrake also write the chapter track's chapters as Nero chapters (udta/chpl).
            var both = Path.Combine(directory, "both.mp4");
            File.WriteAllBytes(both, WithNeroChapters(File.ReadAllBytes(mp4), ["Chapter 1", "Chapter 2"]));
            details = VideoFileDetails.Read(both);
            check(details.Count(g => g.Title.StartsWith("Chapters")) == 1 && details.Single(g => g.Title == "Chapters").Details.First().Value == "Chapter 1",
                "chapters an MP4 carries both as a chapter track and as Nero chapters are listed once");
            var nero = Path.Combine(directory, "nero.mp4");
            Mp4Fixture.Write(nero, [new(new short[8000 * 4], 8000)], []);
            File.WriteAllBytes(nero, WithNeroChapters(File.ReadAllBytes(nero), ["Opening", "Ending"]));
            check(VideoFileDetails.Read(nero).FirstOrDefault(g => g.Title == "Chapters")?.Details.Select(d => d.Label + " " + d.Value).SequenceEqual(["0:00 Opening", "0:01 Ending"]) == true,
                "an MP4 with only Nero chapters lists them");

            var avi = Path.Combine(directory, "old.avi");
            File.WriteAllBytes(avi, Avi());
            details = VideoFileDetails.Read(avi);
            check(Value(details, "File", "Format") == "AVI" && Value(details, "File", "Duration") == "0:10" && Value(details, "File", "Writing application") == "Fixture encoder",
                "AVI details give the duration its frame count and rate make, and its INFO tags");
            check(Value(details, "Video", "Codec") == "MPEG-4 Visual (Xvid)" && Value(details, "Video", "Resolution") == "720 × 400" && Value(details, "Video", "Frame rate") == "25 fps" &&
                Value(details, "Audio", "Codec") == "MP3" && Value(details, "Audio", "Bitrate") == "192 kb/s" && Value(details, "Audio", "Sample rate") == "44.1 kHz",
                "AVI streams list their codec, picture, frame rate and audio format");

            var damaged = Path.Combine(directory, "damaged.mkv");
            File.WriteAllBytes(damaged, File.ReadAllBytes(mkv)[..120]);
            details = VideoFileDetails.Read(damaged);
            check(details.Count >= 1 && Value(details, "File", "Format") == "Matroska" && Value(details, "File", "Note") is not null,
                "a damaged file still lists what could be read, and says that some details are missing");
            check(VideoFileDetails.Read(Path.Combine(directory, "missing.mkv")).Count == 0, "a file that isn't there has no details");
            // An unfinished download: the name says Matroska, the bytes are zeros, and TagLib throws plain exceptions on them.
            var unreadable = true;
            foreach (var (name, length) in new[] { ("zeros.mkv", 100), ("empty.mkv", 0), ("zeros.webm", 3) })
            {
                var path = Path.Combine(directory, name);
                File.WriteAllBytes(path, new byte[length]);
                try { details = VideoFileDetails.Read(path); unreadable &= Value(details, "File", "Size") is not null; }
                catch (Exception) { unreadable = false; }
            }
            check(unreadable, "a zero-filled or empty Matroska file lists its size instead of throwing");
            var unknown = Path.Combine(directory, "clip.ts");
            File.WriteAllBytes(unknown, new byte[188 * 4]);
            details = VideoFileDetails.Read(unknown);
            check(Value(details, "File", "Format") == "MPEG transport stream" && Value(details, "File", "Size") is not null,
                "a format nothing reads still lists the file's format and size");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    // Appends udta/chpl (version 1: flags, reserved, count, then 100 ns start, title length and title) to the file's last box, its moov.
    private static byte[] WithNeroChapters(byte[] file, string[] titles)
    {
        var moov = 0;
        for (var position = 0; position + 8 <= file.Length; position += (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position)))
            if (Encoding.ASCII.GetString(file, position + 4, 4) == "moov") moov = position;
        var entries = titles.SelectMany((title, i) =>
        {
            var start = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(start, i * TimeSpan.TicksPerSecond);
            var text = Encoding.UTF8.GetBytes(title);
            return (byte[])[.. start, (byte)text.Length, .. text];
        }).ToArray();
        static byte[] Box(string type, byte[] body) { var size = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(8 + body.Length)); return [.. size, .. Encoding.ASCII.GetBytes(type), .. body]; }
        var udta = Box("udta", Box("chpl", [1, 0, 0, 0, 0, 0, 0, 0, (byte)titles.Length, .. entries]));
        var result = (byte[])[.. file, .. udta];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(moov), BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(moov)) + (uint)udta.Length);
        return result;
    }

    // RIFF 'AVI ': hdrl (avih, a video strl with Xvid, an audio strl with MP3), INFO with ISFT, and an empty movi.
    private static byte[] Avi()
    {
        static byte[] Chunk(string id, params byte[][] parts) { var body = parts.SelectMany(p => p).ToArray(); return [.. Encoding.ASCII.GetBytes(id), .. U32((uint)body.Length), .. body, .. body.Length % 2 == 1 ? new byte[1] : []]; }
        static byte[] List(string type, params byte[][] parts) => Chunk("LIST", [Encoding.ASCII.GetBytes(type), .. parts]);
        static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes; }
        static byte[] U16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); return bytes; }
        var avih = Chunk("avih", U32(40000), U32(0), U32(0), U32(0x110), U32(250), U32(0), U32(2), U32(0), U32(720), U32(400), new byte[16]);
        var video = List("strl", Chunk("strh", Encoding.ASCII.GetBytes("vids"), Encoding.ASCII.GetBytes("XVID"), U32(0), U16(0), U16(0), U32(0), U32(1), U32(25), U32(0), U32(250), new byte[20]),
            Chunk("strf", U32(40), U32(720), U32(400), U16(1), U16(12), Encoding.ASCII.GetBytes("XVID"), new byte[20]));
        var audio = List("strl", Chunk("strh", Encoding.ASCII.GetBytes("auds"), new byte[4], U32(0), U16(0), U16(0), U32(0), U32(1), U32(24000), U32(0), U32(0), new byte[20]),
            Chunk("strf", U16(0x55), U16(2), U32(44100), U32(24000), U16(1), U16(0), U16(0)));
        var info = List("INFO", Chunk("ISFT", Encoding.ASCII.GetBytes("Fixture encoder\0")));
        var body = new byte[][] { Encoding.ASCII.GetBytes("AVI "), List("hdrl", avih, video, audio), info, List("movi") }.SelectMany(p => p).ToArray();
        return [.. Encoding.ASCII.GetBytes("RIFF"), .. U32((uint)body.Length), .. body];
    }
}
