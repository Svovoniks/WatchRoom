using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Several library cards selected at once: toggling, the action bar, batch watched, metadata lock and removal targets,
// and the selection clearing when the cards leave view.
static class CardSelectionChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethods(flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(window, args);
        void Until(Func<bool> done)
        {
            for (var until = DateTime.UtcNow.AddSeconds(10); !done() && DateTime.UtcNow < until;)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
        }
        void Await(object? task) { var pending = (Task)task!; Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
        var cards = (LibraryCardCollection)typeof(MainWindow).GetField("libraryCards", flags)!.GetValue(window)!;
        var selected = (HashSet<string>)typeof(MainWindow).GetField("selectedCards", flags)!.GetValue(window)!;
        var bar = (Border)window.FindName("CardSelectionBar"); var count = (TextBlock)window.FindName("CardSelectionCount");
        var search = (TextBox)window.FindName("SearchBox");

        var file = Path.Combine(directory, "selection-check.mkv"); File.WriteAllText(file, "selection");
        var first = new MediaItem("selection-first", file, "Selection check alpha", "Movie", Year: 2001);
        var second = first with { Id = "selection-second", Path = file + ".second", Title = "Selection check beta", Year = 2002 };
        var gone = first with { Id = "selection-gone", Path = file + ".gone", Title = "Selection check gone", Year = 2003, Available = false };
        foreach (var item in new[] { first, second, gone }) store.Save(item);
        var wanted = WantedTitles.Add(store, directory, WantedTitles.FromMatch(new(9301, "movie", "Selection check wanted", "2004", null, "", "Movie"), "Movie", [], false, null), []);
        Await(Call("RefreshLibrary"));

        Call("NavigateLibrary", 1, null, null, null);
        search.Text = "Selection check";
        Until(() => cards.Count == 3 && cards.All(c => c.DisplayTitle.StartsWith("Selection check")));
        check(cards.Count == 3 && bar.Visibility == Visibility.Collapsed, "nothing is selected and the selection bar is hidden at first");
        LibraryCardView Card(string id) => cards.Single(c => c.Card.Media.Id == id);

        Call("ToggleCardSelection", Card(first.Id));
        check(Card(first.Id).IsSelected && bar.Visibility == Visibility.Visible && count.Text == "1 selected", "selecting a card marks it and shows the selection bar");
        // While cards are selected, a plain click on another card adds it instead of opening it.
        Call("LibraryCardClick", new Button { DataContext = Card(second.Id) }, new RoutedEventArgs());
        check(Card(second.Id).IsSelected && count.Text == "2 selected" && ((Grid)window.FindName("LibraryPage")).Visibility == Visibility.Visible,
            "clicking a card while selecting adds it to the selection and stays in the library");
        Call("LibraryCardClick", new Button { DataContext = Card(second.Id) }, new RoutedEventArgs());
        check(!Card(second.Id).IsSelected && count.Text == "1 selected", "clicking a selected card deselects it");

        Call("SelectAllCards");
        check(cards.All(c => c.IsSelected) && count.Text == "3 selected", "Select all selects every card in view");
        var (titles, unavailable) = ((WantedTitle[], MediaItem[]))Call("SelectionRemoval", (object)cards.ToArray())!;
        check(titles.Select(x => x.Id).SequenceEqual([wanted.Id]) && unavailable.Length == 0,
            "removal takes the title added without files, never videos still on disk");
        check(((Button)window.FindName("SelectionRemove")).IsEnabled, "Remove from library is offered for a selection with removable titles");
        var headers = ((IEnumerable<object>)Call("SelectionMenuItems", true)!).OfType<MenuItem>().Select(x => x.Header as string).ToArray();
        check(headers.Contains("Play all") && headers.Contains("Add to queue") && headers.Contains("Mark as watched") && headers.Contains("Lock metadata") && headers.Contains("Clear selection"),
            "a selected card's menu acts on the whole selection");

        Call("MarkSelectionWatched", true);
        var placeholder = cards.Single(c => c.HasNoFiles).Card.Media.Id;
        check(new[] { first, second }.All(x => store.Setting("watched:" + x.Id) == "true") && store.Setting("watched:" + placeholder) is null,
            "Mark as watched marks every selected title with files, and skips titles without files");
        check(Card(first.Id).IsWatched && Card(second.Id).IsWatched && cards.All(c => c.IsSelected), "batch watched updates the cards and keeps the selection");
        Call("MarkSelectionWatched", false);
        check(new[] { first, second }.All(x => store.Setting("watched:" + x.Id) == "false"), "Mark as unwatched clears every selected title");

        Call("LockSelectionMetadata", true);
        check(store.Get(first.Id)!.MetadataLocked && store.Get(second.Id)!.MetadataLocked, "Lock metadata locks every selected title");
        Until(() => cards.Count == 3);
        Call("LockSelectionMetadata", false);
        check(!store.Get(first.Id)!.MetadataLocked, "Unlock metadata unlocks the selected titles");
        Until(() => cards.Count == 3);

        // Cards leaving view leave the selection, so actions never reach titles the user can't see.
        Call("SelectAllCards");
        search.Text = "Selection check alpha";
        Until(() => cards.Count == 1);
        check(selected.SetEquals([Card(first.Id).Key]) && count.Text == "1 selected", "narrowing the search drops cards that left view from the selection");
        Call("NavigateLibrary", 4, null, null, null);
        check(selected.Count == 0 && bar.Visibility == Visibility.Collapsed, "changing library location clears the selection");
        // Under Unavailable, the entries of videos that are gone can be removed together.
        search.Text = "Selection check";
        Until(() => cards.Count == 1 && cards[0].Card.Media.Id == gone.Id);
        Call("SelectAllCards");
        (titles, unavailable) = ((WantedTitle[], MediaItem[]))Call("SelectionRemoval", (object)cards.ToArray())!;
        check(titles.Length == 0 && unavailable.Select(x => x.Id).SequenceEqual([gone.Id]), "removal takes the entries of selected videos that are gone");
        Call("ShowPage", "Home");
        check(selected.Count == 0 && bar.Visibility == Visibility.Collapsed, "leaving the library clears the selection");

        search.Clear();
        WantedTitles.Remove(store, directory, wanted);
        foreach (var item in new[] { first, second }) store.Save(item with { Available = false });
        store.RemoveUnavailable([first.Id, second.Id, gone.Id]);
        Await(Call("RefreshLibrary"));
        Call("ShowPage", "Library");
    }
}
