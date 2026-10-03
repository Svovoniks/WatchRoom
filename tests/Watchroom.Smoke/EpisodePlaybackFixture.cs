using Watchroom.Core;

static class EpisodePlaybackFixture
{
    public static void Run(Action<bool, string> check)
    {
        check(!EpisodePlayback.NearEnd(0, 0) && !EpisodePlayback.NearEnd(-1, 60000), "unknown duration and unavailable position never show the end action");
        check(!EpisodePlayback.NearEnd(509999, 600000) && EpisodePlayback.NearEnd(540000, 600000), "end action waits for the final ten percent of a short episode");
        check(!EpisodePlayback.NearEnd(3509999, 3600000) && EpisodePlayback.NearEnd(3510000, 3600000), "long episodes show the end action only in the final ninety seconds");
        var first = new MediaItem("first", "first.mkv", "Example", "Show", Series: "Example", Season: 1, Episode: 1, ShowId: "original");
        var second = first with { Id = "second", Path = "second.mkv", Episode = 2 };
        var nextSeason = first with { Id = "next-season", Path = "next.mkv", Season = 2 };
        var otherShow = second with { Id = "other", ShowId = "remake" };
        MediaItem[] library = [otherShow, nextSeason, second, first];
        check(EpisodePlayback.Next(library, first, false, _ => true)?.Id == second.Id, "next episode follows numbering rather than library order");
        check(EpisodePlayback.Next(library, second, false, _ => true)?.Id == nextSeason.Id, "last episode offers the first episode of the next season");
        check(EpisodePlayback.Next(library, first, true, _ => true) is null, "an existing queue suppresses the next-episode offer");
        check(EpisodePlayback.Next(library, nextSeason, false, _ => true) is null, "series finale has no next-episode offer");
        check(EpisodePlayback.Next(library, first with { Series = null }, false, _ => true) is null, "standalone videos do not offer another episode");
        check(EpisodePlayback.Next(library, first with { Episode = null }, false, _ => true) is null, "unknown episode numbering does not guess a continuation");
        check(EpisodePlayback.Next(library, first, false, item => item.Id != second.Id)?.Id == nextSeason.Id, "unplayable files are skipped across season boundaries");
        check(EpisodePlayback.Next([first, second with { IsVirtual = true }, nextSeason], first, false, _ => true)?.Id == nextSeason.Id,
            "missing episode placeholders are not offered for playback");
        check(EpisodePlayback.Next([first, second with { IsExtra = true }, nextSeason], first, false, _ => true)?.Id == nextSeason.Id,
            "extras are excluded from episode continuation");
        check(EpisodePlayback.Next(library, first with { EpisodeEnd = 2 }, false, _ => true)?.Id == nextSeason.Id,
            "multi-episode files continue after the entire completed range");
        check(EpisodePlayback.Next([first, otherShow], first, false, _ => true) is null, "same-name remakes cannot supply the next episode");

        var duplicate = second with { Id = "duplicate", Path = "duplicate.mkv" };
        MediaItem[] collection = [first, second, duplicate, nextSeason, otherShow];
        var completed = new HashSet<string> { first.Id };
        var seasonCard = new LibraryCard("Season 1", "", null, first, "season", 1);
        var showCard = new LibraryCard("Example", "", null, first, "series");
        bool Watched(LibraryCard card) => LibraryWatchProgress.IsWatched(collection, card, completed.Contains);
        check(!Watched(seasonCard) && !Watched(showCard), "partially watched seasons and shows have no completed badge");
        completed.Add(duplicate.Id);
        check(Watched(seasonCard) && !Watched(showCard), "one watched version completes its episode and season without completing the show");
        check(Watched(new("Episode 2", "", null, second, "episode", 1)), "watched episode badge carries across duplicate file versions");
        completed.Add(nextSeason.Id);
        check(Watched(showCard), "show badge appears only after every season is watched");
        completed.Remove(first.Id);
        check(!Watched(seasonCard) && !Watched(showCard), "clearing an episode's watched state clears season and show badges");
        completed.Add("double");
        collection = [first, second, first with { Id = "double", EpisodeEnd = 2 }, nextSeason, first with { Id = "extra", IsExtra = true }, first with { Id = "future", Season = 3, IsVirtual = true }];
        check(Watched(seasonCard) && Watched(showCard), "watched multi-episode files cover their range while extras and future placeholders do not block completion");
        check(!LibraryWatchProgress.IsWatched(collection, seasonCard with { Season = 99 }, completed.Contains), "empty seasons are never marked watched");
        var movie = first with { Id = "movie", Series = null };
        var movieCard = new LibraryCard("Movie", "", null, movie, "movie");
        check(!LibraryWatchProgress.IsWatched([movie], movieCard, completed.Contains), "unwatched movie has no completed badge");
        completed.Add(movie.Id);
        check(LibraryWatchProgress.IsWatched([movie], movieCard, completed.Contains), "completed movie has a watched badge");
    }
}
