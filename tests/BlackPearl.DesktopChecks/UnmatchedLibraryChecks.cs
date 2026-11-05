using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class UnmatchedLibraryChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        void Until(Func<bool> done)
        {
            for (var until = DateTime.UtcNow.AddSeconds(10); !done() && DateTime.UtcNow < until;)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
        }
        void Refresh() { var pending = (Task)Call("RefreshLibrary")!; Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
        var cards = (LibraryCardCollection)typeof(MainWindow).GetField("libraryCards", flags)!.GetValue(window)!;
        var nav = (Button)window.FindName("UnmatchedLibraryNav"); var count = (TextBlock)window.FindName("UnmatchedLibraryCount");

        // The fixtures share a library, so count what these entries add.
        Refresh();
        var before = int.Parse(count.Text);
        var lost = new MediaItem("unmatched-episode", Path.Combine(directory, "unmatched-episode.mkv"), "Unmatched Check Show", "Show", Series: "Unmatched Check Show", Season: 1, Episode: 1);
        var film = new MediaItem("unmatched-film", Path.Combine(directory, "unmatched-film.mkv"), "Unmatched Check Film", "Movie");
        var found = new MediaItem("matched-film", Path.Combine(directory, "matched-film.mkv"), "Matched Check Film", "Movie", MetadataProvider: "tmdb", MetadataId: 91234);
        foreach (var item in new[] { lost, film, found }) store.Save(item);
        Refresh();
        check(nav.Visibility == Visibility.Visible && int.Parse(count.Text) == before + 2, "the sidebar lists Unmatched with its count of titles");
        check(System.Windows.Automation.AutomationProperties.GetName(nav) == $"Unmatched, {before + 2} titles", "screen readers hear how many titles are unmatched");

        Call("NavigateLibrary", LibraryCatalog.Unmatched, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == film.Id) && cards.All(c => c.Card.Media.Id != found.Id));
        check(cards.Any(c => c.Card.Media.Id == film.Id) && cards.Any(c => LibraryIdentity.SameShow(c.Card.Media, store.Get(lost.Id)!)) && cards.All(c => c.Card.Media.Id != found.Id),
            "Unmatched lists the shows and films nothing identified, and no others");
        check(cards.Count == before + 2, "Unmatched shows one card per title");        check(System.Windows.Automation.AutomationProperties.GetItemStatus(nav) == "Selected", "the sidebar marks Unmatched as the current category");

        var search = (TextBox)window.FindName("SearchBox"); search.Text = "Unmatched Check Film";
        Until(() => cards.Count == 1);
        check(cards.Count == 1 && cards[0].Card.Media.Id == film.Id, "search works within Unmatched");
        search.Clear();
        Until(() => cards.Count == before + 2);

        // They aren't in the categories they'll move to once they're matched.
        Call("NavigateLibrary", 1, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == found.Id));
        check(cards.Any(c => c.Card.Media.Id == found.Id) && cards.All(c => c.Card.Media.Id != film.Id), "an unmatched film isn't in Movies");
        Call("NavigateLibrary", 2, null, null, null);
        Until(() => cards.All(c => c.Card.Media.Id != found.Id));
        check(cards.All(c => !LibraryIdentity.SameShow(c.Card.Media, store.Get(lost.Id)!)), "an unmatched show isn't in Shows");
        Call("NavigateLibrary", 0, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == found.Id));
        check(cards.Any(c => c.Card.Media.Id == film.Id) && cards.Any(c => LibraryIdentity.SameShow(c.Card.Media, store.Get(lost.Id)!)), "All still lists unmatched titles");

        // A title's page belongs to Unmatched too, and its show can still be browsed.
        Call("NavigateLibrary", LibraryCatalog.Unmatched, LibraryIdentity.ShowKey(store.Get(lost.Id)!), "Show", null);
        Until(() => cards.Any(c => c.Card.Media.Id == lost.Id || LibraryIdentity.SameShow(c.Card.Media, store.Get(lost.Id)!)));
        check(cards.Any(c => LibraryIdentity.SameShow(c.Card.Media, store.Get(lost.Id)!)), "an unmatched show opens from Unmatched");
        check((int)Call("CategoryFor", store.Get(film.Id)!)! == LibraryCatalog.Unmatched && (int)Call("CategoryFor", store.Get(found.Id)!)! == 1, "an unmatched title's page sits under Unmatched");

        // Identifying a title takes it off the list and back to its usual category.
        Call("NavigateLibrary", LibraryCatalog.Unmatched, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == film.Id));
        store.Save(store.Get(film.Id)! with { MetadataProvider = "tmdb", MetadataId = 91235 });
        Refresh();
        Until(() => cards.All(c => c.Card.Media.Id != film.Id));
        check(cards.All(c => c.Card.Media.Id != film.Id) && int.Parse(count.Text) == before + 1, "a title leaves Unmatched once it's identified");
        Call("NavigateLibrary", 1, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == film.Id));
        check(cards.Any(c => c.Card.Media.Id == film.Id), "an identified film returns to Movies");

        Call("NavigateLibrary", 0, null, null, null);
        foreach (var item in new[] { lost, film, found }) store.Save(store.Get(item.Id)! with { Available = false });
        store.RemoveUnavailable([lost.Id, film.Id, found.Id]);
        Refresh();
        check(int.Parse(count.Text) == before, "removing the entries restores the count");
        Call("ShowPage", "Library");
    }
}
