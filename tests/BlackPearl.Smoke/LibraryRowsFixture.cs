using BlackPearl.Core;

// Reading the catalog again gives back the records of rows that have not changed, and never a stale one.
static class LibraryRowsFixture
{
    public static void Run(string directory, Action<bool, string> check)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var store = new LibraryStore(directory);
        var plot = "A plot every episode repeats, with \"quotes\", кириллица and 🎬.";
        MediaItem Episode(int number) => new("episode-" + number, Path.Combine(directory, "Rows Show", "Season 1", $"Rows Show - S01E{number:00}.mkv"), "Rows Show", "Show",
            Series: "Rows Show", Season: 1, Episode: number, Overview: plot, Genres: ["Drama", "Mystery"], TmdbRetentionVersion: 1);
        store.Save(Episode(1)); store.Save(Episode(2));
        store.Save(new MediaItem("film", Path.Combine(directory, "Rows Film (2001).mkv"), "Rows Film", "Movie", 2001, Overview: "A film.", TmdbRetentionVersion: 1));

        var first = store.All(); var second = store.All();
        check(first.Count == 3 && first.Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second)) && !ReferenceEquals(first, second),
            "an unchanged catalog is read again as the same records in a new list");
        check(first.Single(x => x.Id == "episode-1").Overview == plot && first.Single(x => x.Id == "film").Year == 2001, "text with quotes, Cyrillic and emoji survives the read");

        var reopened = new LibraryStore(directory).All();
        check(reopened.Count == 3 && ReferenceEquals(reopened.Single(x => x.Id == "episode-1").Overview, reopened.Single(x => x.Id == "episode-2").Overview) &&
            ReferenceEquals(reopened.Single(x => x.Id == "episode-1").Genres![0], reopened.Single(x => x.Id == "episode-2").Genres![0]),
            "episodes that repeat their show's text share one copy of it");
        check(reopened.Zip(first).All(pair => pair.First.Id == pair.Second.Id && pair.First.Overview == pair.Second.Overview && pair.First.Series == pair.Second.Series),
            "a store opened on the same library reads the same catalog");

        store.Save(first.Single(x => x.Id == "film") with { Overview = "A better description." });
        var edited = store.All();
        check(edited.Single(x => x.Id == "film").Overview == "A better description." && !ReferenceEquals(edited.Single(x => x.Id == "film"), first.Single(x => x.Id == "film")),
            "a saved change is read back");
        check(ReferenceEquals(edited.Single(x => x.Id == "episode-2"), first.Single(x => x.Id == "episode-2")), "records of rows the change did not touch are kept");

        // The show's own row changes here; the second episode's row does not.
        store.Save(edited.Single(x => x.Id == "episode-1") with { Overview = "The show's new plot." });
        var shared = store.All();
        check(shared.Single(x => x.Id == "episode-2").Overview == "The show's new plot.", "an episode whose row is unchanged still follows a change to its show");

        // Another process, such as the hourly episode check, writes through its own store.
        new LibraryStore(directory).Save(new MediaItem("second-film", Path.Combine(directory, "Second Film (2002).mkv"), "Second Film", "Movie", 2002, TmdbRetentionVersion: 1));
        check(store.All().Any(x => x.Id == "second-film"), "a row written through another store is read");
        check(store.RemoveUnavailable(["missing"]) == 0 && store.All().Count == 4, "reading again after a write that changed nothing keeps every record");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "library.db") }.ToString()))
        {
            db.Open(); using var delete = db.CreateCommand(); delete.CommandText = "DELETE FROM media WHERE id='second-film'"; delete.ExecuteNonQuery();
        }
        check(store.All().All(x => x.Id != "second-film") && store.All().Count == 3, "a deleted row leaves the catalog");

        var tracked = TmdbRetention.Record(Episode(3), Episode(3) with { Overview = "Downloaded plot." }, "tmdb", "overview");
        var json = Wire.Serialize(tracked); var fetched = tracked.TmdbFields!["overview"].FetchedAt;
        var kept = TmdbRetention.Expire(json, Wire.Now, out var changed, out var due);
        check(!changed && kept == json && due == fetched + (long)TmdbRetention.MaximumAge.TotalMilliseconds, "a row with nothing due says when its oldest download comes due");
        TmdbRetention.Expire(json, due - 1, out var early, out _); TmdbRetention.Expire(json, due, out var late, out _);
        check(!early && late, "the row is unchanged until that moment and expires at it");
        TmdbRetention.Expire(Wire.Serialize(Episode(3)), Wire.Now, out changed, out due);
        check(!changed && due == long.MaxValue, "a row with nothing downloaded never comes due");
    }
}
