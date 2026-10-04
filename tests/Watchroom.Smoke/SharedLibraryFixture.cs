using Watchroom.Core;

static class SharedLibraryFixture
{
    public static void Run(Action<bool, string> check)
    {
        var room = new HostRoomCoordinator("host");
        Participant[] members = [new("host", "Host", true, true), new("a", "Alice", false, true), new("b", "Bob", false, true), new("pending", "Pending", false, false)];
        room.Discover(members, 1000);
        check(!room.Apply("a", new("catalog-browse"), 1000, out _), "library browsing is disabled by default");
        check(!room.Apply("a", new("catalog-permission", Number: 1, Sender: "host"), 1000, out _), "guest cannot spoof library permission");
        room.Apply("host", new("catalog-permission", Number: 1), 1000, out _);
        check(!room.Apply("pending", new("catalog-browse"), 1000, out _), "unapproved guest cannot acquire library");
        check(room.Apply("a", new("catalog-browse"), 1000, out _) && !room.Apply("b", new("catalog-browse"), 1000, out _), "exactly one guest acquires browsing lock");
        foreach (var command in new[] { "catalog-follow", "catalog-artwork", "catalog-watch" })
            check(room.Apply("a", new(command), 1000, out _) && !room.Apply("b", new(command), 1000, out _), "only the browsing lock owner can " + command);
        var view = new LibraryBrowseView(new(), 8, .5, "hover", "selected");
        room.DescribeLibraryView(view, 1000); room.Apply("a", new("catalog-browse"), 1000, out _);
        check(room.LibraryActivity?.View == view, "loading another chunk preserves the authenticated guest view");
        room.DescribeLibrary("Example · Season 1", "pilot", 1001);
        check(room.Snapshot.LibraryActivity is { Name: "Alice", Query: "pilot", Location: "Example · Season 1" }, "host sees authenticated guest activity");
        check(!room.Apply("b", new("catalog-release"), 1002, out _) && !room.Apply("b", new("catalog-view"), 1002, out _), "other guest cannot release lock or report activity");
        room.Apply("a", new("catalog-renew"), 40000, out _);
        check(!room.ExpireLibrary(46001), "active browsing lease is renewed");
        check(room.ExpireLibrary(85000) && room.Apply("b", new("catalog-browse"), 85001, out _), "expired lease becomes available to another guest");
        room.Apply("host", new("catalog-release"), 86000, out _);
        check(room.Snapshot.LibraryActivity is null, "host can end the current browsing session");
        room.Apply("a", new("catalog-browse"), 87000, out _);
        room.Apply("host", new("catalog-permission", Number: 0), 87001, out _);
        check(room.Snapshot.LibraryActivity is null && !room.Apply("a", new("catalog-browse"), 87002, out _), "revocation clears lock and denies further browsing");
        room.Apply("host", new("catalog-permission", Number: 1), 88000, out _);
        room.Apply("a", new("catalog-browse"), 88001, out _); room.Depart("a", 88002);
        check(room.Apply("b", new("catalog-browse"), 88003, out _), "disconnect releases browsing lock");
        room.Discover(members.Where(x => x.Id != "b").ToArray(), 89000);
        check(room.Snapshot.LibraryActivity is null, "discovery removal releases browsing lock");

        var items = Enumerable.Range(0, 20).Select(i => new MediaItem("m" + i, "C:/private/secret-" + i, "Movie " + i.ToString("00"), "Movie", Overview: "Summary")).ToList();
        items.Add(new("ep", "C:/private/episode", "Example", "Show", Series: "Example", Season: 1, Episode: 1, EpisodeTitle: "Pilot"));
        items.Add(new("missing", "C:/private/missing", "Missing", "Movie", Available: false));
        var first = SharedLibrary.Browse(items, new()); var second = SharedLibrary.Browse(items, new(Page: 1));
        check(first.Total == 21 && first.Entries.Length == SharedLibrary.PageSize && !first.Entries.Select(x => x.Id).Intersect(second.Entries.Select(x => x.Id)).Any(), "public library paginates without duplicates and excludes missing files");
        check(!Wire.Serialize(first).Contains("private") && SharedLibrary.Browse(items, new(Query: "secret")).Total == 0, "library responses and searches do not disclose host paths");
        var seasons = SharedLibrary.Browse(items, new(ParentId: "ep"));
        var episodes = SharedLibrary.Browse(items, new(ParentId: "ep", Season: 1));
        check(seasons.Entries.Single().Level == "season" && episodes.Entries.Single().Title.Contains("Pilot"), "guest can navigate series to seasons and episodes");
        check(SharedLibrary.Browse(items, new(Category: 2)).Entries.Single().Id == "ep", "guest category filtering returns shows");
        var unicode = Enumerable.Range(0, 8).Select(i => new MediaItem("u" + i, "", new string('界', 180), "Movie", Overview: new string('界', 1000)));
        var message = new WireMessage("catalog-page", Data: Wire.Serialize(SharedLibrary.Browse(unicode, new())));
        check(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(message)) < 65536, "Unicode catalog page stays within peer transport limit");
        var thumbnail = Convert.ToBase64String(new byte[4000]);
        var illustrated = SharedLibrary.Browse(unicode, new(), _ => thumbnail);
        check(illustrated.Entries.All(entry => entry.Thumbnail == thumbnail), "all cards retain bounded thumbnail bytes");
        check(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(illustrated)))) < 65536,
            "Unicode titles and posters fit the nested peer transport envelope");
        check(SharedLibrary.Browse(items, new(), _ => new string('A', 5401)).Entries.All(entry => entry.Thumbnail is null),
            "oversized thumbnail payloads are excluded");
        var escaped = SharedLibrary.Browse(unicode, new(), _ => new string('+', 5400));
        check(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(new WireMessage("catalog-page", Number: long.MaxValue, Data: Wire.Serialize(escaped)))) < 65536,
            "heavily escaped thumbnail bytes cannot exceed the control channel limit");
    }
}
