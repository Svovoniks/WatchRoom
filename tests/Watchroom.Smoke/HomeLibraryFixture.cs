using Watchroom.Core;

static class HomeLibraryFixture
{
    public static void Run(Action<bool, string> check)
    {
        MediaItem Episode(string id, string show, int season, int number, string kind = "Show") =>
            new(id, "C:/private/" + id + ".mkv", show, kind, Series: show, Season: season, Episode: number, RuntimeMinutes: 24);
        MediaItem[] items = [
            Episode("a1", "Started show", 1, 1), Episode("a2", "Started show", 1, 2), Episode("a3", "Started show", 2, 1),
            Episode("done", "Finished show", 1, 1), Episode("anime", "New anime", 1, 1, "Anime"),
            Episode("new", "New show", 1, 1),
            new("film", "C:/private/film.mkv", "Paused movie", "Movie", RuntimeMinutes: 120),
            new("fresh", "C:/private/fresh.mkv", "New movie", "Movie"),
            new("missing", "C:/private/missing.mkv", "Unavailable", "Movie", Available: false),
            Episode("virtual", "Upcoming", 1, 1) with { IsVirtual = true },
            Episode("extra", "Bonus", 1, 1) with { IsExtra = true }];
        var playback = new Dictionary<string, string> { ["watched:a1"] = "true", ["watched:done"] = "true", ["position:film"] = "60000" };
        var home = LibraryHome.Browse(items, playback, 7);
        var continuing = home.Where(entry => entry.Section == "continue").ToArray();
        check(continuing.Length == 2 && continuing.Single(entry => entry.Card.Media.Series is not null).Card.Media.Id == "a2",
            "Home continues a started show at its next unwatched episode across seasons");
        check(continuing.Single(entry => entry.Card.Media.Id == "film").PositionMs == 60000, "Home preserves a paused movie's resume position");
        check(!home.Any(entry => entry.Section != "mystery" && entry.Card.Media.Id == "done"), "completed shows leave continue watching and suggestions");
        check(home.Where(entry => entry.Section == "suggestions").Select(entry => entry.Card.Media.Kind).ToHashSet().SetEquals(["Movie", "Show", "Anime"]),
            "suggestions mix unstarted movies, shows, and anime");
        check(home.All(entry => entry.Card.Media.Available && !entry.Card.Media.IsExtra && !entry.Card.Media.IsVirtual), "all Home sections exclude unavailable files, extras, and virtual episodes");
        playback["position:a3"] = "45000";
        check(LibraryHome.Browse(items, playback, 7).Single(entry => entry.Section == "continue" && entry.Card.Media.Series is not null).Card.Media.Id == "a3",
            "a partially watched later episode takes precedence over an earlier unwatched episode");
        var alternate = items.Append(items[0] with { Id = "alternate", Path = "C:/private/alternate.mkv" }).ToArray();
        check(LibraryHome.Browse(alternate, playback, 7).All(entry => entry.Section != "continue" || entry.Card.Media.Id != "alternate"),
            "watched state covers alternate files for the same logical episode");
        check(LibraryHome.Browse([], playback).Count == 0, "an empty library has no mystery pick");
        var mysteryIds = Enumerable.Range(0, 100).Select(seed => LibraryHome.Browse(items, playback, seed).Single(entry => entry.Section == "mystery").Card.Media.Id).ToHashSet();
        check(mysteryIds.Contains("anime") && mysteryIds.Contains("fresh") && mysteryIds.Contains("a1"), "mystery can pick movies, shows, and anime with one chance per title");
        var request = new LibraryBrowseRequest(Home: true, Seed: 7);
        var first = SharedLibrary.Browse(items, request, playback: playback);
        check(first.Location == "Home" && first.Entries.Any(entry => entry.HomeSection == "continue" && entry.PositionMs == 60000), "guest Home shares host progress and section labels");
        check(!Wire.Serialize(first).Contains("private") && !Wire.Serialize(first).Contains("position:"), "guest Home keeps host paths and private settings out of the wire response");
        var many = Enumerable.Range(0, 16).Select(index => new MediaItem("film" + index, "", "Film " + index, "Movie")).ToArray();
        var beforeMetadata = LibraryHome.Browse(many, seed: 7);
        var afterMetadata = LibraryHome.Browse(many.Reverse().Select((item, index) => item with { Title = "Corrected title " + index, Overview = "New overview" }), seed: 7);
        check(beforeMetadata.Select(entry => (entry.Section, entry.Card.Media.Id)).SequenceEqual(afterMetadata.Select(entry => (entry.Section, entry.Card.Media.Id))),
            "background metadata and source-order changes preserve suggestion order and the mystery pick");
        var pages = new[] { SharedLibrary.Browse(many, request), SharedLibrary.Browse(many, request with { Page = 1 }) };
        check(pages.Sum(page => page.Entries.Length) == 9 && pages.SelectMany(page => page.Entries).Count(entry => entry.HomeSection == "mystery") == 1,
            "guest Home pagination includes the full suggestions row and one mystery pick");
        check(pages.SelectMany(page => page.Entries).Where(entry => entry.HomeSection == "suggestions").Select(entry => entry.Id).Distinct().Count() == 8,
            "seeded guest suggestions remain stable across pages");
        var unicode = many.Select(item => item with { Title = new string('界', 180), Overview = new string('界', 1000) }).ToArray();
        var illustrated = SharedLibrary.Browse(unicode, request, _ => new string('+', 5400));
        check(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(illustrated)))) < 65536,
            "guest Home stays within the peer transport budget with Unicode and escaped posters");
    }
}
