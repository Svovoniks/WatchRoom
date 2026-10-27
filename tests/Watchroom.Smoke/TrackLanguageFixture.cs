using Watchroom.Core;

static class TrackLanguageFixture
{
    public static void Run(Action<bool, string> check)
    {
        var preferences = TrackLanguages.Parse("Japanese, English, Russian");
        check(preferences.SequenceEqual(["ja", "en", "ru"]), "language names preserve the user's preference order");
        check(TrackLanguages.Parse("eng, en-US, fre, fr, arm, hye").SequenceEqual(["en", "fr", "hy"]), "regional and alternate ISO codes normalize and deduplicate");
        check(TrackLanguages.Parse("nb, nob, Norwegian, nor").SequenceEqual(["no"]) && TrackLanguages.Parse("nn").SequenceEqual(["nn"]),
            "Bokmål and Norwegian codes are one language, while Nynorsk stays separate");
        LanguageTrack[] tracks = [new(0, "English", "eng"), new(1, "Russian", "rus"), new(2, "Japanese", "jpn")];
        check(TrackLanguages.Select(tracks, preferences) == 2, "most preferred language wins regardless of track order");
        check(TrackLanguages.Select(tracks[..2], preferences) == 0, "missing first preference falls back to the next available language");
        check(TrackLanguages.Select(tracks, ["de"]) is null, "no matching language preserves the video default");
        check(TrackLanguages.Select(tracks, []) is null, "blank preferences leave native track selection unchanged");
        check(TrackLanguages.Select([new(4, "Track 4 [Russian]", null)], ["ru"]) == 4, "track labels identify language when metadata is missing");
        check(TrackLanguages.Select([new(4, "French commentary", "eng")], ["fr"]) is null, "explicit language metadata takes precedence over an ambiguous label");
        var subtitles = TrackLanguages.Parse("English, off, Russian", true);
        check(TrackLanguages.Select(tracks, subtitles, true) == 0, "preferred subtitles are enabled ahead of the off fallback");
        check(TrackLanguages.Select(tracks[1..], subtitles, true) == -1, "off disables subtitles before less preferred languages");
        check(TrackLanguages.Select(tracks, ["off"], true) == -1, "off as the first preference always disables subtitles");
        bool rejected = false;
        try { TrackLanguages.Parse("Englsh"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "unknown language names receive actionable validation");
        var rules = TrackLanguages.ParseSubtitleRules([new("Japanese", "Russian, English, off"), new("eng", "off")]);
        check(rules[0].AudioLanguage == "ja" && rules[0].Languages.SequenceEqual(["ru", "en", "off"]), "per-audio subtitle rules normalize names and preserve priority order");
        check(TrackLanguages.Select(tracks, TrackLanguages.SubtitlePreferences(tracks[2], ["en"], rules), true) == 1,
            "selected Japanese audio uses its subtitle rule ahead of the default list");
        check(TrackLanguages.Select(tracks, TrackLanguages.SubtitlePreferences(tracks[0], ["ru"], rules), true) == -1,
            "an audio-specific off rule overrides the default subtitles");
        check(TrackLanguages.SubtitlePreferences(tracks[1], ["en", "off"], rules).SequenceEqual(["en", "off"]), "unspecified audio languages use the default subtitle priority");
        check(TrackLanguages.SubtitlePreferences(new(3, "Track 3", "und"), ["en"], rules).SequenceEqual(["en"]), "unknown audio language uses the default subtitle priority");
        check(TrackLanguages.SubtitlePreferences(new(3, "Japanese", null), ["en"], rules).SequenceEqual(["ru", "en", "off"]), "audio labels select rules when language tags are missing");
        check(TrackLanguages.SubtitlePreferences(new(3, "Japanese", "en-US"), ["ru"], rules).SequenceEqual(["off"]), "audio tags take precedence over labels and regional variants match rules");
        check(TrackLanguages.Select(tracks[..1], TrackLanguages.SubtitlePreferences(tracks[2], ["de"], rules), true) == 0,
            "missing first subtitle priority falls through within the selected audio rule");
        var blankRule = TrackLanguages.ParseSubtitleRules([new("ja", "")]);
        check(TrackLanguages.SubtitlePreferences(tracks[2], ["en"], blankRule).Count == 0, "an explicit blank rule preserves video defaults instead of using the unspecified-language list");
        rejected = false;
        try { TrackLanguages.ParseSubtitleRules([new("English", "ru"), new("en-US", "ja")]); } catch (ArgumentException) { rejected = true; }
        check(rejected, "duplicate audio rules are rejected even when entered using aliases");
        rejected = false;
        try { TrackLanguages.ParseSubtitleRules([new("en-US, ru", "en")]); } catch (ArgumentException) { rejected = true; }
        check(rejected, "each subtitle rule requires one valid audio language");
        UntaggedTracks(check);
        Mp4Tracks(check);
        SubtitleFileNames(check);
        CachedLanguages(check);
    }

    // Cached languages are kept for files that are unchanged or on a drive that isn't connected, and removed otherwise.
    private static void CachedLanguages(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "watchroom-track-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new LibraryStore(Path.Combine(directory, "db"));
            string Video(string name) { var path = Path.Combine(directory, name); File.WriteAllText(path, name); return path; }
            string Cache(string path, string key) { store.CacheTrackLanguage(path, key, true, 1, "ja"); store.CacheTrackLanguage(path, key, false, 2, null); return key; }
            var kept = Video("kept.mkv"); var keptKey = Cache(kept, LibraryStore.TrackLanguageFile(kept)!);
            var deleted = Video("deleted.mkv"); var deletedKey = Cache(deleted, LibraryStore.TrackLanguageFile(deleted)!); File.Delete(deleted);
            var changed = Video("changed.mkv"); var changedKey = Cache(changed, LibraryStore.TrackLanguageFile(changed)!);
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddDays(1));
            var drive = Enumerable.Range('D', 23).Select(letter => (char)letter + @":\").First(root => !Directory.Exists(root));
            var offlineKey = Cache(drive + @"Videos\offline.mkv", "OFFLINE00000000000000000");
            store.Setting(LibraryStore.TrackLanguageKey("UNINDEXED000000000000000", true, 0), "fr");
            check(LibraryStore.TrackLanguageFile(changed) != changedKey && LibraryStore.TrackLanguageFile(deleted) is null,
                "a changed or deleted file no longer matches its cached languages");
            check(store.RemoveStaleTrackLanguages() == 7, "cleanup removes the cached languages of deleted, changed and unrecorded files");
            check(store.Setting(LibraryStore.TrackLanguageKey(keptKey, true, 1)) == "ja" && store.Setting(LibraryStore.TrackLanguageKey(keptKey, false, 2)) == "" &&
                store.Setting(LibraryStore.TrackLanguageKey(offlineKey, true, 1)) == "ja",
                "cleanup keeps the cached languages of unchanged files and of files on a drive that isn't connected");
            check(store.Setting(LibraryStore.TrackLanguageKey(deletedKey, true, 1)) is null && store.Setting(LibraryStore.TrackLanguageKey(changedKey, false, 2)) is null &&
                store.RemoveStaleTrackLanguages() == 0, "removed entries stay removed, and a second cleanup finds nothing");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    // MP4 tracks and their subtitle samples, read through the sample tables.
    private static void Mp4Tracks(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "watchroom-mp4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "untagged.mp4");
            Mp4Fixture.Write(path, [new(new short[8000 * 24], 8000), new(new short[8000 * 24], 8000, "jpn")],
            [
                new("tx3g", ["<i>Where are you going?</i>", "I told you, I'm going home.", "Not without me, you aren't."]),
                new("wvtt", ["- Привет.", "<v Anna>- Как дела?</v>", "- Хорошо, спасибо."]),
                new("tx3g", ["Hallo."], "deu"),
            ]);
            var tracks = SubtitleText.Tracks(path);
            check(tracks.Select(t => (t.Number, t.Type, t.Codec)).SequenceEqual([(1, ContainerTrackType.Audio, "sowt"), (2, ContainerTrackType.Audio, "sowt"),
                (3, ContainerTrackType.Subtitle, "tx3g"), (4, ContainerTrackType.Subtitle, "wvtt"), (5, ContainerTrackType.Subtitle, "tx3g")]),
                "MP4 tracks are listed in file order with their IDs, types and codecs");
            check(tracks.Select(t => t.Untagged).SequenceEqual([true, false, true, true, false]) && tracks[1].Language == "jpn",
                "MP4 tracks marked und count as untagged while explicit codes do not");
            var text = SubtitleText.Sample(path, [3, 4, 1]);
            check(text.Keys.Order().SequenceEqual([3, 4]), "MP4 dialogue is sampled only from requested text subtitle tracks");
            check(text[3].Contains("Where are you going?") && text[3].Contains("Not without me") && !text[3].Contains('<'), "tx3g samples yield their text without markup");
            check(text[4].Contains("- Привет.") && text[4].Contains("- Как дела?") && !text[4].Contains("Anna"), "WebVTT samples yield the cue text of their payload boxes");
            check(text[3].Split('\n').Count(line => line == "Where are you going?") == 4, "every MP4 cue is read once, and empty samples between cues are skipped");
            var chaptered = Path.Combine(directory, "chapters.mp4");
            Mp4Fixture.Write(chaptered, [new(new short[8000 * 20], 8000, "rus")], [new("tx3g", ["Chapter 1", "Chapter 2"], Chapters: true), new("tx3g", ["Hallo."], "deu")]);
            check(SubtitleText.Tracks(chaptered).Select(t => (t.Number, t.Type, t.Text)).SequenceEqual([(1, ContainerTrackType.Audio, false),
                (2, ContainerTrackType.Other, false), (3, ContainerTrackType.Subtitle, true)]) && SubtitleText.Sample(chaptered, [2]).Count == 0,
                "QuickTime chapter title tracks are not listed or sampled as subtitles");
            check(SubtitleText.Tracks(Path.Combine(directory, "missing.mp4")).Count == 0, "missing MP4 files have no tracks");
            File.WriteAllBytes(Path.Combine(directory, "broken.mp4"), [0, 0, 0, 8, .. "moov"u8, 0, 0, 0, 99, .. "trak"u8]);
            check(SubtitleText.Tracks(Path.Combine(directory, "broken.mp4")).Count == 0 && SubtitleText.Sample(Path.Combine(directory, "broken.mp4"), [1]).Count == 0,
                "damaged MP4 files read as having no tracks");
        }
        finally { Directory.Delete(directory, true); }
    }

    // What subtitle file names say, and which files VLC loads beside a video.
    private static void SubtitleFileNames(Action<bool, string> check)
    {
        check(SubtitleFiles.Language("Movie.ru.srt") == "ru" && SubtitleFiles.Language("Movie.en.forced.srt") == "en" && SubtitleFiles.Language("Movie_pt-BR.ass") == "pt" &&
            SubtitleFiles.Language("Movie.English.SDH.srt") == "en" && SubtitleFiles.Language(@"C:\Videos\Show.S01E01.rus.srt") == "ru",
            "subtitle file names give their language, ignoring words such as forced and SDH");
        check(SubtitleFiles.Language("Movie.srt") is null && SubtitleFiles.Language("English.srt") is null && SubtitleFiles.Language("Movie.forced.srt") is null &&
            SubtitleFiles.Language("Show.S01E01.srt") is null && SubtitleFiles.Language("Movie.2010.srt") is null && SubtitleFiles.Language("Sherlock - It.srt") is null,
            "subtitle file names without a language word, or only a title, give none");
        check(SubtitleFiles.VlcUntagged("Movie.srt") && SubtitleFiles.VlcUntagged("Movie.en.forced.srt") && SubtitleFiles.VlcUntagged("Movie ru.srt") &&
            !SubtitleFiles.VlcUntagged("Movie.ru.srt") && !SubtitleFiles.VlcUntagged("Movie.English.srt"),
            "only a language after the last dot of a subtitle file's name is one VLC tags");

        var directory = Path.Combine(Path.GetTempPath(), "watchroom-sidecars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Subs"));
        try
        {
            foreach (var name in new[] { "The Movie (2010).mkv", "the.movie.2010.srt", "The Movie (2010) ru.ass", "The Movie (2010).en.srt", "The Movie (2010).sub",
                "The Movie (2010).idx", "The Movie (2010).nfo", "The Movie.srt", "Other.srt", @"Subs\The_Movie_2010.vtt", @"Subs\readme.txt" })
                File.WriteAllText(Path.Combine(directory, name), "");
            var found = SubtitleFiles.VlcSidecars(Path.Combine(directory, "The Movie (2010).mkv")).Select(file => Path.GetRelativePath(directory, file)).Order().ToArray();
            check(found.SequenceEqual(new[] { "the.movie.2010.srt", "The Movie (2010) ru.ass", "The Movie (2010).en.srt", "The Movie (2010).idx", @"Subs\The_Movie_2010.vtt" }.Order()),
                "subtitle files VLC loads beside a video are those named as it or starting with its name, ignoring case and punctuation, and in a Subs folder");
        }
        finally { Directory.Delete(directory, true); }
    }

    // Reading untagged Matroska tracks and their dialogue, the evidence the desktop app's language models classify.
    private static void UntaggedTracks(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "watchroom-untagged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "untagged.mkv");
            MatroskaFixture.Write(path, [new(new short[16000 * 30], 16000), new(new short[16000 * 30], 16000, "jpn", "Commentary")],
            [
                new("S_TEXT/UTF8", [@"{\an8}<i>Where are you going?</i>", "I told you, I'm going home.", "Not without me, you aren't."]),
                new("S_TEXT/ASS", [@"{\an8\c&H00FFFF&}Bonjour,\Nça va?", @"{\p1}m 0 0 l 100 0 100 100{\p0}", "Je ne sais pas."], "und", "zlib"),
                new("S_TEXT/UTF8", ["- Привет.", "- Как дела?", "- Хорошо, спасибо."], null, "strip"),
                new("S_TEXT/UTF8", ["Hallo."], "ger", Name: "Signs", Forced: true),
            ]);
            var tracks = SubtitleText.Tracks(path);
            check(tracks.Select(t => t.Type).SequenceEqual([ContainerTrackType.Audio, ContainerTrackType.Audio, ContainerTrackType.Subtitle,
                ContainerTrackType.Subtitle, ContainerTrackType.Subtitle, ContainerTrackType.Subtitle]), "Matroska tracks are listed in file order with their types");
            check(tracks.Select(t => t.Untagged).SequenceEqual([true, false, true, true, true, false]), "absent and und languages count as untagged while explicit codes do not");
            check(tracks[1].Name == "Commentary" && tracks[5] is { Name: "Signs", Forced: true } && tracks[0].Name is null && !tracks[2].Forced,
                "Matroska track names and forced flags are read");
            var text = SubtitleText.Sample(path, [3, 4, 5, 1]);
            check(text.Keys.Order().SequenceEqual([3, 4, 5]), "dialogue is sampled only from requested text subtitle tracks");
            check(text[3].Contains("Where are you going?") && text[3].Contains("Not without me") && !text[3].Contains('<') && !text[3].Contains('{'), "plain subtitle events lose their markup and positioning tags");
            check(text[4].Contains("Bonjour, ça va?") && text[4].Contains("Je ne sais pas.") && !text[4].Contains('{') && !text[4].Contains("m 0 0"),
                "zlib-compressed ASS events keep only their dialogue, without overrides or drawings");
            check(text[5].Contains("- Привет.") && text[5].Contains("- Как дела?"), "header-stripped events are restored before reading");
            // 15 events cycle through three lines: five of each when the file is read once.
            check(text[3].Split('\n').Count(line => line == "Where are you going?") == 5, "short files are read once rather than once per sampling window");
            var checksummed = Path.Combine(directory, "checksums.mkv");
            MatroskaFixture.Write(checksummed, [new(new short[16000 * 30], 16000)], [new("S_TEXT/UTF8", ["Where are you going?"])], checksums: true);
            check(SubtitleText.Sample(checksummed, [2]).GetValueOrDefault(2)?.Contains("Where are you going?") == true,
                "dialogue is read from clusters that start with a CRC-32 element, as ffmpeg writes them");
            var karaoke = Path.Combine(directory, "karaoke.mkv");
            MatroskaFixture.Write(karaoke, [new(new short[16000 * 20], 16000)],
                [new("S_TEXT/ASS", ["sa", "sa", "些", "些", "Why don't we take these good times", "Why don't we take these good times", "chi"])]);
            check(SubtitleText.Sample(karaoke, [2]).GetValueOrDefault(2) == "Why don't we take these good times",
                "karaoke syllables and layered repeats of a line are left out of the sampled dialogue");
            check(SubtitleText.Tracks(Path.Combine(directory, "missing.mkv")).Count == 0 && SubtitleText.Tracks(typeof(TrackLanguageFixture).Assembly.Location).Count == 0,
                "missing files and other containers have no Matroska tracks");

            var srt = Path.Combine(directory, "sidecar.srt");
            File.WriteAllText(srt, "1\r\n00:00:01,000 --> 00:00:02,000\r\n<b>Hello</b> there.\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nGeneral Kenobi!\r\n");
            check(SubtitleText.FromFile(srt) == "Hello there.\nGeneral Kenobi!", "subtitle files yield their dialogue without numbers, timings or tags");
            var marked = Path.Combine(directory, "marked.srt");
            File.WriteAllText(marked, "1\r\n00:00:01,000 --> 00:00:02,000\r\n&lrm;ทีนี้ก็อยู่นิ่งๆ\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\n‫אל תזוזי.‬ Tom &amp; Jerry\r\n");
            check(SubtitleText.FromFile(marked) == "ทีนี้ก็อยู่นิ่งๆ\nאל תזוזי. Tom & Jerry", "HTML entities are decoded and invisible direction marks dropped");
            var ass = Path.Combine(directory, "sidecar.ass");
            File.WriteAllText(ass, "[Script Info]\nTitle: x\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\i1}Hola, amigo{\\i0}\n");
            check(SubtitleText.FromFile(ass) == "Hola, amigo", "ASS files yield the text field of dialogue lines");
        }
        finally { Directory.Delete(directory, true); }
    }
}
