using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Titles added before their files are here: adding them, the panel on a wanted show or film, linking files and stopping.
public partial class MainWindow
{
    private Dictionary<string, WantedTitle> wanted = [];
    private int missingTitleCount;
    private void ReloadWanted() => wanted = library.Wanted().ToDictionary(x => x.Id);
    private WantedTitle? WantedFor(MediaItem? item) => item is null ? null : wanted.GetValueOrDefault(LibraryIdentity.ShowKey(item));
    // Shows and films whose cards carry a Missing or Upcoming badge, for the sidebar's Missing category.
    private void CountMissingTitles()
    {
        var playable = items.Where(x => x.Available && x.Series is not null).Select(LibraryIdentity.ShowKey).ToHashSet();
        var keys = items.Where(x => x.IsVirtual).Select(LibraryIdentity.ShowKey).Where(key => playable.Contains(key) || wanted.ContainsKey(key)).ToHashSet();
        missingTitleCount = keys.Count == 0 ? 0 : items.Where(x => !x.IsExtra && (x.Available || x.IsVirtual) && keys.Contains(LibraryIdentity.ShowKey(x)))
            .GroupBy(LibraryIdentity.ShowKey).Count(g => LibraryCatalog.TitleStatus(g.ToArray()) is not null);
    }
    private void LibraryToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        if (narrow == AddTitleButton.Content is not string) return;
        if (narrow) { AddTitleButton.ContentTemplate = null; AddTitleButton.Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 13 }; AddTitleButton.Padding = new Thickness(13, 0, 13, 0); }
        else { AddTitleButton.ContentTemplate = (DataTemplate)FindResource("AddContent"); AddTitleButton.Content = "Add title"; AddTitleButton.Padding = new Thickness(14, 0, 14, 0); }
    }
    // A search that finds nothing in the library offers to look the title up and add it.
    private void ShowFindTitle(string query, bool offer)
    {
        offer &= query.Length > 0 && !GuestLibrary;
        FindTitleButton.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
        FindTitleButton.Content = TmdbCredentials.Resolve(savedMetadataToken) is not null ? $"Find “{query}” on TMDB and add it" : $"Search for “{query}” and add it";
        NoResultsHint.Text = offer ? "You can still add it. It shows as Missing until a scan finds its files." : "Try another search or clear your filters.";
    }
    private async void AddTitleClick(object sender, RoutedEventArgs e) => await Guard(() => AddTitle(""));
    private async void FindTitleClick(object sender, RoutedEventArgs e) => await Guard(() => AddTitle(SearchBox.Text.Trim()));
    private async Task AddTitle(string query)
    {
        if (GuestLibrary || InAppDialog.Open.Count > 0) return;
        if (!ConfirmPageNavigation("Library")) return;
        var choice = AddTitleDialog.Show(this, library, savedMetadataToken, query, items, lifetime.Token);
        if (choice is null) return;
        if (choice.Existing is { } existing)
        {
            if (existing.Series is null) { Select(existing); Details.ScrollToTop(); }
            else NavigateLibrary(CategoryFor(existing), LibraryIdentity.ShowKey(existing), existing.Kind);
            SetStatus(existing.DisplayTitle + " is already in your library."); return;
        }
        var match = choice.Match!;
        var title = WantedTitles.FromMatch(match, choice.Kind, choice.Tracked ?? [], choice.AddNewSeasons, choice.Poster, choice.Seasons);
        title = await Task.Run(() =>
        {
            var added = WantedTitles.Add(library, App.DataDirectory, title, choice.Seasons ?? [], choice.WatchedThrough, choice.Seen);
            // Files already in the library may be this title, filed under another name.
            WantedTitles.Attach(library, App.DataDirectory);
            return added;
        });
        watchedState.Clear();
        await RefreshLibrary();
        OpenWanted(title);
        var progress = WantedProgress(title);
        var arrived = library.LastWantedArrivals.FirstOrDefault();
        var text = $"{title.Title} added to {CategoryName(title.Kind == "Anime" ? 3 : title.IsMovie ? 1 : 2)}" + (arrived is not null ? " · its files were already in your library" :
            title.IsMovie ? progress.Upcoming > 0 ? " · Upcoming" : " · Missing" : $" · {progress.Missing} missing" + (progress.Upcoming > 0 ? $", {progress.Upcoming} upcoming" : ""));
        SetStatus(text); InAppDialog.Toast(this, text, TimeSpan.FromSeconds(6));
    }
    private void OpenWanted(WantedTitle title)
    {
        if (!wanted.ContainsKey(title.Id) && title.IsMovie)
        {
            // A film whose file was already here is done; open the file.
            if (items.FirstOrDefault(x => x.Series is null && LibraryIdentity.ProviderId(x, title.Provider) == title.ProviderId.ToString()) is { } film) { Select(film); Details.ScrollToTop(); }
            return;
        }
        if (title.IsMovie) { if (items.FirstOrDefault(x => x.Id == title.Id) is { } film) { Select(film); Details.ScrollToTop(); } return; }
        var category = title.Kind == "Anime" ? 3 : 2;
        NavigateLibrary(category, title.Id, items.FirstOrDefault(x => LibraryIdentity.ShowKey(x) == title.Id)?.Kind ?? title.Kind);
    }
    private WantedProgress WantedProgress(WantedTitle title) => WantedTitles.Progress(title,
        items.Where(x => title.IsMovie ? x.Id == title.Id : x.Series is not null && LibraryIdentity.ShowKey(x) == title.Id), WasWatched);
    private string PlaceholderNotice(MediaItem item)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var date = DateOnly.TryParse(item.AirDate, out var day) ? day : (DateOnly?)null;
        var what = item.Series is null ? "This film" : "This episode";
        return date > today ? $"{what} comes out {date:MMM d, yyyy}. It shows as Missing after that until a scan finds its file."
            : $"{what} isn't in your folders yet. Add the video to a library folder and it's picked up on the next scan.";
    }

    // The wanted show being browsed, or the wanted film open in Details, gets its panel.
    private void ShowWantedPanels()
    {
        if (WantedLibraryPanel is null || WantedDetailPanel is null) return;
        var showKey = GuestLibrary || browseSeries is null || browseSeason is not null ? null : items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind)) is { } member ? LibraryIdentity.ShowKey(member) : null;
        Present(WantedLibraryPanel, showKey is null ? null : wanted.GetValueOrDefault(showKey));
        Present(WantedDetailPanel, !GuestLibrary && selected is { Series: null } item ? wanted.GetValueOrDefault(item.Id) : null);
    }
    // Library updates arrive often during scans; the panel is rebuilt only when what it shows changed, so focus stays put.
    private void Present(ContentControl host, WantedTitle? title)
    {
        var progress = title is null ? null : WantedProgress(title);
        var key = title is null ? null : Wire.Serialize(title) + Wire.Serialize(progress! with { NextUp = null }) + progress!.NextUp?.Id +
            items.FirstOrDefault(x => !x.IsVirtual && (title.IsMovie ? x.Id == title.Id : x.Series is not null && LibraryIdentity.ShowKey(x) == title.Id))?.Path;
        if (host.Tag as string == key && (key is null) == (host.Content is null)) return;
        host.Tag = key; host.Content = title is null ? null : WantedPanel(title);
        host.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private FrameworkElement WantedPanel(WantedTitle title)
    {
        var progress = WantedProgress(title);
        var panel = new StackPanel();
        var border = new Border { Child = panel, Style = (Style)FindResource("Card"), Margin = new Thickness(0) };
        AutomationProperties.SetName(border, "Wanted title");
        TextBlock Line(string text, bool muted = true, double size = 13) { var line = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }; if (muted) line.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return line; }
        if (!title.IsMovie)
        {
            var total = progress.OnDisk + progress.Missing + progress.Upcoming;
            var heading = Line($"{progress.OnDisk} of {total} tracked {(total == 1 ? "episode" : "episodes")} on disk" + (progress.Upcoming > 0 ? $" · {progress.Upcoming} upcoming" : ""), muted: false, size: 14);
            heading.FontWeight = FontWeights.SemiBold; panel.Children.Add(heading);
            var bar = new ProgressBar { Maximum = Math.Max(1, total), Value = progress.OnDisk, Height = 4, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Width = 420, Margin = new Thickness(0, 8, 0, 8), Style = (Style)FindResource("CardProgress") };
            AutomationProperties.SetName(bar, "Episodes on disk"); panel.Children.Add(bar);
            if (progress.NextUp is { } next)
                panel.Children.Add(Line($"Next up · S{next.Season}E{next.Episode}" + (string.IsNullOrWhiteSpace(next.EpisodeTitle) ? "" : $" “{next.EpisodeTitle}”") + (progress.NextUpStatus is { } status ? " · " + status : "")));
        }
        else
        {
            var release = DateOnly.TryParse(title.ReleaseDate, out var day) ? day.ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture) : null;
            var heading = Line(progress.Upcoming > 0 ? release is null ? "Upcoming · no file yet" : $"Comes out {release} · no file yet" : "Missing · no file yet", muted: false, size: 14);
            heading.FontWeight = FontWeights.SemiBold; panel.Children.Add(heading);
            if (progress.Upcoming > 0) panel.Children.Add(Line("It shows as Missing after release until a scan finds its file."));
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button Action(string text, Func<Task> run, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            if (primary) button.SetResourceReference(StyleProperty, "Primary");
            button.Click += async (_, _) => await Guard(run);
            actions.Children.Add(button); return button;
        }
        Action(title.IsMovie ? "Link a file or folder…" : "Link a folder…", () => LinkFolder(title), primary: progress.OnDisk == 0).ToolTip = "Choose where this title's files are";
        Action("Rescan now", Scan).ToolTip = "Check your folders for new videos";
        if (!title.IsMovie) Action("Edit seasons", () => EditWantedSeasons(title));
        Action(progress.OnDisk == 0 ? "Remove from library" : "Stop tracking", () => RemoveWanted(title)).ToolTip = progress.OnDisk == 0
            ? "Remove this title and its placeholders" : "Keep the files here and stop showing missing episodes";
        panel.Children.Add(actions);
        var location = items.FirstOrDefault(x => !x.IsVirtual && (title.IsMovie ? x.Id == title.Id : LibraryIdentity.ShowKey(x) == title.Id && x.Series is not null));
        panel.Children.Add(Line(location is null ? "No files yet. They're picked up automatically when a scan finds them in your library folders." : "Files: " + WantedTitles.Location([location])));
        var untracked = (title.KnownSeasons ?? []).Where(s => !title.Seasons.Contains(s.Number)).OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToArray();
        if (untracked.Length > 0)
        {
            var seasons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            AutomationProperties.SetName(seasons, "Seasons not tracked");
            foreach (var season in untracked)
            {
                var button = AddTitleDialog.Dashed($"Track {AddTitleDialog.SeasonName(season.Number)} · {season.Episodes} {(season.Episodes == 1 ? "episode" : "episodes")}");
                button.ToolTip = "Its episodes aren't counted as missing";
                button.Click += async (_, _) => await Guard(() => SetWantedSeasons(title, title.Seasons.Append(season.Number).ToArray(), title.AddNewSeasons));
                seasons.Children.Add(button);
            }
            panel.Children.Add(seasons);
        }
        if (title.Candidate is { } candidate)
        {
            var banner = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 14, 0, 0) };
            banner.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var link = new Button { Content = "Link", Margin = new Thickness(8, 0, 0, 0) }; link.SetResourceReference(StyleProperty, "Primary");
            var reject = new Button { Content = "Not this", Margin = new Thickness(8, 0, 0, 0) };
            link.Click += async (_, _) => await Guard(() => LinkCandidate(title));
            reject.Click += async (_, _) => await Guard(async () => { WantedTitles.Reject(library, title); await RefreshLibrary(); SetStatus("Those files won't be suggested for " + title.Title + " again."); });
            buttons.Children.Add(link); buttons.Children.Add(reject);
            DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons);
            var texts = new StackPanel();
            var found = Line("Found possible files: " + candidate.Path, muted: false); found.FontWeight = FontWeights.SemiBold; texts.Children.Add(found);
            texts.Children.Add(Line(candidate.Reason + ". Nothing was linked automatically."));
            row.Children.Add(texts); banner.Child = row;
            AutomationProperties.SetName(banner, "Possible files"); AutomationProperties.SetLiveSetting(banner, AutomationLiveSetting.Polite);
            panel.Children.Add(banner);
        }
        return border;
    }

    // The folder joins the title. One outside the library folders becomes a library folder first, since only those are scanned.
    private async Task LinkFolder(WantedTitle title)
    {
        if (scanning) { SetStatus("Wait for the scan to finish, then link the files."); return; }
        var picker = new OpenFolderDialog { Title = $"Choose the folder with {title.Title}" };
        if (picker.ShowDialog(this) != true) return;
        var path = picker.FolderName;
        if (!folders.Any(f => LibraryStore.IsWithin(path, f.Path)))
        {
            folders.Add(new(path, title.Kind == "Anime" ? "Anime" : title.IsMovie ? "Movie" : "Show")); SaveFolders();
        }
        await Scan();
        ReloadWanted();
        if (!wanted.TryGetValue(title.Id, out var current)) { SetStatus(title.Title + " · the film is in your library now."); OpenWanted(title); return; }
        var folder = WantedTitles.Folder(current, items, path);
        if (folder is not { } chosen) { Dialogs.Alert(this, "No videos found", $"BlackPearl found no {(title.IsMovie ? "films" : "episodes")} in {path}. Check the folder and try again."); return; }
        if (chosen.Likeness is WantedTitles.Likeness.None)
        {
            var looksLike = chosen.Files[0].Series ?? chosen.Files[0].Title;
            if (!Dialogs.Confirm(this, "Link this folder?", $"This folder looks like {looksLike}, not {title.Title}. Link it anyway? Its files will be listed as {title.Title}" + (title.IsMovie ? "." : " episodes."), "Link anyway")) return;
        }
        var linked = await Task.Run(() => WantedTitles.Link(library, App.DataDirectory, current, chosen.Files));
        watchedState.Clear(); await RefreshLibrary(); OpenWanted(current);
        SetStatus(title.IsMovie ? $"Linked {title.Title}." : $"Linked {linked} {(linked == 1 ? "file" : "files")} to {title.Title}.");
    }
    private async Task LinkCandidate(WantedTitle title)
    {
        if (title.Candidate is not { } candidate) return;
        var files = items.Where(x => !x.IsVirtual && !x.IsExtra && LibraryIdentity.ShowKey(x) == candidate.Key).ToArray();
        if (files.Length == 0) { SetStatus("Those files are no longer in your library."); WantedTitles.Reject(library, title); await RefreshLibrary(); return; }
        var linked = await Task.Run(() => WantedTitles.Link(library, App.DataDirectory, title, files));
        watchedState.Clear(); await RefreshLibrary(); OpenWanted(title);
        SetStatus(title.IsMovie ? $"Linked {title.Title}." : $"Linked {linked} {(linked == 1 ? "file" : "files")} to {title.Title}.");
    }
    private async Task EditWantedSeasons(WantedTitle title)
    {
        if (AddTitleDialog.EditSeasons(this, title) is not { } edit) return;
        await SetWantedSeasons(title, edit.Seasons, edit.AddNewSeasons);
    }
    // Untracked seasons lose their placeholders at once; newly tracked ones need their episodes from the provider.
    private async Task SetWantedSeasons(WantedTitle title, int[] seasons, bool addNew)
    {
        var current = (library.Wanted(title.Id) ?? title) with { Seasons = seasons.Distinct().Order().ToArray(), AddNewSeasons = addNew };
        library.SaveWanted(current);
        await Task.Run(() => WantedTitles.Reconcile(library, App.DataDirectory, current));
        if (seasons.Except(title.Seasons).Any())
        {
            SetStatus("Loading episodes · " + title.Title);
            var token = savedMetadataToken;
            await Task.Run(() => WantedTitles.RefreshAsync(library, App.DataDirectory, token, null, lifetime.Token, only: title.Id));

        }
        await RefreshLibrary();
        SetStatus($"Tracking {seasons.Length} {(seasons.Length == 1 ? "season" : "seasons")} of {title.Title}.");
    }
    private async Task RemoveWanted(WantedTitle title)
    {
        var onDisk = WantedProgress(title).OnDisk > 0;
        if (!Dialogs.Confirm(this, onDisk ? $"Stop tracking {title.Title}?" : $"Remove {title.Title}?",
            onDisk ? "The files already here stay in your library. Missing and upcoming episodes stop showing." : "It has no files, so only its missing and upcoming placeholders are removed.",
            onDisk ? "Stop tracking" : "Remove")) return;
        await Task.Run(() => WantedTitles.Remove(library, App.DataDirectory, title));
        if (!onDisk && !title.IsMovie && browseSeries == title.Id) { browseSeries = browseKind = null; browseSeason = null; }
        await RefreshLibrary();
        if (!onDisk) NavigateLibrary(libraryCategory is 4 ? 0 : libraryCategory);
        SetStatus(onDisk ? $"Stopped tracking {title.Title}." : $"{title.Title} removed from your library.");
    }
    // Tells what the last scan linked to wanted titles, and which ones it found possible files for.
    private void AnnounceArrivals(HashSet<string> hadCandidates)
    {
        var lines = library.LastWantedArrivals.ToList();
        lines.AddRange(wanted.Values.Where(x => x.Candidate is not null && !hadCandidates.Contains(x.Id)).Select(x => $"{x.Title} · possible files found. Open it to confirm."));
        if (lines.Count == 0) return;
        if (lines.Count > 0) watchedState.Clear();
        InAppDialog.Toast(this, string.Join("\n", lines.Take(4)) + (lines.Count > 4 ? $"\n{lines.Count - 4} more" : ""), TimeSpan.FromSeconds(8));
    }
}
