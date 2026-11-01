using System.IO;
using System.Reflection;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;
using Windows.Media.SpeechSynthesis;

// The bundled language models on untagged tracks: CLD3 on subtitle text, Whisper tiny on speech synthesized by
// the voices Windows has installed, and the player naming and choosing tracks by the languages found.
static class LanguageDetectionChecks
{
    private static readonly Dictionary<string, string> Speech = new()
    {
        ["en"] = "Good evening. Tonight we are talking about the weather, the long road home, and the small town where my grandmother lived. " +
            "She kept a garden behind the house, and every summer we picked tomatoes and beans until the baskets were full.",
        ["ru"] = "Добрый вечер. Сегодня мы поговорим о погоде, о долгой дороге домой и о маленьком городе, где жила моя бабушка. " +
            "За домом у неё был огород, и каждое лето мы собирали помидоры и фасоль, пока корзины не были полными.",
        ["de"] = "Guten Abend. Heute sprechen wir über das Wetter, den langen Weg nach Hause und die kleine Stadt, in der meine Großmutter lebte. " +
            "Hinter dem Haus hatte sie einen Garten, und jeden Sommer pflückten wir Tomaten und Bohnen, bis die Körbe voll waren.",
        ["fr"] = "Bonsoir. Ce soir, nous parlons du temps qu'il fait, du long chemin du retour et de la petite ville où vivait ma grand-mère. " +
            "Elle avait un jardin derrière la maison, et chaque été nous cueillions des tomates et des haricots jusqu'à remplir les paniers.",
        ["es"] = "Buenas noches. Esta noche hablamos del tiempo, del largo camino a casa y del pequeño pueblo donde vivía mi abuela. " +
            "Tenía un huerto detrás de la casa, y cada verano recogíamos tomates y judías hasta llenar las cestas.",
        ["ja"] = "こんばんは。今夜は天気のこと、家までの長い道のり、そして祖母が住んでいた小さな町について話します。" +
            "祖母は家の裏に畑を持っていて、毎年夏になると、かごがいっぱいになるまでトマトや豆を摘みました。",
    };
    private static readonly string[] English = ["Where are you going at this hour?", "I told you already, I am going home.", "Not without me, you are not.",
        "Then hurry up, the last train leaves in ten minutes.", "Wait for me at the station, I will bring the tickets."];
    private static readonly string[] Russian = ["Куда ты идёшь в такой час?", "Я же тебе сказал, я иду домой.", "Без меня ты никуда не пойдёшь.",
        "Тогда поторопись, последний поезд уходит через десять минут.", "Жди меня на вокзале, я принесу билеты."];

    public static async Task Run(string directory)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

        Check(LanguageDetection.SpeechAvailable, "the Whisper tiny model is bundled next to the app");
        // libVLC 3 aborts the process after about 80 engines have been created and released.
        using (var first = BackgroundVlc.Acquire())
        using (var second = BackgroundVlc.Acquire())
            Check(first is not null && ReferenceEquals(first.Engine, second?.Engine), "frame captures and audio decoding share one long-lived libVLC engine");
        Check(LanguageDetection.Written(string.Join("\n", English)) == "en", "English dialogue is identified from its text");
        Check(LanguageDetection.Written(string.Join("\n", Russian)) == "ru", "Russian dialogue is identified from its text");
        Check(LanguageDetection.Written("夜遅くにどこへ行くの？\nもう言ったでしょ、家に帰るの。\n私も一緒に行く。\n急いで、最終電車は十分後に出るよ。") == "ja", "Japanese dialogue is identified from its text");
        Check(LanguageDetection.Written("Où vas-tu à cette heure-ci ?\nJe te l'ai déjà dit, je rentre à la maison.\nPas sans moi.\nAlors dépêche-toi, le dernier train part dans dix minutes.") == "fr",
            "French dialogue is identified from its text");
        // Read whole, a Latin title line ahead of the dialogue makes CLD3 call Arabic Swedish and Korean Hindi.
        string Long(params string[] lines) => "DARK MATTER\n" + string.Join("\n", Enumerable.Repeat(lines, 20).SelectMany(line => line));
        Check(LanguageDetection.Written(Long("أي واحد أنت؟", "أظن أنك تعرف.", "ماذا تفعل في هذا العالم؟", "لم أنت هنا؟", "أكفّر عن ذنوبي.")) == "ar" &&
            LanguageDetection.Written(Long("Ποιος απ' όλους είσαι;", "Νομίζω πως ξέρεις.", "Τι κάνεις σ' αυτόν τον κόσμο;", "Γιατί ήρθες εδώ;")) == "el" &&
            LanguageDetection.Written(Long("넌 어떤 제이슨이야?", "알 텐데", "들어가도 될까?", "이 세계에서 뭐 하는 거야?", "여긴 왜 왔어?", "속죄하러")) == "ko",
            "Arabic, Greek and Korean dialogue is identified despite a Latin title line before it");
        Check(LanguageDetection.Written("Hi.") is null && LanguageDetection.Written("") is null, "too little text claims no language");
        // Just over one chunk: the short remainder can't be read confidently, and mustn't outvote the chunk before it.
        var overOne = new List<string>();
        while (System.Text.Encoding.UTF8.GetByteCount(string.Join("\n", overOne)) < 1010) overOne.Add(English[overOne.Count % English.Length]);
        Check(LanguageDetection.Written(string.Join("\n", overOne)) == "en", "dialogue a little longer than one chunk is identified");

        // Picture subtitles: the bundled Tesseract script models read rendered lines, and CLD3 names their language.
        Check(SubtitleOcr.Available, "Tesseract's script models are bundled next to the app");
        Check(LanguageDetection.Written(SubtitleOcr.Read(English.Select(Picture).ToArray(), CancellationToken.None)) == "en" &&
            LanguageDetection.Written(SubtitleOcr.Read(Russian.Select(Picture).ToArray(), CancellationToken.None)) == "ru",
            "English and Russian picture subtitles are read and identified");
        Check(SubtitleOcr.Read([Picture("")], CancellationToken.None) is null, "a picture without text yields no text");

        // Speech in every language Windows has a voice for; machines without voices only check the subtitle path.
        var spoken = new List<(string Language, MatroskaFixture.Audio Audio)>();
        foreach (var (language, text) in Speech)
        {
            var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
            if (voice is null) continue;
            using var synthesizer = new SpeechSynthesizer { Voice = voice };
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            using var input = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream);
            using var buffer = new MemoryStream(); await input.CopyToAsync(buffer);
            if (Wav(buffer.ToArray()) is { } audio) spoken.Add((language, audio));
        }
        Console.WriteLine("Voices: " + (spoken.Count == 0 ? "none installed" : string.Join(", ", spoken.Select(s => s.Language))));
        var path = Path.Combine(directory, "untagged-languages.mkv");
        var silence = new MatroskaFixture.Audio(new short[16000 * 24], 16000);
        MatroskaFixture.Write(path, spoken.Count == 0 ? [silence] : spoken.Select(s => s.Audio).ToArray(),
            [new("S_TEXT/UTF8", English), new("S_TEXT/UTF8", Russian, "und", "zlib"), new("S_TEXT/UTF8", ["Hallo, wie geht's?"], "ger"), new("S_TEXT/UTF8", ["Hm."])]);
        var audioCount = Math.Max(1, spoken.Count);
        for (var i = 0; i < spoken.Count; i++)
            Check(await LanguageDetection.SpokenAsync(path, i, 0, CancellationToken.None) == spoken[i].Language,
                $"speech in an untagged audio track is identified as {spoken[i].Language}");
        if (spoken.Count == 0) Check(await LanguageDetection.SpokenAsync(path, 0, 0, CancellationToken.None) is null, "silence claims no spoken language");

        Core.Initialize();
        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        using var media = new Media(engine, path, FromType.FromPath);
        var window = new MainWindow();
        void Field(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Wait(Func<bool> condition, string what, int seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
            if (!condition()) throw new Exception("Timed out: " + what);
        }
        string[] Names(string list) => ((ListBox)window.FindName(list)).Items.Cast<object>()
            .Where(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! >= 0).Select(item => item.ToString()!).ToArray();
        var english = audioCount; var russian = audioCount + 1; var german = audioCount + 2;
        try
        {
            ((LanguagePriorityPicker)window.FindName("PreferredAudioLanguages")).Text = "";
            ((LanguagePriorityPicker)window.FindName("PreferredSubtitleLanguages")).Text = "Russian, German";
            Check((bool)Call("SaveTrackPreferences")!, "subtitle preferences naming an untagged track's language save");
            Field("player", player); Field("vlc", engine); Field("loadGeneration", 1);
            player.Play(media);
            await Wait(() => player.SpuDescription.Count(t => t.Id >= 0) == 4 && player.AudioTrackDescription.Count(t => t.Id >= 0) == audioCount, "native tracks");
            Field("ready", true);
            Call("RefreshTracks");
            Check(Names("SubtitleTracks").Count(name => name.Contains("identifying language")) == 3 && player.Spu != russian,
                "untagged subtitles are marked as being identified and preferences wait for them");
            await Wait(() => !(bool)Call("TrackLanguagesPending", (TrackType?)null)!, "language detection");
            Call("RefreshTracks");
            var subtitles = Names("SubtitleTracks");
            Check(subtitles.Any(name => name.Contains("[English]") && name.Contains("Detected")) && subtitles.Any(name => name.Contains("[Russian]") && name.Contains("Detected")),
                "detected subtitle languages name their tracks");
            // VLC calls an untagged Matroska track English, as the format's default says; too little text to tell keeps it unknown.
            Check(subtitles.Count(name => name.EndsWith("[Unknown language]")) == 1 && !subtitles.Any(name => name.Contains("[English]") && !name.Contains("Detected")),
                "an untagged track whose language can't be identified is named as unknown rather than English");
            await Wait(() => player.Spu == russian, "preferred subtitles");
            Check(true, "a preferred language found by detection selects the untagged subtitle track ahead of a tagged one");
            if (spoken.Count > 0)
                Check(Names("AudioTracks").Select((name, i) => name.Contains("[" + System.Globalization.CultureInfo.GetCultureInfo(spoken[i].Language).EnglishName + "]")).All(found => found),
                    "detected spoken languages name the audio tracks");

            Field("loadGeneration", 2); Call("RefreshTracks");
            Check(!(bool)Call("TrackLanguagesPending", (TrackType?)null)! && Get<CancellationTokenSource?>("languageDetectionCancellation") is null &&
                Names("SubtitleTracks").Count(name => name.Contains("Detected")) == 2, "detected languages are cached for the file and reused without analysis");
        }
        finally { player.Stop(); Field("player", null); Field("vlc", null); Field("closing", true); window.Close(); }
        await FileSubtitles(directory, Check);
        await AheadOfPlayback(directory, spoken, Check);
    }

    // A video's page lists its tracks and identifies the untagged ones before it plays; so does the episode after the
    // playing one. Playback joins identification already under way instead of starting it again.
    private static async Task AheadOfPlayback(string directory, List<(string Language, MatroskaFixture.Audio Audio)> spoken, Action<bool, string> Check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var folder = Path.Combine(directory, "ahead"); Directory.CreateDirectory(folder);
        var paths = new[] { Path.Combine(folder, "Show S01E01.mkv"), Path.Combine(folder, "Show S01E02.mkv") };
        var voice = spoken.Count > 0 ? spoken[0].Audio : new MatroskaFixture.Audio(new short[16000 * 24], 16000);
        foreach (var path in paths)
            MatroskaFixture.Write(path, [voice, voice with { Language = "jpn", Name = "Commentary" }],
                [new("S_TEXT/UTF8", English), new("S_TEXT/UTF8", ["Hallo, wie geht's?"], "ger", Name: "Signs", Forced: true)]);
        File.WriteAllText(Path.Combine(folder, "Show S01E01.ru.srt"), string.Concat(Russian.Select((line, i) => $"{i + 1}\n00:00:{i * 2:00},000 --> 00:00:{i * 2 + 1:00},000\n{line}\n\n")));
        var episodes = paths.Select((path, i) => new MediaItem("ahead-" + (i + 1), path, "Show", "Show", Series: "Show", Season: 1, Episode: i + 1)).ToArray();
        var speech = spoken.Count > 0 ? System.Globalization.CultureInfo.GetCultureInfo(spoken[0].Language).EnglishName : null;

        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        var window = new MainWindow();
        void Field(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        T Get<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Wait(Func<bool> condition, string what, int seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
            if (!condition()) throw new Exception("Timed out: " + what);
        }
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
        var library = Get<LibraryStore>("library");
        var work = Get<Dictionary<string, Task>>("languageWork");
        string Key(string path, bool audio, int id) => LibraryStore.TrackLanguageKey(LibraryStore.TrackLanguageFile(path)!, audio, id);
        try
        {
            Field("items", episodes.ToList());
            Call("Select", episodes[0]);
            await Wait(() => ((System.Windows.FrameworkElement)window.FindName("DetailTracks")).Visibility == System.Windows.Visibility.Visible, "the video page's track list");
            if (speech is not null)
                Check(Text("DetailAudio").StartsWith("Unknown language · identifying…, Japanese – Commentary"), "a video page lists its audio and marks untagged tracks being identified");
            await Wait(() => Get<string?>("prefetchPage") is null && !Text("DetailAudio").Contains("identifying"), "identification from the video page");
            Check(Text("DetailAudio") == (speech is null ? "Unknown language" : speech + " (detected)") + ", Japanese – Commentary" &&
                library.Setting(Key(paths[0], true, 0)) == (spoken.Count > 0 ? spoken[0].Language : ""),
                "opening a video page identifies its untagged audio before it plays and caches the result");
            Check(Text("DetailSubtitles") == "English (detected), German – Signs (forced), Russian (file)" && library.Setting(Key(paths[0], false, 2)) == "en",
                "a video page lists subtitles with their names, forced flags, identified languages and subtitle files");

            // The episode after the playing one is identified next; starting it early joins that work.
            Field("playingItem", episodes[0]);
            Call("PrefetchNextLanguages");
            Check(Get<string?>("prefetchNext") == paths[1], "the episode after the playing one is queued for identification");
            if (speech is null) await Wait(() => library.Setting(Key(paths[1], true, 0)) is not null, "next episode identification");
            else
            {
                await Wait(() => work.ContainsKey(Key(paths[1], true, 0)), "next episode speech identification");
                var running = work[Key(paths[1], true, 0)];
                Field("player", player); Field("vlc", engine); Field("loadGeneration", 1); Field("playingItem", episodes[1]);
                using var media = new Media(engine, paths[1], FromType.FromPath);
                player.Play(media);
                await Wait(() => player.AudioTrackDescription.Count(t => t.Id >= 0) == 2, "native tracks of the next episode");
                Field("ready", true);
                Call("RefreshTracks");
                Check(work.TryGetValue(Key(paths[1], true, 0), out var joined) && ReferenceEquals(joined, running) &&
                    (bool)Call("TrackLanguagesPending", (TrackType?)TrackType.Audio)!, "playback joins identification already under way instead of starting it again");
                await Wait(() => !(bool)Call("TrackLanguagesPending", (TrackType?)null)!, "next episode languages in playback");
                Call("RefreshTracks");
                var audio = ((ListBox)window.FindName("AudioTracks")).Items.Cast<object>().Select(item => item.ToString()!).ToArray();
                Check(audio.Any(name => name.Contains("[" + speech + "]")), "the shared result names the playing episode's audio track");
            }
            Check(library.Setting(Key(paths[1], false, 2)) == "en", "the next episode's untagged subtitles are identified ahead of it");

            // Containers without a header reader of ours are listed by VLC's parser, once playback has loaded VLC.
            Field("vlc", null);
            var unparsed = Path.Combine(folder, "Show S01E04.wav"); File.WriteAllBytes(unparsed, [0]);
            var waiting = (Task)Call("ReadFileTracks", unparsed)!; await waiting;
            Check(Property(Property(waiting, "Result")!, "Complete") is false, "other containers wait to be parsed until playback has loaded VLC");
            Field("vlc", engine);
            var wav = Path.Combine(folder, "Show S01E03.wav");
            using (var output = new BinaryWriter(File.Create(wav)))
            {
                output.Write("RIFF"u8); output.Write(36 + voice.Samples.Length * 2); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)1); output.Write((short)1);
                output.Write(voice.SampleRate); output.Write(voice.SampleRate * 2); output.Write((short)2); output.Write((short)16); output.Write("data"u8); output.Write(voice.Samples.Length * 2);
                foreach (var sample in voice.Samples) output.Write(sample);
            }
            var reading = (Task)Call("ReadFileTracks", wav)!;
            await reading;
            var tracks = ((System.Collections.IEnumerable)Property(Property(reading, "Result")!, "Tracks")!).Cast<object>().ToArray();
            Check(tracks.Length == 1 && (TrackType)Property(tracks[0], "Type")! == TrackType.Audio && (int)Property(tracks[0], "Id")! == 0 && (bool)Property(tracks[0], "Untagged")!,
                "other containers' tracks are listed by VLC's parser with the ids playback uses");
        }
        finally { player.Stop(); Field("player", null); Field("vlc", null); Field("closing", true); window.Close(); }
    }

    // MP4 subtitles, subtitle files VLC loads beside the video, and files loaded into the player.
    private static async Task FileSubtitles(string directory, Action<bool, string> Check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var folder = Path.Combine(directory, "file-subtitles"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Episode One.mp4");
        Mp4Fixture.Write(path, [new(new short[8000 * 20], 8000)], [new("tx3g", Russian), new("tx3g", English, "eng")]);
        static string Srt(string[] lines) => string.Concat(lines.Select((line, i) => $"{i + 1}\n00:00:{i * 2:00},000 --> 00:00:{i * 2 + 1:00},000\n{line}\n\n"));
        File.WriteAllText(Path.Combine(folder, "Episode One.srt"), Srt(English)); // VLC loads it untagged
        File.WriteAllText(Path.Combine(folder, "Episode One.de.srt"), Srt(["Wohin gehst du um diese Zeit?", "Ich habe es dir doch gesagt, ich gehe nach Hause."]));
        var named = Path.Combine(folder, "extra.fr.srt"); File.WriteAllText(named, Srt(English)); // the name wins over the text
        var unnamed = Path.Combine(folder, "extra.srt"); File.WriteAllText(unnamed, Srt(Russian));

        using var engine = new LibVLC("--quiet", "--vout=dummy", "--aout=dummy");
        using var player = new MediaPlayer(engine);
        using var media = new Media(engine, path, FromType.FromPath);
        var window = new MainWindow();
        void Field(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        async Task Wait(Func<bool> condition, string what, int seconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition() && DateTime.UtcNow < until) await Task.Delay(25);
            if (!condition()) throw new Exception("Timed out: " + what);
        }
        string[] Names() => ((ListBox)window.FindName("SubtitleTracks")).Items.Cast<object>()
            .Where(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! >= 0).Select(item => item.ToString()!).ToArray();
        bool Pending() => (bool)Call("TrackLanguagesPending", (TrackType?)null)!;
        try
        {
            Field("player", player); Field("vlc", engine); Field("loadGeneration", 1);
            player.Play(media);
            await Wait(() => player.SpuDescription.Count(t => t.Id >= 0) == 4, "MP4 and sidecar subtitle tracks");
            Field("ready", true);
            Call("RefreshTracks");
            await Wait(() => !Pending(), "MP4 and sidecar subtitle languages");
            Call("RefreshTracks");
            var names = Names();
            Check(names.Count(name => name == "Detected - [Russian]") == 1, "an untagged MP4 subtitle track is identified from its tx3g text");
            Check(names.Count(name => name == "Detected - [English]") == 1 && names.Count(name => name.Contains("German")) == 1,
                "an untagged subtitle file VLC loads beside the video is identified, and a tagged one keeps its language");

            // Loaded one right after the other: each file still takes its own track.
            var watchers = new List<Task>();
            foreach (var file in new[] { named, unnamed })
            {
                var before = player.SpuDescription.Select(t => t.Id).ToHashSet();
                player.AddSlave(MediaSlaveType.Subtitle, new Uri(file).AbsoluteUri, false);
                watchers.Add((Task)Call("WatchLoadedSubtitle", file, before, 1)!);
            }
            await Task.WhenAll(watchers);
            await Wait(() => !Pending(), "loaded subtitle file languages", 10);
            Call("RefreshTracks");
            names = Names();
            Check(names.Contains("extra.fr.srt - [French]"), "a loaded subtitle file named with a language takes it from its name");
            Check(names.Contains("extra.srt · detected - [Russian]"), "a loaded subtitle file without one is identified from its text and shown by its file name");
        }
        finally { player.Stop(); Field("player", null); Field("vlc", null); Field("closing", true); window.Close(); }
    }

    // One subtitle line as a decoded picture: dark text on white, about as tall as the decoder scales lines to.
    private static SubtitleImage Picture(string line)
    {
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black, IsAntialias = true, Typeface = SkiaSharp.SKTypeface.FromFamilyName("Arial"), TextSize = 36 };
        var width = Math.Max(40, (int)paint.MeasureText(line) + 24); const int height = 64;
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Gray8));
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawText(line, 12, 44, paint);
        }
        return new SubtitleImage(width, height, bitmap.Bytes);
    }

    private static MatroskaFixture.Audio? Wav(byte[] bytes)
    {
        int rate = 0, channels = 0, bits = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4); var size = BitConverter.ToInt32(bytes, offset + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(bytes, offset + 10); rate = BitConverter.ToInt32(bytes, offset + 12); bits = BitConverter.ToInt16(bytes, offset + 22); }
            if (id == "data" && bits == 16 && rate > 0 && channels > 0)
            {
                var frames = Math.Min(size, bytes.Length - offset - 8) / 2 / channels;
                var samples = new short[frames];
                for (var i = 0; i < frames; i++) samples[i] = BitConverter.ToInt16(bytes, offset + 8 + i * 2 * channels);
                return new(samples, rate);
            }
            offset += 8 + size + (size & 1);
        }
        return null;
    }
}

// Read-only diagnostics: what the app would detect for untagged tracks of real files. Nothing is cached.
static class LanguageProbe
{
    // Arguments: files, or @list (a file with one path per line). --tagged also identifies tagged subtitles, and tagged audio
    // of every fourth file, to compare with their tags. --no-audio and --no-text skip either kind. --tsv prints one
    // tab-separated line per track for analysis. --text prints the text read from each subtitle track.
    public static async Task Run(IEnumerable<string> arguments)
    {
        var options = arguments.Where(a => a.StartsWith("--", StringComparison.Ordinal)).ToHashSet();
        var paths = arguments.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .SelectMany(a => a.StartsWith('@') ? File.ReadAllLines(a[1..]) : [a]).Where(p => p.Length > 0).ToArray();
        bool tagged = options.Contains("--tagged"), tsv = options.Contains("--tsv");
        var files = 0;
        foreach (var path in paths)
        {
            files++;
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var tracks = SubtitleText.Tracks(path);
                if (!tsv) Console.WriteLine(Path.GetFileName(path));
                // Other containers (AVI, TS…): the tracks as VLC's parser lists them, as the app reads them; with --tagged
                // every audio track is identified. Embedded subtitles there are only listed.
                foreach (var track in tracks.Count == 0 ? await ParseWithVlc(path) : [])
                {
                    var audio = track.Type == TrackType.Audio;
                    var detected = audio && (track.Language is null || tagged) && !options.Contains("--no-audio")
                        ? await LanguageDetection.SpokenAsync(path, track.Id, 0, CancellationToken.None, tsv ? null : line => Console.WriteLine("      " + line)) ?? "unknown"
                        : audio ? "" : "not read";
                    if (tsv) Console.WriteLine(string.Join('\t', path, track.Id, audio ? "Audio" : "Subtitle", track.Codec, track.Language ?? "-", (track.Name ?? "").Replace('\t', ' '), "", detected, -1));
                    else Console.WriteLine($"  id {track.Id} {(audio ? "Audio" : "Subtitle")} {track.Codec} tag={track.Language ?? "-"}{(track.Name is null ? "" : $" \"{track.Name}\"")}{(detected.Length > 0 ? " → " + detected : "")}");
                }
                var text = SubtitleText.Sample(path, tracks.Where(t => t.Text && (tagged || t.Untagged)).Select(t => t.Number).ToArray());
                // Picture subtitles are read as the app reads them; their text is counted like a text track's.
                var pictures = options.Contains("--no-text") ? new Dictionary<int, IReadOnlyList<SubtitleImage>>() :
                    SubtitleText.SampleImages(path, tracks.Where(t => t.Image && (tagged || t.Untagged)).Select(t => t.Number).ToArray());
                foreach (var (number, images) in pictures)
                    if (SubtitleOcr.Read(images, CancellationToken.None, tsv ? null : line => Console.WriteLine($"      #{number} {line}")) is { } read)
                        text = new Dictionary<int, string>(text) { [number] = read };
                for (var index = 0; index < tracks.Count; index++)
                {
                    var track = tracks[index];
                    if (track.Type is not (ContainerTrackType.Audio or ContainerTrackType.Subtitle)) continue;
                    var identify = (track.Untagged || tagged && (track.Type == ContainerTrackType.Subtitle || files % 4 == 0)) &&
                        !options.Contains(track.Type == ContainerTrackType.Audio ? "--no-audio" : "--no-text");
                    var detected = !identify ? "" : track.Type == ContainerTrackType.Audio
                        ? await LanguageDetection.SpokenAsync(path, index, 0, CancellationToken.None, tsv ? null : line => Console.WriteLine("      " + line)) ?? "unknown"
                        : track.Text || track.Image ? LanguageDetection.Written(text.GetValueOrDefault(track.Number)) ?? "unknown" : "image";
                    var chars = (track.Text || track.Image) && identify ? text.GetValueOrDefault(track.Number)?.Length ?? 0 : -1;
                    if (options.Contains("--text") && text.GetValueOrDefault(track.Number) is { } read) Console.WriteLine($"  #{track.Number} text:\n{read}");
                    if (tsv)
                        Console.WriteLine(string.Join('\t', path, track.Number, track.Type, track.Codec, TrackLanguages.Normalize(track.Language) ?? "-",
                            (track.Name ?? "").Replace('\t', ' '), track.Forced ? "forced" : "", detected, chars));
                    else
                        Console.WriteLine($"  #{track.Number} {track.Type} {track.Codec} tag={track.Language ?? "-"}{(track.Name is null ? "" : $" \"{track.Name}\"")}{(detected.Length > 0 ? " → " + detected + (chars >= 0 ? $" ({chars} chars)" : "") : "")}");
                }
                foreach (var file in SubtitleFiles.VlcSidecars(path))
                {
                    var detected = LanguageDetection.Written(SubtitleText.FromFile(file)) ?? "unknown";
                    if (tsv) Console.WriteLine(string.Join('\t', path, "file", "Sidecar", Path.GetExtension(file), SubtitleFiles.Language(Path.GetFileName(file)) ?? "-", Path.GetFileName(file), "", detected, -1));
                    else Console.WriteLine($"  file {Path.GetFileName(file)} → {detected}");
                }
            }
            catch (Exception ex) { Console.WriteLine(tsv ? string.Join('\t', path, "error", "", "", "", "", "", ex.GetType().Name + ": " + ex.Message, -1) : "  error " + ex.Message); }
            if (!tsv) Console.WriteLine($"  {started.ElapsedMilliseconds} ms");
            else Console.Error.WriteLine($"{files}/{paths.Length} {started.ElapsedMilliseconds} ms");
        }
    }

    private sealed record VlcTrack(int Id, TrackType Type, string Codec, string? Language, string? Name);
    private static async Task<IReadOnlyList<VlcTrack>> ParseWithVlc(string path)
    {
        using var lease = BackgroundVlc.Acquire();
        if (lease is null) return [];
        using var media = new Media(lease.Engine, path, FromType.FromPath);
        var status = await media.Parse(MediaParseOptions.ParseLocal, 10000);
        await Task.Yield(); // leave VLC's event thread before touching the media
        if (status != MediaParsedStatus.Done) return [];
        return media.Tracks.Where(track => track.TrackType is TrackType.Audio or TrackType.Text && track.Id >= 0)
            .Select(track => new VlcTrack(track.Id, track.TrackType, System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(track.Codec)).Trim('\0', ' '),
                TrackLanguages.Normalize(track.Language), string.IsNullOrWhiteSpace(track.Description) ? null : track.Description.Trim())).ToList();
    }
}
