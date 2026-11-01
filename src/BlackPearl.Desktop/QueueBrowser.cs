using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// The Queues gallery, queue artwork, and the Add videos panel beside an opened queue.
public partial class MainWindow
{
    // Rows, covers and search results share artwork so each image decodes once. Only rows on screen ask
    // for it, and the cache is dropped when it grows so scrolling a large library does not hold every image.
    private readonly Dictionary<string, LibraryCardView> queueArtwork = new(StringComparer.OrdinalIgnoreCase);
    private LibraryCardView QueueArtwork(string key, MediaItem item, Func<LibraryCard> card)
    {
        if (queueArtwork.TryGetValue(key, out var view))
        {
            if (!ReferenceEquals(view.Card.Media, item)) view.Update(card());
            return view;
        }
        if (queueArtwork.Count >= 160) queueArtwork.Clear();
        queueArtwork[key] = view = new LibraryCardView(card());
        _ = view.LoadPosterAsync();
        return view;
    }
    // Episode stills, or a movie's poster, with the item's watch progress.
    private LibraryCardView QueueStill(MediaItem item)
    {
        var view = QueueArtwork("still:" + item.Id, item, () => new LibraryCard(item.DisplayTitle, item.Caption,
            item.Series is null ? new[] { item.Poster, item.SeriesPoster }.FirstOrDefault(ArtworkCache.IsUsable) : LibraryCatalog.EpisodeArtwork(item),
            item, item.Series is null ? "movie" : "episode"));
        UpdateCardPlayback(view);
        return view;
    }
    // One portrait poster per show or movie, in queue order.
    private List<LibraryCardView> QueueCovers(IEnumerable<MediaItem> media, int count)
    {
        var covers = new List<LibraryCardView>(count);
        var shows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media)
        {
            if (covers.Count == count) break;
            if (!shows.Add(LibraryIdentity.ShowKey(item))) continue;
            var poster = (item.Series is null ? new[] { item.Poster, item.SeriesPoster } : new[] { item.SeriesPoster, item.SeasonPoster, item.Poster })
                .FirstOrDefault(ArtworkCache.IsUsable);
            if (poster is not null) covers.Add(QueueArtwork("cover:" + poster, item, () => new LibraryCard(item.DisplayTitle, "", poster, item, "movie")));
        }
        return covers;
    }
    private sealed record QueueCoverSlot(LibraryCardView? View)
    {
        public Visibility Visibility => View is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private sealed record QueueCard(SavedQueue? Queue, string Name, string Caption)
    {
        public QueueCoverSlot Cover1 { get; init; } = new(null);
        public QueueCoverSlot Cover2 { get; init; } = new(null);
        public QueueCoverSlot Cover3 { get; init; } = new(null);
        public bool Playing { get; init; }
        public int Count { get; init; }
        public int Watched { get; init; }
        public Visibility CreateVisibility => Queue is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PlaceholderVisibility => Cover1.View is null ? Visibility.Visible : Visibility.Collapsed;
        public string PlaceholderIcon => Queue is null ? "" : "";
        public Visibility PlayingVisibility => Playing ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RingVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string WatchedText => $"{Watched} of {Count} watched";
        // The ring's dash is measured in stroke widths: a 24px ellipse with a 3px stroke is about 22 widths around.
        public DoubleCollection RingDash => new([Count == 0 ? 0 : 21.99 * Watched / Count, 100]);
        public string AccessibleName => Queue is null ? "New queue" : $"{Name}, {VideoCount(Count)}" + (Playing ? ", playing" : "");
    }
    private void RefreshQueueGallery()
    {
        // Covers and watch counts are read only while the gallery is on screen; showing the page refreshes it.
        if (QueueGallery is null || QueuesPage.Visibility != Visibility.Visible || QueueGalleryView.Visibility != Visibility.Visible) return;
        var byId = QueueMedia();
        var playback = ShowingHostQueues ? [] : library.PlaybackSettings();
        QueueGallery.ItemsSource = QueueSource.Select(saved =>
        {
            var media = saved.MediaIds.Select(id => byId.GetValueOrDefault(id)).OfType<MediaItem>().ToArray();
            var covers = QueueCovers(media, 3);
            return new QueueCard(saved, saved.Name, QueueSummary(saved, media, playback, detailed: false))
            {
                Cover1 = new(covers.ElementAtOrDefault(0)), Cover2 = new(covers.ElementAtOrDefault(1)), Cover3 = new(covers.ElementAtOrDefault(2)),
                Playing = ShowingHostQueues ? saved.Id == hostQueues!.ActiveId && hostQueues.ActiveIndex >= 0 : saved.Id == activeQueueId && playingItem is not null, Count = saved.MediaIds.Length,
                Watched = media.Count(item => playback.TryGetValue("watched:" + item.Id, out var value) && value == "true")
            };
        }).Append(new QueueCard(null, "New queue", "Start an empty queue")).ToArray();
    }
    private void QueueCardClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not QueueCard card) return;
        if (card.Queue is null) CreateQueue(sender, e); else OpenQueue(card.Queue.Id);
    }
    private void QueueCardContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { Tag: QueueCard { Queue: { } shown } } button || QueueSource.FirstOrDefault(x => x.Id == shown.Id) is not { } saved)
        { e.Handled = true; return; }
        var menu = new ContextMenu { PlacementTarget = button };
        var open = new MenuItem { Header = "Open" }; open.Click += (_, _) => OpenQueue(saved.Id); menu.Items.Add(open);
        foreach (var item in QueueActions(saved, includePlay: true)) menu.Items.Add(item);
        button.ContextMenu = menu;
    }

    // Add videos: search results grouped by season, with movies listed after shows.
    private sealed class QueueAddRow(string title, string caption) : INotifyPropertyChanged
    {
        public string Title { get; } = title;
        public MediaItem? Media { get; init; }
        public MediaItem[] Group { get; init; } = [];
        public bool IsGroup { get; init; }
        public bool CanAddAll { get; init; }
        public Func<MediaItem, LibraryCardView>? Artwork { get; init; }
        public LibraryCardView? View => Media is null || Artwork is null ? null : Artwork(Media);
        private int queued;
        // How many of the row's videos the opened queue already holds.
        public int Queued
        {
            get => queued;
            set
            {
                if (queued == value) return;
                queued = value;
                foreach (var name in new[] { nameof(Queued), nameof(Caption), nameof(QueuedVisibility), nameof(AddVisibility), nameof(AccessibleName) }) PropertyChanged?.Invoke(this, new(name));
            }
        }
        private bool AllQueued => IsGroup ? Group.Length > 0 && queued >= Group.Length : queued > 0;
        public string Caption => IsGroup && queued > 0 && !AllQueued ? $"{caption} · {queued} in queue" : caption;
        public Visibility ThumbnailVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
        public Visibility QueuedVisibility => AllQueued && (!IsGroup || CanAddAll) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddVisibility => AllQueued || IsGroup && !CanAddAll ? Visibility.Collapsed : Visibility.Visible;
        public string AddLabel => IsGroup ? "Add all" : "Add";
        public string AddHint => IsGroup ? $"Add the rest of {Title} to the queue" : $"Add {Title} to the queue";
        public string AccessibleName => $"{Title}, {Caption}" + (AllQueued ? ", in queue" : "");
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly DispatcherTimer queueAddSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? queueAddSearch;
    private QueueAddRow[] queueAddRows = [];
    private async void AddQueueVideos(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (OpenedQueue is not { } saved || !CanEditQueue()) return;
        if (QueueAddPanel.Visibility == Visibility.Visible) { QueueAddSearch.Focus(); return; }
        // Checking files can block on a sleeping or network drive, so it runs off the UI thread.
        var library = items;
        var anyAvailable = await Task.Run(() => library.Any(x => x.Available && File.Exists(x.Path)));
        if (closing || OpenedQueue?.Id != saved.Id || !CanEditQueue()) return;
        if (!anyAvailable)
        {
            ShowPage("Folders");
            SetStatus("Add or reconnect a video folder, then return to Queues to add videos.");
            return;
        }
        QueueAddPanel.Visibility = Visibility.Visible; QueueAddColumn.Width = new GridLength(380);
        QueueAddSearch.Focus();
        SearchQueueAdditions();
    });
    private void CloseQueueAdd(object sender, RoutedEventArgs e) { CloseQueueAddPanel(); AddQueueVideosButton.Focus(); }
    private void CloseQueueAddPanel()
    {
        if (QueueAddPanel is null || QueueAddPanel.Visibility != Visibility.Visible) return;
        QueueAddPanel.Visibility = Visibility.Collapsed; QueueAddColumn.Width = new GridLength(0);
        queueAddSearchDelay.Stop(); queueAddSearch?.Cancel(); queueAddSearch = null;
        queueAddRows = []; QueueAddResults.ItemsSource = null; QueueAddSearch.Text = ""; QueueAddStatus.Text = "";
    }
    private void QueueAddSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (QueueAddPanel.Visibility != Visibility.Visible) return;
        queueAddSearchDelay.Stop(); queueAddSearchDelay.Tick -= QueueAddSearchElapsed; queueAddSearchDelay.Tick += QueueAddSearchElapsed; queueAddSearchDelay.Start();
    }
    private void QueueAddSearchElapsed(object? sender, EventArgs e) { queueAddSearchDelay.Stop(); SearchQueueAdditions(); }
    private void QueueAddFilterChanged(object sender, SelectionChangedEventArgs e) { if (QueueAddPanel?.Visibility == Visibility.Visible) SearchQueueAdditions(); }
    private async void SearchQueueAdditions()
    {
        queueAddSearch?.Cancel();
        var cancellation = queueAddSearch = new CancellationTokenSource();
        var query = QueueAddSearch.Text; var filter = QueueAddFilter.SelectedIndex; var source = items;
        Func<MediaItem, LibraryCardView> artwork = QueueStill;
        QueueAddRow[] rows;
        try { rows = await Task.Run(() => BuildQueueAddRows(source, query, filter, artwork, cancellation.Token), cancellation.Token); }
        catch (OperationCanceledException) { return; }
        if (closing || cancellation != queueAddSearch || QueueAddPanel.Visibility != Visibility.Visible) return;
        queueAddRows = rows;
        RefreshQueueAddMarks();
        QueueAddResults.ItemsSource = rows;
        var videos = rows.Count(row => !row.IsGroup);
        QueueAddStatus.Text = videos > 0 ? VideoCount(videos) : string.IsNullOrWhiteSpace(query) ? "No available videos." : $"Nothing matches “{query.Trim()}”.";
        if (rows.Length > 0) QueueAddResults.ScrollIntoView(rows[0]);
    }
    private static QueueAddRow[] BuildQueueAddRows(List<MediaItem> source, string query, int filter, Func<MediaItem, LibraryCardView> artwork, CancellationToken cancellation)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(MediaItem item)
        {
            cancellation.ThrowIfCancellationRequested();
            if (filter switch { 1 => item.Kind != "Movie", 2 => item.Kind != "Show", 3 => item.Kind != "Anime", 4 => !item.IsMusic, _ => false }) return false;
            if (terms.Length == 0) return true;
            var text = $"{item.Series} {item.Title} {item.EpisodeTitle} {item.Artist} {item.Year} {Path.GetFileNameWithoutExtension(item.Path)}" +
                (item.Episode is { } episode ? $" S{item.Season}E{episode} S{item.Season:00}E{episode:00}" : "");
            return terms.All(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        var matches = LibraryCatalog.PreferredFiles(source.Where(item => !item.IsExtra && !item.IsVirtual && item.Available)).Where(Matches).ToArray();
        var rows = new List<QueueAddRow>();
        foreach (var season in matches.Where(item => item.Series is not null)
            .GroupBy(item => (Show: LibraryIdentity.ShowKey(item), item.Season))
            .OrderBy(group => group.First().Series, StringComparer.CurrentCultureIgnoreCase).ThenBy(group => group.Key.Season is null or 0 ? int.MaxValue : group.Key.Season))
        {
            cancellation.ThrowIfCancellationRequested();
            var episodes = season.OrderBy(item => item.Episode ?? int.MaxValue).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            rows.Add(new QueueAddRow($"{episodes[0].Series} · {SeasonName(season.Key.Season ?? -1, episodes)}", episodes.Length == 1 ? "1 episode" : $"{episodes.Length} episodes")
                { IsGroup = true, CanAddAll = true, Group = episodes });
            rows.AddRange(episodes.Select(item => new QueueAddRow(item.EpisodeDisplayTitle, item.RuntimeMinutes is int minutes and > 0 ? $"{minutes} min" : item.Caption)
                { Media = item, Group = [item], Artwork = artwork }));
        }
        var movies = matches.Where(item => item.Series is null && item.ExtraOf is null).OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Year).ToArray();
        if (movies.Length > 0)
        {
            rows.Add(new QueueAddRow("Movies and other videos", VideoCount(movies.Length)) { IsGroup = true });
            rows.AddRange(movies.Select(item => new QueueAddRow(item.Title, item.Caption + (item.RuntimeMinutes is int minutes and > 0 ? $" · {minutes} min" : ""))
                { Media = item, Group = [item], Artwork = artwork }));
        }
        return rows.ToArray();
    }
    // Marks results already in the opened queue after every queue change.
    private void RefreshQueueAddMarks()
    {
        if (queueAddRows.Length == 0) return;
        var queued = new HashSet<string>(OpenedQueue?.MediaIds ?? []);
        foreach (var row in queueAddRows) row.Queued = row.Group.Count(item => queued.Contains(item.Id));
    }
    private void AddQueueRows(IEnumerable<QueueAddRow> rows)
    {
        if (OpenedQueue is not { } saved) return;
        var queued = new HashSet<string>(saved.MediaIds);
        // Add all skips the season's videos already queued; a single video can be queued again.
        var media = rows.SelectMany(row => row.IsGroup ? row.Group.Where(item => !queued.Contains(item.Id)) : row.Group).DistinctBy(item => item.Id).ToArray();
        if (media.Length > 0) AddToSavedQueue(saved, media);
    }
    private void QueueAddRowClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is QueueAddRow row) AddQueueRows([row]);
    }
    private void QueueAddDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not ListBoxItem)
        {
            if (node is ButtonBase) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (node is ListBoxItem { DataContext: QueueAddRow row } && (!row.IsGroup || row.CanAddAll)) { AddQueueRows([row]); e.Handled = true; }
    }
    private void QueueAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseQueueAdd(sender, e); e.Handled = true; return; }
        if (e.Key != Key.Enter || e.OriginalSource is not (ListBox or ListBoxItem)) return;
        AddQueueRows(QueueAddResults.SelectedItems.OfType<QueueAddRow>().Where(row => !row.IsGroup || row.CanAddAll).OrderBy(row => Array.IndexOf(queueAddRows, row)));
        e.Handled = true;
    }
    private void QueueAddSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseQueueAdd(sender, e); e.Handled = true; }
        else if (e.Key == Key.Down && QueueAddResults.Items.Count > 0)
        {
            QueueAddResults.SelectedIndex = 0; QueueAddResults.UpdateLayout();
            (QueueAddResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
    }
}
