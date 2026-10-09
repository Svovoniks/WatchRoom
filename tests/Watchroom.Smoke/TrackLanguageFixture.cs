using Watchroom.Core;

static class TrackLanguageFixture
{
    public static void Run(Action<bool, string> check)
    {
        var preferences = TrackLanguages.Parse("Japanese, English, Russian");
        check(preferences.SequenceEqual(["ja", "en", "ru"]), "language names preserve the user's preference order");
        check(TrackLanguages.Parse("eng, en-US, fre, fr, arm, hye").SequenceEqual(["en", "fr", "hy"]), "regional and alternate ISO codes normalize and deduplicate");
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
    }
}
