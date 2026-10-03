using Watchroom.Core;

static class RoomLibraryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var host = new RoomLibrary(true, "host"); var guest = new RoomLibrary(false, "guest");
        var traffic = new Queue<Action>(); var results = new List<LibraryResult>();
        host.Send += (_, message) => traffic.Enqueue(() => guest.Receive("host", message));
        guest.Send += (_, message) => traffic.Enqueue(() => host.Receive("guest", message));
        guest.Result += results.Add;
        void Pump() { var count = 0; while (traffic.TryDequeue(out var action)) { if (++count > 1000) throw new Exception("Unbounded catalog exchange"); action(); } }
        host.SetPeople([new("host", "Host", true, true), new("guest", "Anna", false, true), new("pending", "Pending", false, false)]);
        host.Publish(Enumerable.Range(0, 57).Select(i => new SharedLibraryItem("item-" + i, "Movie " + i, "Movie", 2026, null, null, null, null, true)));
        host.PeerReady("guest"); Pump();
        check(guest.Catalog.Count == 0 && !guest.OwnAccess.Browse, "room catalog requires explicit host access");
        host.Receive("pending", new("library-command", Data: Wire.Serialize(new LibraryCommand("pending", "add", host.Queue.Revision, "item-0"))));
        check(host.Queue.Entries.Length == 0, "unadmitted peer cannot mutate direct queue");
        host.SetAccess("guest", new(true, true, true)); Pump();
        check(guest.Catalog.Count == 57 && guest.Catalog.Keys.SequenceEqual(host.Catalog.Keys), "paged peer catalog contains every shared item in order");
        check(!Wire.Serialize(guest.Catalog).Contains("path", StringComparison.OrdinalIgnoreCase), "shared catalog does not expose host paths");
        var add = new LibraryCommand("add-once", "add", host.Queue.Revision, "item-0");
        host.Receive("guest", new("library-command", Data: Wire.Serialize(add))); Pump();
        host.Receive("guest", new("library-command", Data: Wire.Serialize(add))); Pump();
        check(host.Queue.Entries.Length == 1 && guest.Queue.Entries.Length == 1 && host.Queue.Entries[0].AddedBy == "Anna", "peer queue is authoritative, attributable and retry-safe");
        host.Receive("guest", new("library-command", Data: Wire.Serialize(add with { RequestId = "stale" }))); Pump();
        check(!results.Last().Success && host.Queue.Entries.Length == 1, "stale queue edits rejected with latest state");
        guest.Request("host", "add", "item-0"); Pump();
        check(host.Queue.Entries.Select(e => e.Id).Distinct().Count() == 2, "duplicate titles have independent queue entry IDs");
        guest.Request("host", "move", entry: guest.Queue.Entries[1].Id, index: 0); Pump();
        check(host.Queue.Entries[0].Id == guest.Queue.Entries[0].Id, "guest reorder is broadcast over peer connection");
        var beforeRemove = host.Queue.Entries;
        guest.Request("host", "remove", entry: guest.Queue.Entries[0].Id); Pump();
        host.Request("host", "undo"); Pump();
        check(host.Queue.Entries.SequenceEqual(beforeRemove), "host can undo a guest queue edit");
        var view = new GuestLibraryView(1, true, "Movie", "All titles", 8, 4, 0,
            host.Catalog.Keys.Take(32).ToArray(), "item-3", "item-20");
        guest.SendView("host", view); Pump();
        check(host.Views["guest"].VisibleIds.SequenceEqual(view.VisibleIds) && host.Views["guest"].Columns == 8 && host.Views["guest"].Rows == 4 && host.Views["guest"].HoveredId == "item-20", "host receives full negotiated grid and hovered item");
        guest.SendView("host", view with { Sequence = 0, HoveredId = "item-1" }); Pump();
        check(host.Views["guest"].HoveredId == "item-20", "out-of-order browsing updates cannot regress hover");
        guest.SendView("host", view with { Sequence = 2, VisibleIds = ["hidden"] }); Pump();
        check(host.Views["guest"].Sequence == 1, "unshared items rejected from guest preview");
        host.ExpireViews(Wire.Now + 9000);
        check(!host.Views.ContainsKey("guest"), "disconnected browsing presence expires");
        LibraryStart? requested = null; host.StartRequested += request => requested = request;
        guest.Request("host", "next"); Pump();
        check(requested is not null && host.CanCommit(requested), "guest can request a queued video directly from host");
        var first = requested;
        guest.Request("host", "play", "item-1"); Pump();
        check(!results.Last().Success && requested == first, "competing video starts are rejected while preparation is pending");
        host.SetAccess("guest", new(true, true, false)); Pump();
        check(!host.CanCommit(requested!), "permission revocation invalidates pending video start");
        host.CompleteStart(requested!, true); Pump();
        check(!results.Last().Success && host.Queue.Entries.Length == 2, "failed start preserves queue");
        host.SetAccess("guest", new(true, true, true)); Pump();
        guest.Request("host", "next"); Pump(); host.CompleteStart(requested!, true); Pump();
        check(results.Last().Success && guest.Queue.Entries.Length == 1, "successful queued start consumes exactly one entry");
        guest.Request("host", "play", "hidden"); Pump();
        check(!results.Last().Success, "guest cannot start an unpublished video");
        host.SetAccess("guest", new()); Pump();
        check(guest.Catalog.Count == 0 && !host.Views.ContainsKey("guest"), "revocation clears guest catalog and host preview");
        guest.Receive("host", new("library-item", Data: Wire.Serialize(new LibraryCatalogItem(guest.Generation - 1, host.Catalog.Values.First()))));
        check(guest.Catalog.Count == 0, "late bulk catalog data cannot repopulate a revoked library");
        guest.Request("host", "clear"); Pump();
        check(!results.Last().Success && host.Queue.Entries.Length == 1, "revoked guest cannot clear queue");
        host.Publish([]); Pump();
        check(host.Queue.Entries.Length == 0, "unsharing collection removes actionable queue entries");
    }
}
