using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Titles added before their files are here: the toolbar button, the Missing category, dimmed cards, the wanted panel
// on a show and a film, the search offer and the Add title dialog.
static class WantedLibraryChecks
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
        IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        IEnumerable<T> Logical<T>(object root) where T : DependencyObject
        {
            if (root is not DependencyObject node) yield break;
            if (node is T match) yield return match;
            foreach (var child in LogicalTreeHelper.GetChildren(node)) foreach (var nested in Logical<T>(child)) yield return nested;
        }
        var cards = (LibraryCardCollection)typeof(MainWindow).GetField("libraryCards", flags)!.GetValue(window)!;
        string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");
        MetadataSeason Season(int number, params int[] offsets) => new(number, null, null, null, null, offsets.Select((o, i) => new MetadataEpisode(i + 1, $"Chapter {i + 1}", null, null, Day(o), 24)).ToArray());

        var show = WantedTitles.Add(store, directory, WantedTitles.FromMatch(new(9101, "tv", "Wanted Check Show", "2021", null, "", "Show"), "Show", [1, 2], true, null),
            [Season(0, -300), Season(1, -200, -193, -186), Season(2, 40)]);
        var film = WantedTitles.Add(store, directory, WantedTitles.FromMatch(new(9102, "movie", "Wanted Check Film", "2027", null, "", "Movie", ReleaseDate: Day(60)), "Movie", [], false, null), []);
        Await(Call("RefreshLibrary"));

        var add = (Button)window.FindName("AddTitleButton");
        check(add.Visibility == Visibility.Visible && Grid.GetColumn(add) == 3 && Grid.GetColumn((Button)window.FindName("RescanButton")) == 2 && (string)add.Content == "Add title",
            "Add title sits at the end of the library toolbar after Rescan");
        var missingNav = (Button)window.FindName("MissingLibraryNav");
        check(missingNav.Visibility == Visibility.Visible && int.Parse(((TextBlock)window.FindName("MissingLibraryCount")).Text) >= 2, "the sidebar lists Missing with a count");
        Call("NavigateLibrary", 5, null, null, null);
        Until(() => cards.Any(c => c.Card.Media.Id == film.Id));
        var showCard = cards.FirstOrDefault(c => LibraryIdentity.ShowKey(c.Card.Media) == show.Id); var filmCard = cards.FirstOrDefault(c => c.Card.Media.Id == film.Id);
        check(showCard is { HasNoFiles: true, EpisodeStatus: "Missing", DimArtwork: false } && filmCard is { HasNoFiles: true, EpisodeStatus: "Upcoming", IsUpcoming: true },
            "wanted titles appear under Missing, dimmed with a dashed edge and a Missing or Upcoming badge");
        check(cards.All(c => c.Card.Status is not null), "the Missing category lists only titles with something missing or upcoming");

        Call("NavigateLibrary", 2, show.Id, "Show", null);
        var panel = (ContentControl)window.FindName("WantedLibraryPanel");
        Until(() => panel.Visibility == Visibility.Visible);
        var texts = Logical<TextBlock>(panel.Content!).Select(x => x.Text).ToArray();
        var buttons = Logical<Button>(panel.Content!).Select(x => x.Content as string).OfType<string>().ToArray();
        check(panel.Visibility == Visibility.Visible && texts.Any(x => x.StartsWith("0 of 4 tracked episodes on disk · 1 upcoming")) && texts.Any(x => x.StartsWith("Next up · S1E1")),
            "a wanted show's page shows what is on disk and what is next");
        check(buttons.Contains("Link a folder…") && buttons.Contains("Rescan now") && buttons.Contains("Edit seasons") && buttons.Contains("Remove from library") && buttons.Any(x => x.StartsWith("Track Specials")),
            "a wanted show can link a folder, rescan, edit or track seasons and be removed");
        store.SaveWanted(store.Wanted(show.Id)! with { Candidate = new("elsewhere", Path.Combine(directory, "Wanted Check Show"), "The files have no year to confirm it") });
        Await(Call("RefreshLibrary"));
        Until(() => panel.Content is not null && Logical<TextBlock>(panel.Content).Any(x => x.Text.StartsWith("Found possible files")));
        check(Logical<Button>(panel.Content!).Any(x => x.Content as string == "Not this") && Logical<TextBlock>(panel.Content!).Any(x => x.Text.Contains("Nothing was linked automatically")),
            "possible files wait for the user to link them or turn them down");

        Call("Select", store.Get(film.Id)!);
        var detail = (ContentControl)window.FindName("WantedDetailPanel");
        check(detail.Visibility == Visibility.Visible && ((Button)window.FindName("DetailPlay")).Visibility == Visibility.Collapsed && Logical<TextBlock>(detail.Content!).Any(x => x.Text.StartsWith("Comes out ")),
            "a wanted film's page explains when it comes out and offers its actions");

        // A search that finds nothing offers to add the title.
        Call("NavigateLibrary", 0, null, null, null);
        var search = (TextBox)window.FindName("SearchBox"); search.Text = "zz no such title";
        var find = (Button)window.FindName("FindTitleButton");
        Until(() => find.Visibility == Visibility.Visible);
        check(find.Visibility == Visibility.Visible && ((string)find.Content).Contains("“zz no such title”") && ((string)find.Content).EndsWith("and add it"), "an empty search offers to find the title and add it");
        search.Clear();
        ((ComboBox)window.FindName("LibraryStatusFilter")).SelectedIndex = 6;
        Until(() => cards.Count > 0 && cards.All(c => c.Card.Media.Id == film.Id || LibraryIdentity.ShowKey(c.Card.Media) == show.Id));
        check(cards.Any(c => c.Card.Media.Id == film.Id) && cards.All(c => c.Card.Media.Id == film.Id || LibraryIdentity.ShowKey(c.Card.Media) == show.Id), "the Upcoming filter lists titles with upcoming episodes or releases");
        ((ComboBox)window.FindName("LibraryStatusFilter")).SelectedIndex = 0;

        // The dialog opens with the search pre-filled; closing it adds nothing.
        // The dialog waits in its own message loop, so look at it and close it from a callback that loop runs.
        string? dialogTitle = null; string[] kinds = [];
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            var dialog = InAppDialog.Open.LastOrDefault();
            dialogTitle = dialog?.Title; kinds = dialog?.Content is { } content ? Logical<Button>(content).Select(x => x.Content as string).OfType<string>().Take(4).ToArray() : [];
            foreach (var open in InAppDialog.Open.ToArray()) open.Close();
        });
        var before = store.Wanted().Count;
        Call("AddTitleClick", window, new RoutedEventArgs());
        Until(() => InAppDialog.Open.Count == 0);
        check(dialogTitle == "Add a title" && kinds.SequenceEqual(["Any", "Movie", "Show", "Anime"]), "Add title opens a search with Any, Movie, Show and Anime");
        check(store.Wanted().Count == before, "closing the dialog adds nothing");

        foreach (var title in new[] { show, film }) WantedTitles.Remove(store, directory, store.Wanted(title.Id)!);
        Await(Call("RefreshLibrary"));
        check(missingNav.Visibility == Visibility.Collapsed || int.Parse(((TextBlock)window.FindName("MissingLibraryCount")).Text) < 2, "removing wanted titles takes them out of Missing");
        Call("ShowPage", "Library");
    }
}
