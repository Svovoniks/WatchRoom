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
    }
}
