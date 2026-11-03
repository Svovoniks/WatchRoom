using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Several library cards picked at once, so they can be queued, marked watched or removed together.
// Ctrl+click toggles a card, Shift+click selects a range, and while anything is selected a plain click toggles too.
// The selection lasts while the cards stay in view; changing location or page clears it.
public partial class MainWindow
{
    private readonly HashSet<string> selectedCards = [];
    private string? selectionAnchor;
    private bool CardsSelected => selectedCards.Count > 0;
    private LibraryCardView[] SelectedCardViews => libraryCards.Where(view => selectedCards.Contains(view.Key)).ToArray();

    private void LibraryCardClick(object sender, RoutedEventArgs e)
    {
        if (!GuestLibrary && sender is FrameworkElement { DataContext: LibraryCardView view } && PosterGrid.ItemsSource == libraryCards)
        {
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Shift) && selectionAnchor is not null) { SelectCardRange(view, modifiers.HasFlag(ModifierKeys.Control)); return; }
            if (modifiers.HasFlag(ModifierKeys.Control) || CardsSelected) { ToggleCardSelection(view); return; }
        }
        SelectMovie(sender, e);
    }
    private void ToggleCardSelection(LibraryCardView view)
    {
        if (!selectedCards.Remove(view.Key)) selectedCards.Add(view.Key);
        selectionAnchor = view.Key;
        SyncCardSelection();
    }
    // Shift selects from the last card picked through this one; Ctrl+Shift adds the range to what is already selected.
    private void SelectCardRange(LibraryCardView view, bool add)
    {
        var from = libraryCards.ToList().FindIndex(card => card.Key == selectionAnchor); var to = libraryCards.IndexOf(view);
        if (from < 0 || to < 0) { ToggleCardSelection(view); return; }
        if (!add) selectedCards.Clear();
        for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++) selectedCards.Add(libraryCards[index].Key);
        SyncCardSelection();
    }
    private void SelectAllCards()
    {
        foreach (var view in libraryCards) selectedCards.Add(view.Key);
        selectionAnchor ??= libraryCards.FirstOrDefault()?.Key;
        SyncCardSelection();
    }
    private void ClearCardSelection()
    {
        if (!CardsSelected && selectionAnchor is null) return;
        selectedCards.Clear(); selectionAnchor = null;
        SyncCardSelection();
    }
    private void ClearCardSelectionClick(object sender, RoutedEventArgs e) => ClearCardSelection();

    // Drops cards no longer in view, marks the rest and shows what the bar can do with them.
    private void SyncCardSelection()
    {
        if (CardSelectionBar is null) return;
        if (GuestLibrary || PosterGrid.ItemsSource != libraryCards) selectedCards.Clear();
        var keys = libraryCards.Select(view => view.Key).ToHashSet();
        selectedCards.IntersectWith(keys);
        if (selectionAnchor is not null && !keys.Contains(selectionAnchor)) selectionAnchor = null;
        foreach (var view in libraryCards) view.SetSelected(selectedCards.Contains(view.Key));
        CardSelectionBar.Visibility = CardsSelected ? Visibility.Visible : Visibility.Collapsed;
        if (!CardsSelected) return;
        var views = SelectedCardViews;
        CardSelectionCount.Text = views.Length == 1 ? "1 selected" : $"{views.Length} selected";
        var videos = SelectionVideos(views);
        var playable = videos.Any(x => x.Available && File.Exists(x.Path));
        SelectionPlay.IsEnabled = playable && room?.Identity?.Host != false;
        SelectionQueue.IsEnabled = videos.Length > 0 && QueueChangeable;
        var watchable = views.Where(Watchable).ToArray();
        SelectionWatched.IsEnabled = watchable.Any(view => !view.IsWatched);
        SelectionUnwatched.IsEnabled = watchable.Any(view => view.IsWatched || view.Progress > 0);
        var removal = SelectionRemoval(views);
        SelectionRemove.IsEnabled = !scanning && removal.Wanted.Length + removal.Unavailable.Length > 0;
        SelectionRemove.ToolTip = scanning ? "Wait for the scan to finish" : SelectionRemove.IsEnabled
            ? "Removes titles added without files and entries for videos that are gone. Videos that are here stay."
            : "Only titles added without files and videos that are gone can be removed. Videos that are here come back with the next scan.";
        ToolTipService.SetShowOnDisabled(SelectionRemove, true);
    }
    private static bool Watchable(LibraryCardView view) => !view.IsRemote && !view.HasNoFiles && !view.Card.Media.IsMusic;

    // The videos behind the selected cards, in grid order: a show or season brings its episodes.
    private MediaItem[] SelectionVideos(IEnumerable<LibraryCardView> views) => views
        .SelectMany(view => LibraryCatalog.CardItems(items, view.Card, displaySpecialsWithinSeasons, PreferredVersion(view.Card.Media)))
        .DistinctBy(x => x.Id).ToArray();

    private void SelectionPlayClick(object sender, RoutedEventArgs e) => PlayCardVideos(SelectionVideos(SelectedCardViews), (FrameworkElement)sender);
    // Replaces the local queue with the videos that can play and starts the first.
    private void PlayCardVideos(IEnumerable<MediaItem> videos, FrameworkElement source)
    {
        if (!CanEditQueue()) return;
        queue.Clear(); queuePosition = -1; activeQueueId = null;
        foreach (var video in videos.Where(x => x.Available && File.Exists(x.Path))) queue.Add(video);
        PublishQueue(); RefreshQueuePickers(); NextQueued(source, new RoutedEventArgs());
    }
    private void SelectionQueueClick(object sender, RoutedEventArgs e) => ShowAddToQueueMenu((FrameworkElement)sender, SelectionVideos(SelectedCardViews));
    private void SelectionWatchedClick(object sender, RoutedEventArgs e) => MarkSelectionWatched(true);
    private void SelectionUnwatchedClick(object sender, RoutedEventArgs e) => MarkSelectionWatched(false);
    private void MarkSelectionWatched(bool watched)
    {
        var views = SelectedCardViews.Where(Watchable).ToArray();
        if (views.Length == 0) return;
        foreach (var view in views)
        {
            // Marking a single film or episode watched starts it from the beginning next time, as the details page does.
            if (watched && view.Card.Level is "movie" or "episode") library.Setting("position:" + view.Card.Media.Id, "0");
            MarkWatched(view.Card.Media, view.Card.Level, view.Card.Season, watched, refresh: false);
        }
        RefreshHome();
        SetStatus($"{Count(views.Length, "title")} marked as {(watched ? "watched" : "unwatched")}.");
        SyncCardSelection();
    }

    // What Remove from library can take away: titles added without their files, whole, and entries for videos that are gone.
    // Videos still on disk stay, since the next scan would find them again.
    private (WantedTitle[] Wanted, MediaItem[] Unavailable) SelectionRemoval(IEnumerable<LibraryCardView> views)
    {
        var selection = views.Where(view => !view.IsRemote).ToArray();
        var titles = selection.Where(view => view.Card.Level is "series" or "movie").Select(view => WantedFor(view.Card.Media)).OfType<WantedTitle>().DistinctBy(x => x.Id).ToArray();
        var unavailable = selection.SelectMany(view => UnavailableCardItems(view.Card)).DistinctBy(x => x.Id).ToArray();
        return (titles, unavailable);
    }
    private async void SelectionRemoveClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (GuestLibrary || scanning) return;
        var views = SelectedCardViews; var (titles, unavailable) = SelectionRemoval(views);
        if (titles.Length + unavailable.Length == 0) return;
        var tracked = titles.Where(title => WantedProgress(title).OnDisk > 0).ToArray();
        var placeholders = titles.Except(tracked).ToArray();
        var kept = views.Count(view => view.Card.Level is "series" or "movie" ? WantedFor(view.Card.Media) is null && !UnavailableCardItems(view.Card).Any() : !UnavailableCardItems(view.Card).Any());
        var lines = new List<string>();
        if (placeholders.Length > 0) lines.Add($"{Count(placeholders.Length, "title")} with no files {(placeholders.Length == 1 ? "is" : "are")} removed with {(placeholders.Length == 1 ? "its" : "their")} missing and upcoming placeholders.");
        if (tracked.Length > 0) lines.Add($"{Count(tracked.Length, "title")} stop{(tracked.Length == 1 ? "s" : "")} being tracked; the files already here stay.");
        if (unavailable.Length > 0) lines.Add($"{Count(unavailable.Length, "unavailable video")} {(unavailable.Length == 1 ? "is" : "are")} removed. Reconnecting the drive and rescanning brings {(unavailable.Length == 1 ? "it" : "them")} back.");
        if (kept > 0) lines.Add($"{Count(kept, "selected title")} with videos on disk stay{(kept == 1 ? "s" : "")}.");
        if (!Dialogs.Confirm(this, views.Length == 1 ? "Remove from library?" : $"Remove {views.Length} selected titles from library?", string.Join("\n\n", lines), "Remove")) return;
        foreach (var title in titles) await Task.Run(() => WantedTitles.Remove(library, App.DataDirectory, title));
        var removed = unavailable.Length == 0 ? 0 : library.RemoveUnavailable(unavailable.Select(x => x.Id).ToArray());
        foreach (var item in unavailable) watchedState.Remove(item.Id);
        var leaving = placeholders.Any(title => !title.IsMovie && browseSeries == title.Id);
        if (leaving) { browseSeries = browseKind = null; browseSeason = null; }
        ClearCardSelection();
        await RefreshLibrary();
        if (leaving) NavigateLibrary(libraryCategory is 4 ? 0 : libraryCategory);
        var done = new List<string>();
        if (placeholders.Length > 0) done.Add($"{Count(placeholders.Length, "title")} removed");
        if (tracked.Length > 0) done.Add($"stopped tracking {Count(tracked.Length, "title")}");
        if (removed > 0) done.Add($"{Count(removed, "unavailable video")} removed");
        var summary = string.Join(", ", done);
        if (summary.Length > 0) SetStatus(char.ToUpperInvariant(summary[0]) + summary[1..] + ".");
    });

    private void SelectionMoreClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Top };
        foreach (var entry in SelectionMenuItems(includeBarActions: false)) menu.Items.Add(entry);
        button.ContextMenu = menu; menu.IsOpen = true;
    }
    // The right-click menu of a selected card, and the bar's More menu; the bar's own buttons are left out of the latter.
    private IEnumerable<object> SelectionMenuItems(bool includeBarActions)
    {
        var views = SelectedCardViews;
        MenuItem Entry(string header, Action run, bool enabled = true) { var entry = new MenuItem { Header = header, IsEnabled = enabled }; entry.Click += (_, _) => run(); return entry; }
        if (includeBarActions)
        {
            yield return Entry("Play all", () => PlayCardVideos(SelectionVideos(SelectedCardViews), PosterGrid), SelectionPlay.IsEnabled);
            var queueMenu = QueueMenu(SelectionVideos(views)); queueMenu.IsEnabled &= SelectionQueue.IsEnabled; yield return queueMenu;
            yield return Entry("Mark as watched", () => MarkSelectionWatched(true), SelectionWatched.IsEnabled);
            yield return Entry("Mark as unwatched", () => MarkSelectionWatched(false), SelectionUnwatched.IsEnabled);
            yield return Entry("Remove from library…", () => SelectionRemoveClick(SelectionRemove, new RoutedEventArgs()), SelectionRemove.IsEnabled);
            yield return new Separator();
        }
        var members = SelectionMembers(views);
        yield return Entry("Lock metadata", () => LockSelectionMetadata(true), members.Any(x => !x.MetadataLocked));
        yield return Entry("Unlock metadata", () => LockSelectionMetadata(false), members.Any(x => x.MetadataLocked));
        yield return new Separator();
        yield return Entry("Select all", SelectAllCards, views.Length < libraryCards.Count);
        yield return Entry("Clear selection", ClearCardSelection);
    }
    private MediaItem[] SelectionMembers(IEnumerable<LibraryCardView> views)
    {
        var cards = views.Where(view => !view.IsRemote).Select(view => view.Card).ToArray();
        var shows = cards.Where(card => card.Level is "series" or "season").Select(card => LibraryIdentity.ShowKey(card.Media)).ToHashSet();
        var ids = cards.Where(card => card.Level is not ("series" or "season")).Select(card => card.Media.Id).ToHashSet();
        return items.Where(x => ids.Contains(x.Id) || x.Series is not null && shows.Contains(LibraryIdentity.ShowKey(x))).ToArray();
    }
    private void LockSelectionMetadata(bool locked)
    {
        // Read each title from the store: the in-memory list can lag behind a change made a moment ago.
        var members = SelectionMembers(SelectedCardViews).Select(x => library.Get(x.Id)).OfType<MediaItem>().Where(x => x.MetadataLocked != locked).ToArray();
        foreach (var item in members) library.Save(item with { MetadataLocked = locked });
        SetStatus(locked ? "Metadata locked for the selected titles." : "Metadata unlocked for the selected titles.");
        _ = Guard(RefreshLibrary);
    }

    // Ctrl+A selects every card in view and Esc clears the selection, unless a text field has the keys.
    private bool CardSelectionKey(KeyEventArgs e)
    {
        if (GuestLibrary || LibraryPage.Visibility != Visibility.Visible || InAppDialog.Open.Count > 0 || Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox) return false;
        if (e.Key == Key.Escape && CardsSelected) { ClearCardSelection(); return true; }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && libraryCards.Count > 0 && PosterGrid.ItemsSource == libraryCards) { SelectAllCards(); return true; }
        return false;
    }
}
