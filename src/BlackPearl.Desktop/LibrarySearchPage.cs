using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

/// <summary>A choice in one of Search's filter menus. An empty code means any.</summary>
public sealed record SearchChoice(string Name, string Code)
{
    public override string ToString() => Name;
}

// The Search tab is the library's grid in another mode: a search field, the type of title and filter menus take the
// place of the category's breadcrumbs, and the grid lists matching shows, films and albums from the whole library.
// Opening a show from it browses that show in the library; Back returns to the search as it was.
// Audio and subtitle languages come from a background pass over the library's videos (FileLanguageIndex), started the
// first time Search opens and repeated when the library changes. Matches appear as files are read.
public partial class MainWindow
{
    private bool librarySearch;
    private Dictionary<string, FileLanguages> searchLanguages = new(StringComparer.OrdinalIgnoreCase);
    private bool languageIndexRunning, languageIndexAgain, languageIndexWanted, languageIndexDone, updatingSearchFilters;
    private (int Done, int Total)? languageIndexProgress;

    private void SearchNavClick(object sender, RoutedEventArgs e) => OpenSearch(focus: false);

    /// <summary>Shows the Search tab with the search and filters it last had.</summary>
    private void OpenSearch(bool focus)
    {
        if (!ConfirmPageNavigation("Library")) return;
        if (!librarySearch || currentPage != "Library")
        {
            // Remember where Search was opened from before changing anything, so Back returns there as it was.
            RememberLocation();
            ClearCardSelection();
            librarySearch = true; libraryCategory = 0; browseSeries = browseKind = null; browseSeason = null;
            if (GuestLibrary) guestBrowse = new(Category: SearchCategory());
            RefreshSearchOptions();
            var restoring = restoringNavigation; restoringNavigation = true;
            try { ShowPage("Library"); }
            finally { restoringNavigation = restoring; }
        }
        if (!GuestLibrary && !languageIndexDone) IndexLanguages();
        if (focus) { SearchBox.Focus(); SearchBox.SelectAll(); }
    }

    /// <summary>Shows or hides what belongs to Search or to browsing a category; the grid between them is the same.</summary>
    private void ApplyLibraryMode()
    {
        if (SearchHeader is null) return;
        var guest = GuestLibrary;
        SearchHeader.Visibility = librarySearch ? Visibility.Visible : Visibility.Collapsed;
        LibraryBreadcrumbs.Visibility = librarySearch ? Visibility.Collapsed : Visibility.Visible;
        RescanButton.Visibility = AddTitleButton.Visibility = guest || librarySearch ? Visibility.Collapsed : Visibility.Visible;
        // A host's library is searched by name and type; the other filters need the files.
        SearchFilters.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
        // Search shows its count beside the type of title, so its toolbar row only appears for a note.
        LibraryToolbar.MinHeight = librarySearch ? 0 : 40;
        ResultCount.Visibility = librarySearch ? Visibility.Collapsed : Visibility.Visible;
        ArrangeLibraryToolbar();
        // Search and the library share this page, so a change between them changes the sidebar entry and the title.
        if (currentPage is "Library" or "Details") MarkCurrentPage();
        var music = SearchCategory() == LibraryCatalog.Music;
        SearchAudio.IsEnabled = SearchSubtitles.IsEnabled = SearchLengthFilter.IsEnabled = !music;
        ClearSearchFiltersButton.Visibility = SearchFiltersActive() ? Visibility.Visible : Visibility.Collapsed;
        ResetFiltersButton.Content = librarySearch ? "Clear search and filters" : "Show all titles";
        UpdateSearchNote();
    }

    private void UpdateSearchNote()
    {
        if (SearchNote is null) return;
        var text = !librarySearch ? "" : GuestLibrary ? "Searching the host’s library by name. Filters work in your own library."
            : languageIndexProgress is { } progress ? $"Reading audio and subtitle languages · {progress.Done:N0} of {progress.Total:N0} videos" : "";
        SearchNote.Text = text;
        SearchNote.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LibraryToolbar.Visibility = !librarySearch || text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The filters that are on, as their pills read: "Rating 7 and up · Status Unwatched".</summary>
    private string SearchFilterSummary() => string.Join(" · ", new[] { SearchAudio, SearchSubtitles, SearchGenre, SearchDecade, SearchLengthFilter, SearchRating, LibraryStatusFilter }
        .Where(box => box.IsEnabled && box.SelectedIndex > 0)
        .Select(box => FilterPill.GetLabel(box) + " " + (box.SelectedItem is ComboBoxItem item ? item.Content : box.SelectedItem)));

    private int SearchCategory() => SearchKinds?.Children.OfType<RadioButton>().FirstOrDefault(choice => choice.IsChecked == true)?.Tag is string tag ? int.Parse(tag, CultureInfo.InvariantCulture) : 0;

    private static string? Choice(ComboBox box) => box.SelectedValue is string { Length: > 0 } code ? code
        : box.SelectedItem is ComboBoxItem { Tag: string { Length: > 0 } tag } ? tag : null;

    /// <summary>The filters as the menus show them; menus that don't apply to music are left out for it.</summary>
    private LibrarySearchFilters CurrentSearchFilters()
    {
        var category = SearchCategory();
        var music = category == LibraryCatalog.Music;
        return new(category, music ? null : Choice(SearchAudio), music ? null : Choice(SearchSubtitles), Choice(SearchGenre),
            int.TryParse(Choice(SearchDecade), CultureInfo.InvariantCulture, out var decade) ? decade : null,
            !music && Enum.TryParse<SearchLength>(Choice(SearchLengthFilter), out var length) ? length : SearchLength.Any,
            double.TryParse(Choice(SearchRating), CultureInfo.InvariantCulture, out var rating) ? rating : null);
    }

    private bool SearchFiltersActive() => CurrentSearchFilters() != new LibrarySearchFilters(SearchCategory()) || LibraryStatusFilter.SelectedIndex != 0;

    /// <summary>Puts the menus back to the given filters, as Back does.</summary>
    private void ApplySearchFilters(LibrarySearchFilters filters, int status)
    {
        updatingSearchFilters = true;
        try
        {
            foreach (var choice in SearchKinds.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == filters.Category.ToString(CultureInfo.InvariantCulture);
            RefreshSearchOptions(filters);
            Select(SearchAudio, filters.AudioLanguage); Select(SearchSubtitles, filters.SubtitleLanguage);
            Select(SearchGenre, filters.Genre); Select(SearchDecade, filters.Decade?.ToString(CultureInfo.InvariantCulture));
            SelectTag(SearchLengthFilter, filters.Length.ToString());
            SelectTag(SearchRating, filters.MinimumRating?.ToString(CultureInfo.InvariantCulture) ?? "");
            LibraryStatusFilter.SelectedIndex = status;
        }
        finally { updatingSearchFilters = false; }
        static void Select(ComboBox box, string? code) => box.SelectedValue = code ?? "";
        static void SelectTag(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == tag) ?? box.Items[0];
    }

    private void SearchFilterChanged(object sender, RoutedEventArgs e)
    {
        if (updatingSearchFilters || SearchHeader is null) return;
        DetachGuestMirror();
        if (GuestLibrary && librarySearch) guestBrowse = guestBrowse with { Category = SearchCategory() };
        FilterLibrary();
    }

    private void ClearSearchFiltersClick(object sender, RoutedEventArgs e) { ClearSearchFilters(); FilterLibrary(); }
    // Clear filters keeps the type of title; starting over also clears it and the text.
    private void ClearSearchFilters() => ApplySearchFilters(new(SearchCategory()), 0);
    private void ResetSearch() { ApplySearchFilters(new(), 0); SearchBox.Clear(); }

    // An open menu holds the mouse, so a click anywhere else only closes it: WPF's ComboBox takes that click. The click
    // still passes Search's header on its way to the menu, so when it lands on another menu, type of title or a pill's
    // clear button, that control acts on it too once the menu has closed. Moving between filters takes one click.
    private void InitializeSearchFilters() =>
        SearchHeader.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SearchHeaderMouseDown), handledEventsToo: true);

    private void SearchHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The menu only closes for clicks outside it and its list, which reach it as the menu itself.
        if (Mouse.Captured is ComboBox { IsDropDownOpen: true } open && e.OriginalSource == open) ClickThrough(open, e.GetPosition(SearchHeader));
    }

    /// <summary>Acts on the control of Search's header at the point, a position in the header, unless that is the open menu.</summary>
    private void ClickThrough(ComboBox open, Point point)
    {
        if (SearchHeader.InputHitTest(point) is not DependencyObject hit) return;
        for (var node = hit; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == open) return;
            Action? act = node switch
            {
                Button { IsEnabled: true, Command: { } command } button => () => command.Execute(button.CommandParameter),
                ComboBox { IsEnabled: true } other => () => other.IsDropDownOpen = true,
                RadioButton { IsEnabled: true } kind => () => kind.IsChecked = true,
                _ => null
            };
            if (act is null) continue;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, act);
            return;
        }
    }

    // Language lists change while files are read; an open menu keeps its list until it closes.
    private void SearchOptionsClosed(object? sender, EventArgs e) => RefreshSearchOptions();

    /// <summary>
    /// Fills the menus from the library: the languages read so far, the genres and the decades it has. A current choice
    /// stays listed even when nothing has it now, so a menu never changes its choice by itself.
    /// </summary>
    private void RefreshSearchOptions(LibrarySearchFilters? keep = null)
    {
        if (SearchAudio is null) return;
        keep ??= CurrentSearchFilters();
        var languages = searchLanguages.Values;
        var videos = items.Where(item => (item.Available || item.IsVirtual) && !item.IsExtra && !item.IsMusic).ToArray();
        var restoring = updatingSearchFilters; updatingSearchFilters = true;
        try
        {
            Fill(SearchAudio, "Any audio", Languages(languages.SelectMany(file => file.Audio), keep.AudioLanguage), null);
            Fill(SearchSubtitles, "Any subtitles", Languages(languages.SelectMany(file => file.Subtitles), keep.SubtitleLanguage),
                new SearchChoice("No subtitles", LibrarySearchFilters.NoSubtitles));
            var genres = videos.SelectMany(item => item.Genres ?? []).Append(keep.Genre).OfType<string>().Where(genre => genre.Trim().Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase);
            Fill(SearchGenre, "Any genre", genres.Select(genre => new SearchChoice(genre, genre)), null);
            var decades = videos.Select(item => item.Year).OfType<int>().Where(year => year > 1800).Select(year => year / 10 * 10)
                .Append(keep.Decade ?? 0).Where(decade => decade > 0).Distinct().OrderDescending();
            Fill(SearchDecade, "Any year", decades.Select(decade => new SearchChoice($"{decade}s", decade.ToString(CultureInfo.InvariantCulture))), null);
        }
        finally { updatingSearchFilters = restoring; }

        static IEnumerable<SearchChoice> Languages(IEnumerable<string> codes, string? current) => codes.Append(current)
            .OfType<string>().Where(code => code != LibrarySearchFilters.NoSubtitles).Distinct()
            .Select(code => new SearchChoice(LanguageName(code), code)).OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase);
        static void Fill(ComboBox box, string any, IEnumerable<SearchChoice> options, SearchChoice? extra)
        {
            if (box.IsDropDownOpen) return;
            var list = new List<SearchChoice> { new(any, "") };
            if (extra is not null) list.Add(extra);
            list.AddRange(options);
            var current = box.SelectedValue as string ?? "";
            if (box.ItemsSource is List<SearchChoice> shown && shown.SequenceEqual(list)) return;
            box.ItemsSource = list;
            box.SelectedValue = list.Any(option => option.Code == current) ? current : "";
        }
    }

    /// <summary>Reads the languages of the library's videos in the background; a pass under way is repeated once it ends.</summary>
    private async void IndexLanguages()
    {
        languageIndexWanted = true;
        if (languageIndexRunning) { languageIndexAgain = true; return; }
        languageIndexRunning = true;
        try
        {
            do
            {
                languageIndexAgain = false;
                var paths = items.Where(item => item.Available && !item.IsVirtual && !item.IsMusic).Select(item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var total = paths.Length;
                // A pass over files read before reports what it has read so far on top of the last pass's results.
                void Progress(Dictionary<string, FileLanguages> partial, int done) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (closing || !languageIndexRunning) return;
                    var merged = new Dictionary<string, FileLanguages>(searchLanguages, StringComparer.OrdinalIgnoreCase);
                    foreach (var (path, languages) in partial) merged[path] = languages;
                    languageIndexProgress = (done, total);
                    UseLanguages(merged);
                }));
                var token = lifetime.Token;
                var result = await Task.Run(() => FileLanguageIndex.Read(library, paths, Progress, token), token);
                if (closing) return;
                languageIndexProgress = null;
                UseLanguages(result);
            } while (languageIndexAgain && !closing);
            languageIndexDone = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { PlaybackDiagnostics.Record("search-language-index-failed", new { error = ex.GetType().Name, ex.Message }); }
        finally { languageIndexRunning = false; languageIndexProgress = null; UpdateSearchNote(); }
    }

    private void UseLanguages(Dictionary<string, FileLanguages> languages)
    {
        searchLanguages = languages;
        RefreshSearchOptions(); UpdateSearchNote();
        if (librarySearch && currentPage == "Library" && !GuestLibrary && CurrentSearchFilters() is { AudioLanguage: not null } or { SubtitleLanguage: not null })
            FilterLibrary(backgroundUpdate: true);
    }
}
