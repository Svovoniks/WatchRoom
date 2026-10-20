using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    // Starts a queue from the playing season; it appears at the end of the player's queue picker.
    private static readonly SavedQueue seasonQueueChoice = new("new-season", "+ New queue from this season", []);
    private bool CanEditQueue()
    {
        if (room is not null && (!room.IsConnected || room.Identity?.Host != true))
        { SetStatus("The host controls the queue. Rejoin the room if disconnected."); return false; }
        return true;
    }
    private static bool QueueItemAvailable(MediaItem item) => item.Available && File.Exists(item.Path);
    // The playback tick enables Previous and Next by scanning for the nearest playable file. With a drive offline that
    // can mean hundreds of disk checks per tick, so the tick reuses answers for a few seconds; playing still checks the disk.
    private readonly Dictionary<string, (bool Exists, long CheckedAt)> recentFileChecks = new(StringComparer.OrdinalIgnoreCase);
    private bool RecentlyExists(string path)
    {
        var now = Environment.TickCount64;
        if (recentFileChecks.TryGetValue(path, out var known) && now - known.CheckedAt < 5000) return known.Exists;
        if (recentFileChecks.Count > 4096) recentFileChecks.Clear();
        var exists = File.Exists(path); recentFileChecks[path] = (exists, now); return exists;
    }
    private IEnumerable<MediaItem> ResolveQueue(SavedQueue saved) => saved.MediaIds.Select(id => items.FirstOrDefault(x => x.Id == id)).OfType<MediaItem>();
    private void SaveActiveQueueEdits()
    {
        var index = savedQueues.FindIndex(x => x.Id == activeQueueId);
        if (index >= 0) savedQueues[index] = savedQueues[index] with { MediaIds = queue.Select(x => x.Id).ToArray() };
        SaveQueues(); RefreshQueuePickers();
    }
    private void SaveQueues()
    {
        library.Setting("queues", Wire.Serialize(savedQueues));
        library.Setting("activeQueue", activeQueueId ?? "");
    }
    private void RefreshQueuePickers(string? selectedId = null)
    {
        if (QueuePicker is null || PlayerQueuePicker is null) return;
        selectedId ??= (QueuePicker.SelectedItem as SavedQueue)?.Id ?? activeQueueId;
        updatingQueues = true;
        QueuePicker.ItemsSource = savedQueues.ToArray();
        QueuePicker.SelectedItem = savedQueues.FirstOrDefault(x => x.Id == selectedId) ?? savedQueues.FirstOrDefault();
        var temporary = activeQueueId is null && queue.Count > 0 ? new SavedQueue("temporary", "Current queue", queue.Select(item => item.Id).ToArray()) : null;
        // "No queue" is the Episodes / Off side of the queue menu's switch, so the picker lists only queues.
        var seasonChoice = playingItem is { Series: not null, Season: not null, Episode: not null, IsExtra: false } ? new[] { seasonQueueChoice } : [];
        PlayerQueuePicker.ItemsSource = (temporary is null ? [] : new[] { temporary }).Concat(savedQueues).Concat(seasonChoice).ToArray();
        PlayerQueuePicker.SelectedItem = savedQueues.FirstOrDefault(x => x.Id == activeQueueId) ?? temporary;
        updatingQueues = false;
        RefreshSavedItems();
    }
    private sealed record QueueEntry(int Index, MediaItem? Media)
    {
        public string Number => (Index + 1).ToString("00");
        public string Title => Media is null ? "Video no longer in library" : Media.Episode is null ? Media.Title : Media.EpisodeDisplayTitle;
        public string Caption => Media is null ? "Remove this entry or add the video again." : Media.Caption + (Media.Available ? "" : " · Unavailable");
    }
    private void RefreshSavedItems()
    {
        var saved = QueuePicker.SelectedItem as SavedQueue;
        SavedQueueItems.ItemsSource = saved?.MediaIds.Select((id, index) => new QueueEntry(index, items.FirstOrDefault(x => x.Id == id))).ToArray() ?? [];
        QueueDetailTitle.Text = saved?.Name ?? "Choose a queue";
        QueueDetailCaption.Text = saved is null ? "Create a queue to get started." : $"{saved.MediaIds.Length} videos";
        UpdateSavedQueueActions();
    }
    private void SavedQueueSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSavedQueueActions();
    private void UpdateSavedQueueActions()
    {
        if (PlaySavedQueueButton is null || SavedQueueEarlier is null || SavedQueueFeedback is null) return;
        var saved = QueuePicker.SelectedItem as SavedQueue;
        var canEdit = room is null || room.IsConnected && room.Identity?.Host == true;
        var playable = saved is not null && ResolveQueue(saved).Any(x => x.Available && File.Exists(x.Path));
        PlaySavedQueueButton.IsEnabled = canEdit && playable;
        PlaySelectedSavedQueueButton.IsEnabled = canEdit && SavedQueueItems.SelectedItem is QueueEntry { Media: { } selectedItem } && QueueItemAvailable(selectedItem);
        var hasEntries = saved?.MediaIds.Length > 0;
        PlaySavedQueueButton.Visibility = PlaySelectedSavedQueueButton.Visibility =
            SavedQueueEarlier.Visibility = SavedQueueLater.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
        PlaySavedQueueButton.Style = (Style)FindResource(playable ? "Primary" : typeof(Button));
        AddQueueVideosButton.Style = (Style)FindResource(playable ? typeof(Button) : "Primary");
        AddQueueVideosButton.IsEnabled = canEdit && saved is not null;
        var index = SavedQueueItems.SelectedIndex;
        SavedQueueEarlier.IsEnabled = canEdit && index > 0;
        SavedQueueLater.IsEnabled = canEdit && index >= 0 && index < (saved?.MediaIds.Length ?? 0) - 1;
        SavedQueueFeedback.Text = !canEdit ? "The host controls the queue."
            : saved?.MediaIds.Length == 0 ? ""
            : !playable ? "No available videos. Reconnect their drive or add videos from your library." : "";
    }
    private void SavedQueueChanged(object sender, SelectionChangedEventArgs e) { if (!updatingQueues && SavedQueueItems is not null) RefreshSavedItems(); }
    private void SelectPlaybackQueue(SavedQueue saved)
    {
        activeQueueId = saved.Id; queue.Clear(); queuePosition = -1;
        library.Setting("lastQueue", saved.Id);
        foreach (var item in ResolveQueue(saved)) queue.Add(item);
        SaveQueues(); RefreshQueuePickers(saved.Id); PublishQueue();
    }
    // Switching to a queue keeps the current video playing. When the queue contains it, the queue
    // continues from that point; otherwise Next starts the queue from its first video.
    private void SwitchToQueue(SavedQueue saved)
    {
        SelectPlaybackQueue(saved);
        if (playingItem is { } playing && queue.FindIndex(item => item.Id == playing.Id) is var index and >= 0) { queuePosition = index; PublishQueue(); }
        RefreshEpisodeEndAction();
    }
    private void PlayerQueueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingQueues || PlayerQueuePicker.SelectedItem is not SavedQueue saved) return;
        if (saved.Id == "temporary") return;
        if (!CanEditQueue()) { RefreshQueuePickers(); return; }
        if (saved == seasonQueueChoice) CreateSeasonQueue(); else SwitchToQueue(saved);
    }
    private void CreateSeasonQueue()
    {
        if (playingItem is not { Series: not null, Season: not null } playing) { RefreshQueuePickers(); return; }
        var episodes = EpisodePlayback.Episodes(items, playing).Where(item => item.Season == playing.Season).ToArray();
        if (episodes.Length == 0) episodes = [playing];
        var saved = new SavedQueue(Guid.NewGuid().ToString("N"), $"{playing.Series} · Season {playing.Season}", episodes.Select(item => item.Id).ToArray());
        savedQueues.Add(saved); SwitchToQueue(saved);
        SetStatus($"Created {saved.Name}.");
    }
    private void SelectNoQueue()
    {
        SaveActiveQueueEdits();
        activeQueueId = null; queue.Clear(); queuePosition = -1; queueModeRequested = false;
        if (roomLibrary?.Queue.Entries.Length > 0) roomLibrary.Request(HostPeerId(), "clear");
        SaveQueues(); RefreshQueuePickers(); PublishQueue(); RefreshEpisodeEndAction();
    }
    private void CreateQueue(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(this, "Create queue", "Queue name", action: "Create queue");
        if (name is null) return;
        var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []);
        savedQueues.Add(saved); SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void RenameQueue(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved) return;
        var name = Dialogs.Prompt(this, "Rename queue", "Queue name", saved.Name, action: "Save name");
        if (name is null) return;
        savedQueues[savedQueues.IndexOf(saved)] = saved with { Name = name };
        SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void DeleteQueue(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved || !CanEditQueue()) return;
        savedQueues.Remove(saved);
        if (savedQueues.Count == 0) savedQueues.Add(new(Guid.NewGuid().ToString("N"), "Default queue", []));
        if (activeQueueId == saved.Id) SelectNoQueue();
        SaveQueues(); RefreshQueuePickers();
    }
    private void PlaySavedQueue(object sender, RoutedEventArgs e) => StartQueue(QueuePicker.SelectedItem as SavedQueue, sender, e);
    private void PlayPlayerQueue(object sender, RoutedEventArgs e) => StartQueue(PlayerQueuePicker.SelectedItem as SavedQueue, sender, e);
    private async void PlaySelectedSavedQueue(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (QueuePicker.SelectedItem is SavedQueue saved && SavedQueueItems.SelectedItem is QueueEntry entry)
            await PlaySavedQueueEntry(saved, entry.Index);
    });
    private async Task PlaySavedQueueEntry(SavedQueue saved, int index)
    {
        if (!CanEditQueue() || !savedQueues.Contains(saved) || index < 0 || index >= saved.MediaIds.Length) return;
        var item = items.FirstOrDefault(x => x.Id == saved.MediaIds[index]);
        if (item is null || !QueueItemAvailable(item)) { SetStatus("This queue video is unavailable."); return; }
        // Missing library references are omitted from the playback queue. Count
        // occurrences before the clicked row so duplicate videos keep their place.
        var playbackIndex = saved.MediaIds.Take(index).Count(id => items.Any(x => x.Id == id));
        SelectPlaybackQueue(saved);
        await PlayQueueAt(playbackIndex);
    }
    private async void PlaySelectedQueued(object sender, RoutedEventArgs e) =>
        await Guard(() => QueueList.SelectedItem is EpisodeEntry entry ? PlayListedEpisode(entry.Media) : PlayQueueAt(QueueList.SelectedIndex));

    // Without a queue, the queue menu lists the playing show's episodes so another one is a double-click away.
    private sealed record EpisodeEntry(MediaItem Media)
    {
        public string QueueTitle => $"S{Media.Season} · {Media.EpisodeDisplayTitle}";
    }
    private (string Id, List<MediaItem> Library)? episodeListKey;
    private MediaItem[] listedEpisodes = [];
    private bool ShowingEpisodeList => episodeListKey is not null;
    private void RefreshQueueList(bool force = false)
    {
        var current = !HasPlaybackQueue && !queueModeRequested && (room is null || room.Identity?.Host == true) &&
            playingItem is { Series: not null, Season: not null, Episode: not null, IsExtra: false } playing ? playing : null;
        (string, List<MediaItem>)? key = current is null ? null : (current.Id, items);
        if (!force && key == episodeListKey) return;
        var wasShowing = ShowingEpisodeList; episodeListKey = key;
        if (current is null)
        {
            if (!force && !wasShowing) return;
            listedEpisodes = [];
            QueueList.ItemsSource = queue.ToArray(); QueueList.SelectedIndex = queuePosition;
            PlayPlayerQueueButton.ClearValue(VisibilityProperty);
            if (!force) QueueStatus.Text = "Queue is empty.";
            return;
        }
        var episodes = EpisodePlayback.Episodes(items, current);
        // Library refreshes replace the item list; keep the rows (and scroll position) when the episodes are the same.
        if (!wasShowing || !episodes.Select(item => item.Id).SequenceEqual(listedEpisodes.Select(item => item.Id)))
            QueueList.ItemsSource = episodes.Select(item => new EpisodeEntry(item)).ToArray();
        listedEpisodes = episodes;
        QueueList.SelectedIndex = Array.FindIndex(episodes, item => item.Id == current.Id) is var index and >= 0 ? index
            : Array.FindIndex(episodes, item => item.Season == current.Season && item.Episode == current.Episode);
        QueueStatus.Text = current.Season is { } season ? $"{current.Series} · Season {season} · plays in order" : $"{current.Series} · plays in order";
        PlayPlayerQueueButton.Visibility = Visibility.Collapsed;
        // Rows mark the playing episode, so redraw them when it changes.
        QueueList.Items.Refresh();
    }
    // Previous and next step through the listed episodes when no queue is loaded.
    private MediaItem? AdjacentEpisode(int direction)
    {
        if (!ShowingEpisodeList) return null;
        var current = Array.FindIndex(listedEpisodes, item => item.Id == playingItem?.Id);
        if (current < 0) current = QueueList.SelectedIndex;
        if (current < 0) return null;
        for (var index = current + direction; index >= 0 && index < listedEpisodes.Length; index += direction)
            if (listedEpisodes[index].Available && RecentlyExists(listedEpisodes[index].Path)) return listedEpisodes[index];
        return null;
    }
    private async Task PlayListedEpisode(MediaItem item)
    {
        if (!CanEditQueue()) return;
        if (!QueueItemAvailable(item)) { SetStatus("This episode is unavailable. Reconnect its drive and rescan."); return; }
        selected = item;
        await PlayFollowingEpisode(item);
        UpdateSessionControls();
    }
    private async Task PlayQueueAt(int index)
    {
        if (!CanEditQueue() || index < 0 || index >= queue.Count) return;
        var item = queue[index];
        if (!QueueItemAvailable(item)) { SetStatus("This queue video is unavailable."); return; }
        if (room is null) await PlayLocalItem(item);
        else { StartHostedVideo(item); ShowPage("Room"); }
        queuePosition = index; selected = item;
        PublishQueue();
    }
    private void QueueItemDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && node is not ListBoxItem)
        {
            if (node is ButtonBase || node is FrameworkElement { Cursor: { } cursor } && cursor == Cursors.SizeAll) return;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (node is not ListBoxItem) return;
        if (list == SavedQueueItems) PlaySelectedSavedQueue(sender, e); else PlaySelectedQueued(sender, e);
        e.Handled = true;
    }
    private void QueueItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not (ListBox or ListBoxItem)) return;
        if (sender == SavedQueueItems) PlaySelectedSavedQueue(sender, e); else PlaySelectedQueued(sender, e);
        e.Handled = true;
    }
    private void StartQueue(SavedQueue? saved, object sender, RoutedEventArgs e)
    {
        if (saved is null || saved == seasonQueueChoice || !CanEditQueue()) return;
        if (!ResolveQueue(saved).Any(x => x.Available && File.Exists(x.Path)))
        { UpdateSavedQueueActions(); SetStatus("This queue has no available videos."); return; }
        SelectPlaybackQueue(saved); NextQueued(sender, e);
    }
    private void ApplyQueueOrder(SavedQueue saved, int[] order)
    {
        if (!CanEditQueue() || !savedQueues.Contains(saved) || order.Distinct().Count() != order.Length || order.Any(i => i < 0 || i >= saved.MediaIds.Length)) return;
        var resolvedIndices = Enumerable.Range(0, saved.MediaIds.Length).Where(i => items.Any(x => x.Id == saved.MediaIds[i])).ToArray();
        var cursor = queuePosition >= 0 && queuePosition < resolvedIndices.Length ? resolvedIndices[queuePosition] : -1;
        var updated = saved with { MediaIds = order.Select(i => saved.MediaIds[i]).ToArray() };
        savedQueues[savedQueues.IndexOf(saved)] = updated;
        if (activeQueueId == saved.Id)
        {
            queue.Clear(); queue.AddRange(ResolveQueue(updated));
            var resolvedOrder = order.Where(i => items.Any(x => x.Id == saved.MediaIds[i])).ToArray();
            queuePosition = cursor < 0 ? -1 : Array.IndexOf(resolvedOrder, cursor);
            if (cursor >= 0 && queuePosition < 0) queuePosition = resolvedOrder.Count(i => i < cursor) - 1;
            PublishQueue();
        }
        SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void RemoveSavedItem(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved) return;
        var entry = (sender as FrameworkElement)?.Tag as QueueEntry ?? SavedQueueItems.SelectedItem as QueueEntry;
        if (entry is null) return;
        ApplyQueueOrder(saved, Enumerable.Range(0, saved.MediaIds.Length).Where(i => i != entry.Index).ToArray());
    }
    private void EditQueueOrder(object sender, RoutedEventArgs e)
    {
        if (!CanEditQueue() || QueuePicker.SelectedItem is not SavedQueue saved) return;
        var index = SavedQueueItems.SelectedIndex;
        var destination = index + ((string)((Button)sender).Tag == "Earlier" ? -1 : 1);
        if (index < 0 || destination < 0 || destination >= saved.MediaIds.Length) return;
        MoveSavedEntry(saved, index, destination);
    }
    private void MoveSavedEntry(SavedQueue saved, int source, int destination)
    {
        if (source < 0 || destination < 0 || source >= saved.MediaIds.Length || destination >= saved.MediaIds.Length || source == destination) return;
        var order = Enumerable.Range(0, saved.MediaIds.Length).ToList(); order.RemoveAt(source); order.Insert(destination, source);
        ApplyQueueOrder(saved, order.ToArray()); SavedQueueItems.SelectedIndex = destination;
    }
    private sealed record QueueDrag(SavedQueue Queue, int Index);
    private Point queueDragStart;
    private QueueEntry? queueDragEntry;
    private void QueueDragDown(object sender, MouseButtonEventArgs e)
    {
        queueDragEntry = (sender as FrameworkElement)?.DataContext as QueueEntry;
        queueDragStart = e.GetPosition(SavedQueueItems);
    }
    private void QueueDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { queueDragEntry = null; return; }
        if (queueDragEntry is not { } entry || QueuePicker.SelectedItem is not SavedQueue saved) return;
        var point = e.GetPosition(SavedQueueItems);
        if (Math.Abs(point.X - queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        queueDragEntry = null;
        if (CanEditQueue())
        {
            try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(QueueDrag), new QueueDrag(saved, entry.Index)), DragDropEffects.Move); }
            finally { QueueDropMarker.Visibility = Visibility.Collapsed; }
        }
    }
    private (int Index, double Y) QueueDropLocation(DragEventArgs e)
    {
        var pointer = e.GetPosition(SavedQueueItems).Y;
        var result = (Index: SavedQueueItems.Items.Count, Y: 0d);
        for (var i = 0; i < SavedQueueItems.Items.Count; i++)
        {
            if (SavedQueueItems.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem card) continue;
            var top = card.TranslatePoint(new Point(), SavedQueueItems).Y;
            if (pointer < top + card.ActualHeight / 2) return (i, top);
            result = (i + 1, top + card.ActualHeight);
        }
        return result;
    }
    private void QueueDragOver(object sender, DragEventArgs e)
    {
        var drag = e.Data.GetData(typeof(QueueDrag)) as QueueDrag;
        e.Effects = drag is not null && ReferenceEquals(QueuePicker.SelectedItem, drag.Queue) && room?.Identity?.Host != false ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        QueueDropMarker.Visibility = e.Effects == DragDropEffects.Move ? Visibility.Visible : Visibility.Collapsed;
        var markerY = QueueDropLocation(e).Y;
        QueueDropMarker.Margin = new Thickness(8, Math.Clamp(markerY, 0, Math.Max(0, SavedQueueItems.ActualHeight - 4)), 28, 0);
        var point = e.GetPosition(SavedQueueItems);
        if (FindQueueScroll(SavedQueueItems) is { } scroll)
        {
            if (point.Y < 32) scroll.LineUp(); else if (point.Y > SavedQueueItems.ActualHeight - 32) scroll.LineDown();
        }
    }
    private void QueueDragLeave(object sender, DragEventArgs e) => QueueDropMarker.Visibility = Visibility.Collapsed;
    private static ScrollViewer? FindQueueScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer scroll) return scroll; if (FindQueueScroll(child) is { } found) return found; }
        return null;
    }
    private void QueueDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        QueueDropMarker.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(typeof(QueueDrag)) is not QueueDrag drag || !ReferenceEquals(QueuePicker.SelectedItem, drag.Queue)) return;
        var insertion = QueueDropLocation(e).Index;
        MoveSavedEntry(drag.Queue, drag.Index, insertion > drag.Index ? insertion - 1 : insertion);
    }
    private async void AddQueueVideos(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved || !CanEditQueue()) return;
        // Checking every file can block on a sleeping or network drive, so it runs off the UI thread.
        var library = items;
        var available = await Task.Run(() => library.Where(x => x.Available && File.Exists(x.Path)).ToArray());
        if (closing || !ReferenceEquals(QueuePicker.SelectedItem, saved) || !CanEditQueue()) return;
        if (available.Length == 0)
        {
            ShowPage("Folders");
            SetStatus("Add or reconnect a video folder, then return to Queues to add videos.");
            return;
        }
        var win = new Window { Owner = this, Title = "Add videos", Width = 650, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var add = new Button { Content = "Add selected", IsDefault = true, IsEnabled = false }; DockPanel.SetDock(add, Dock.Bottom); panel.Children.Add(add);
        var list = new ListBox { ItemsSource = available, DisplayMemberPath = "QueueTitle", SelectionMode = SelectionMode.Extended }; panel.Children.Add(list);
        // The whole library can be listed here; realize only the visible rows.
        ScrollViewer.SetCanContentScroll(list, true); VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItems.Count > 0;
        add.Click += (_, _) => { if (list.SelectedItems.Count > 0) win.DialogResult = true; };
        win.Content = panel;
        if (win.ShowDialog() == true) AddToSavedQueue(saved, list.SelectedItems.Cast<MediaItem>());
    });
    private void AddToSavedQueue(SavedQueue saved, IEnumerable<MediaItem> source)
    {
        if (!CanEditQueue()) return;
        var added = source.Where(x => x.Available && File.Exists(x.Path)).ToArray();
        if (added.Length == 0) { SetStatus("No available videos to add."); return; }
        savedQueues[savedQueues.IndexOf(saved)] = saved with { MediaIds = saved.MediaIds.Concat(added.Select(x => x.Id)).ToArray() };
        if (activeQueueId == saved.Id) { foreach (var item in added) queue.Add(item); PublishQueue(); }
        SaveQueues(); RefreshQueuePickers(saved.Id); SetStatus($"Added {added.Length} videos to {saved.Name}.");
    }
    private MenuItem QueueMenu(IEnumerable<MediaItem> source)
    {
        var videos = source.ToArray();
        var menu = new MenuItem { Header = "Add to queue", IsEnabled = room?.Identity?.Host != false };
        foreach (var saved in savedQueues)
        {
            var entry = new MenuItem { Header = saved.Name }; entry.Click += (_, _) => AddToSavedQueue(saved, videos); menu.Items.Add(entry);
        }
        var create = new MenuItem { Header = "New queue…" };
        create.Click += (_, _) =>
        {
            var name = Dialogs.Prompt(this, "Create queue", "Queue name", action: "Create queue"); if (name is null) return;
            var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []); savedQueues.Add(saved); AddToSavedQueue(saved, videos);
        };
        menu.Items.Add(new Separator()); menu.Items.Add(create); return menu;
    }
    private void ShowAddToQueueMenu(FrameworkElement target, IEnumerable<MediaItem> source)
    {
        var menu = new ContextMenu { PlacementTarget = target }; menu.Items.Add(QueueMenu(source)); target.ContextMenu = menu; menu.IsOpen = true;
    }
    private void CardContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (GuestLibrary) { e.Handled = true; return; }
        if (sender is not Button button || button.Tag is not LibraryCard card) return;
        var videos = LibraryCatalog.CardItems(items, card, displaySpecialsWithinSeasons).ToArray();
        var menu = new ContextMenu { PlacementTarget = button };
        var open = new MenuItem { Header = card.Level is "series" or "season" ? "Open" : "Details" }; open.Click += (_, _) => SelectMovie(button, new RoutedEventArgs()); menu.Items.Add(open);
        var play = new MenuItem { Header = card.Level is "series" or "season" ? "Play all" : "Play", IsEnabled = room?.Identity?.Host != false && videos.Any(x => x.Available && File.Exists(x.Path)) };
        play.Click += (_, _) =>
        {
            if (!CanEditQueue()) return;
            queue.Clear(); queuePosition = -1; activeQueueId = null; foreach (var video in videos.Where(x => x.Available && File.Exists(x.Path))) queue.Add(video);
            PublishQueue(); RefreshQueuePickers(); NextQueued(button, new RoutedEventArgs());
        };
        menu.Items.Add(play); menu.Items.Add(QueueMenu(videos));
        var unavailable = UnavailableCardItems(card).ToArray();
        if (unavailable.Length > 0)
        {
            var remove = new MenuItem { Header = card.Level is "series" or "season" ? "Remove unavailable videos from library" : "Remove from library", IsEnabled = !scanning };
            remove.Click += async (_, _) => await Guard(() => RemoveUnavailableItems(unavailable));
            menu.Items.Add(new Separator()); menu.Items.Add(remove);
        }
        var members = items.Where(x => x.Id == card.Media.Id || card.Level is "series" or "season" && LibraryIdentity.SameShow(x, card.Media)).ToArray();
        var locked = new MenuItem { Header = "Lock metadata", IsCheckable = true, IsChecked = members.All(x => x.MetadataLocked) };
        locked.Click += (_, _) => { foreach (var item in members) library.Save(item with { MetadataLocked = locked.IsChecked }); _ = Guard(RefreshLibrary); };
        menu.Items.Add(locked);
        if (card.Media.Series is not null)
        {
            var ordering = new MenuItem { Header = "Episode numbering" };
            foreach (var order in new[] { "aired", "absolute", "dvd" })
            {
                var choice = new MenuItem { Header = order == "dvd" ? "DVD order (TMDB)" : order == "absolute" ? "Absolute numbering" : "Aired order", IsCheckable = true, IsChecked = card.Media.NumberingOrder == order };
                choice.Click += async (_, _) => await Guard(async () =>
                {
                    foreach (var item in items.Where(x => LibraryIdentity.SameShow(x, card.Media)).ToArray()) library.Save(item with { NumberingOrder = order, MetadataFetchedAt = 0 });
                    await FetchArtwork(lifetime.Token, true); await RefreshLibrary();
                });
                ordering.Items.Add(choice);
            }
            menu.Items.Add(ordering);
        }
        if (card.Level is "movie" or "episode")
        {
            if (card.Level == "episode" && card.Media.Episode is not null)
            {
                var versions = items.Where(x => LibraryIdentity.SameShow(x, card.Media) && x.Season == card.Media.Season && x.Episode == card.Media.Episode && x.EpisodeEnd == card.Media.EpisodeEnd && !x.IsVirtual).ToArray();
                if (versions.Length > 1)
                {
                    var versionMenu = new MenuItem { Header = "Choose version" };
                    foreach (var version in versions) { var choice = new MenuItem { Header = Path.GetFileName(version.Path) }; choice.Click += (_, _) => Select(version); versionMenu.Items.Add(choice); }
                    menu.Items.Add(versionMenu);
                }
            }
            if (!card.Media.IsVirtual)
            {
                var export = new MenuItem { Header = "Export NFO metadata" }; export.Click += async (_, _) => await Guard(() =>
                { LocalMetadata.Export(card.Media); SetStatus("NFO metadata saved beside the video."); return Task.CompletedTask; }); menu.Items.Add(export);
            }
            var copy = new MenuItem { Header = "Copy file path" }; copy.Click += (_, _) => { try { Clipboard.SetText(card.Media.Path); } catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); } }; menu.Items.Add(copy);
        }
        button.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void ExcludedSelectionChanged(object sender, SelectionChangedEventArgs e) { if (RestoreFolderButton is not null) RestoreFolderButton.IsEnabled = !scanning && ExcludedFolderList.SelectedItem is string; }
    private void ExcludeFolder(object sender, RoutedEventArgs e)
    {
        if (scanning) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Exclude folders from the library", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames.Where(path => !exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))) exclusions.Add(path);
        SaveExclusions();
    }
    private void RestoreFolder(object sender, RoutedEventArgs e)
    {
        if (scanning || ExcludedFolderList.SelectedItem is not string path) return;
        exclusions.Remove(path); SaveExclusions();
    }
    private void SaveExclusions()
    {
        library.Setting("exclusions", Wire.Serialize(exclusions)); scanDelay.Stop(); scanDelay.Start();
        _ = Guard(async () => { await RefreshLibrary(); SetStatus("Folder exclusions updated."); });
    }
}
