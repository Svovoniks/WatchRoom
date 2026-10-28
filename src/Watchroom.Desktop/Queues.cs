using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private bool QueueEditable => room is null || room.IsConnected && room.Identity?.Host == true;
    // Guests change the host's queues through the host; only the host plays, renames or deletes them.
    private bool QueueChangeable => QueueEditable || ShowingHostQueues;
    private bool CanChangeQueue() => ShowingHostQueues || CanEditQueue();
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
    private Dictionary<string, MediaItem> MediaById()
    {
        var result = new Dictionary<string, MediaItem>(items.Count);
        foreach (var item in items) result.TryAdd(item.Id, item);
        return result;
    }
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
        ScheduleQueueShare();
    }
    private void RefreshQueuePickers()
    {
        if (PlayerQueuePicker is null) return;
        updatingQueues = true;
        var temporary = activeQueueId is null && queue.Count > 0 ? new SavedQueue("temporary", "Current queue", queue.Select(item => item.Id).ToArray()) : null;
        // "No queue" is the Episodes / Off side of the queue menu's switch, so the picker lists only queues.
        var seasonChoice = playingItem is { Series: not null, Season: not null, Episode: not null, IsExtra: false } ? new[] { seasonQueueChoice } : [];
        PlayerQueuePicker.ItemsSource = (temporary is null ? [] : new[] { temporary }).Concat(savedQueues).Concat(seasonChoice).ToArray();
        PlayerQueuePicker.SelectedItem = savedQueues.FirstOrDefault(x => x.Id == activeQueueId) ?? temporary;
        updatingQueues = false;
        RefreshSavedItems();
    }

    // The Queues page shows either the gallery of queues or one opened queue.
    private string? openQueueId;
    private SavedQueue? OpenedQueue => openQueueId is null ? null : QueueSource.FirstOrDefault(x => x.Id == openQueueId);
    private void OpenQueue(string id)
    {
        if (QueueSource.All(x => x.Id != id)) return;
        if (openQueueId != id)
        {
            if (currentPage == "Queues") RememberLocation();
            CloseQueueAddPanel(); SavedQueueItems.SelectedIndex = -1;
            openQueueId = id;
        }
        RefreshSavedItems();
    }
    private void ShowQueueGallery(object sender, RoutedEventArgs e) => ShowAllQueues();
    // A guest in a room edits the host's queues, and the page says so.
    private void RefreshQueuesHeading()
    {
        if (QueuesHeading is null) return;
        QueuesHeading.Text = ShowingHostQueues ? $"{HostName}'s queues" : "Queues";
        QueuesSubheading.Text = ShowingHostQueues ? $"You're in {HostName}'s room, so changes here go to their queues." : "Line up movies and episodes to play back to back.";
    }
    private void ShowAllQueues()
    {
        if (openQueueId is null) return;
        if (currentPage == "Queues") RememberLocation();
        openQueueId = null; RefreshSavedItems();
    }

    private sealed record QueueEntry(int Index, MediaItem? Media)
    {
        public Func<MediaItem, LibraryCardView>? Artwork { get; init; }
        public bool IsCurrent { get; init; }
        public string Number => (Index + 1).ToString("00");
        public string Title => Media is null ? "Video no longer in library" : Media.Episode is null ? Media.Title : Media.EpisodeDisplayTitle;
        public string Caption => Media is null ? "Remove this entry or add the video again." : Media.Caption;
        public string Problem => Media is null ? "Removed" : Media.IsVirtual ? "Missing" : Media.Available ? "" : "Unavailable";
        public bool HasProblem => Problem.Length > 0;
        public Visibility ProblemVisibility => HasProblem ? Visibility.Visible : Visibility.Collapsed;
        public string Runtime => Media?.RuntimeMinutes is int minutes and > 0 ? $"{minutes} min" : "";
        public Visibility NumberVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
        public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        // Artwork loads only for rows on screen.
        public LibraryCardView? View => Media is null || Artwork is null ? null : Artwork(Media);
        public string AccessibleName => Title + (IsCurrent ? ", playing" : "") + (HasProblem ? ", " + Problem : "");
    }
    private void RefreshSavedItems()
    {
        if (SavedQueueItems is null) return;
        RefreshQueuesHeading();
        var saved = OpenedQueue;
        if (saved is null) openQueueId = null;
        QueueGalleryView.Visibility = saved is null ? Visibility.Visible : Visibility.Collapsed;
        QueueDetailView.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;
        if (saved is null)
        {
            CloseQueueAddPanel();
            SavedQueueItems.ItemsSource = Array.Empty<QueueEntry>();
            RefreshQueueGallery();
            UpdateSavedQueueActions();
            return;
        }
        var byId = QueueMedia();
        var current = ShowingHostQueues ? saved.Id == hostQueues!.ActiveId ? hostQueues.ActiveIndex : -1
            : saved.Id == activeQueueId ? SavedEntryIndex(saved, byId, queuePosition) : -1;
        if (current >= saved.MediaIds.Length || !ShowingHostQueues && current >= 0 && playingItem?.Id != saved.MediaIds[current]) current = -1;
        Func<MediaItem, LibraryCardView> artwork = QueueStill;
        var selectedIndex = SavedQueueItems.SelectedIndex;
        SavedQueueItems.ItemsSource = saved.MediaIds.Select((id, index) => new QueueEntry(index, byId.GetValueOrDefault(id)) { Artwork = artwork, IsCurrent = index == current }).ToArray();
        if (selectedIndex >= 0) SavedQueueItems.SelectedIndex = Math.Min(selectedIndex, saved.MediaIds.Length - 1);
        QueueDetailTitle.Text = saved.Name;
        RefreshQueueHero(saved, byId);
        RefreshQueueAddMarks();
        UpdateSavedQueueActions();
    }
    // Maps a position in the playback queue, which omits videos no longer in the library, to its saved entry.
    private static int SavedEntryIndex(SavedQueue saved, Dictionary<string, MediaItem> byId, int playbackIndex)
    {
        if (playbackIndex < 0) return -1;
        for (int index = 0, resolved = 0; index < saved.MediaIds.Length; index++)
            if (byId.ContainsKey(saved.MediaIds[index]) && resolved++ == playbackIndex) return index;
        return -1;
    }
    // The entry Play continues from: the queue's place when it is in use, otherwise its first unwatched video.
    private int QueueUpNext(SavedQueue saved, Dictionary<string, MediaItem> byId)
    {
        bool Ready(int index) => byId.TryGetValue(saved.MediaIds[index], out var item) && item.Available && !item.IsVirtual && !WasWatched(item.Id);
        var start = saved.Id == activeQueueId ? SavedEntryIndex(saved, byId, queuePosition) : -1;
        for (var index = Math.Max(start, 0); start >= 0 && index < saved.MediaIds.Length; index++) if (Ready(index)) return index;
        for (var index = 0; index < saved.MediaIds.Length; index++) if (Ready(index)) return index;
        return -1;
    }
    private static string VideoCount(int count) => count == 1 ? "1 video" : $"{count} videos";
    private void RefreshQueueHero(SavedQueue saved, Dictionary<string, MediaItem> byId)
    {
        var media = saved.MediaIds.Select(id => byId.GetValueOrDefault(id)).OfType<MediaItem>().ToArray();
        var covers = QueueCovers(media, 4);
        var slots = new[] { QueueHeroCover0, QueueHeroCover1, QueueHeroCover2, QueueHeroCover3 };
        for (var index = 0; index < slots.Length; index++) slots[index].DataContext = new QueueCoverSlot(index < covers.Count ? covers[index] : null);
        QueueHeroBackdrop.DataContext = new QueueCoverSlot(covers.FirstOrDefault());
        QueueDetailCaption.Text = QueueSummary(saved, media, QueuesPage.Visibility == Visibility.Visible && !ShowingHostQueues ? library.PlaybackSettings() : null, detailed: true);
    }
    // Watch state comes from one settings read; without it the summary shows only the size of the queue.
    private static string QueueSummary(SavedQueue saved, MediaItem[] media, Dictionary<string, string>? playback, bool detailed)
    {
        var parts = new List<string> { VideoCount(saved.MediaIds.Length) };
        var known = media.Where(item => item.RuntimeMinutes > 0).ToArray();
        if (known.Length > 0) parts.Add(Duration(known.Sum(item => item.RuntimeMinutes!.Value)) + (known.Length < media.Length ? "+" : ""));
        if (!detailed || playback is null) return string.Join(" · ", parts);
        bool Watched(MediaItem item) => playback.TryGetValue("watched:" + item.Id, out var value) && value == "true";
        var watched = media.Count(Watched);
        if (watched > 0)
        {
            parts.Add($"{watched} watched");
            var unwatched = media.Where(item => !Watched(item)).ToArray();
            if (unwatched.Length > 0 && unwatched.All(item => item.RuntimeMinutes > 0))
            {
                var left = unwatched.Sum(item => item.RuntimeMinutes!.Value * 60000L -
                    (playback.TryGetValue("position:" + item.Id, out var position) && long.TryParse(position, out var ms) ? Math.Clamp(ms, 0, item.RuntimeMinutes!.Value * 60000L) : 0));
                parts.Add($"about {Duration((int)Math.Max(1, left / 60000))} left");
            }
        }
        return string.Join(" · ", parts);
    }
    private void SavedQueueSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSavedQueueActions();
    private void UpdateSavedQueueActions()
    {
        if (PlaySavedQueueButton is null || SavedQueueFeedback is null) return;
        var saved = OpenedQueue;
        var canEdit = QueueEditable;
        var byId = saved is null ? null : QueueMedia();
        var playable = saved is not null && saved.MediaIds.Any(id => byId!.TryGetValue(id, out var item) && QueueItemAvailable(item));
        var hasEntries = saved?.MediaIds.Length > 0;
        PlaySavedQueueButton.IsEnabled = canEdit && playable;
        var upNext = saved is null || !playable ? -1 : QueueUpNext(saved, byId!);
        var next = upNext >= 0 ? byId![saved!.MediaIds[upNext]] : null;
        var resume = next is not null && long.TryParse(library.Setting("position:" + next.Id), out var position) && position >= 10000;
        PlaySavedQueueButton.Content = next is null || upNext == 0 && !resume ? "Play"
            : $"{(resume ? "Resume" : "Continue")} · {(next.Episode is { } episode ? $"{next.Series} · S{next.Season ?? 0}E{episode}" : next.Title)}";
        PlayQueueFromStartButton.Visibility = canEdit && next is not null && (upNext > 0 || resume) ? Visibility.Visible : Visibility.Collapsed;
        var selectedEntry = SavedQueueItems.SelectedItem as QueueEntry;
        PlaySelectedSavedQueueButton.IsEnabled = canEdit && selectedEntry is { Media: { } selectedItem } && QueueItemAvailable(selectedItem);
        // A guest editing the host's queues sees no play buttons: the host starts videos for the room.
        var guest = ShowingHostQueues;
        PlaySelectedSavedQueueButton.Visibility = !guest && hasEntries && selectedEntry is not null && selectedEntry.Index != upNext ? Visibility.Visible : Visibility.Collapsed;
        PlaySavedQueueButton.Visibility = !guest && hasEntries ? Visibility.Visible : Visibility.Collapsed;
        PlaySavedQueueButton.Style = (Style)FindResource(playable ? "Primary" : typeof(Button));
        AddQueueVideosButton.Style = (Style)FindResource(playable ? typeof(Button) : "Primary");
        AddQueueVideosButton.IsEnabled = canEdit && saved is not null;
        AddQueueVideosButton.Visibility = QueueMoreButton.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
        QueueMoreButton.IsEnabled = canEdit && saved is not null;
        SavedQueueFeedback.Text = guest && QueueNotice is { } notice ? notice
            : guest ? $"Changes are saved to {HostName}'s queue. To add videos, open a title in {HostName}'s library and choose Add to queue."
            : !canEdit ? "The host controls the queue."
            : saved?.MediaIds.Length is null or 0 ? ""
            : !playable ? "No available videos. Reconnect their drive or add videos from your library." : "";
    }
    private void SelectPlaybackQueue(SavedQueue saved)
    {
        activeQueueId = saved.Id; queue.Clear(); queuePosition = -1;
        library.Setting("lastQueue", saved.Id);
        foreach (var item in ResolveQueue(saved)) queue.Add(item);
        SaveQueues(); RefreshQueuePickers(); PublishQueue();
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
        SaveQueues(); RefreshQueuePickers(); PublishQueue(); RefreshEpisodeEndAction();
    }
    // A new queue opens with Add videos ready, since an empty queue has nothing else to do.
    private void CreateQueue(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(this, "Create queue", "Queue name", action: "Create queue");
        if (name is null) return;
        if (ShowingHostQueues) { SendQueueCreate(name, []); return; }
        var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []);
        savedQueues.Add(saved); SaveQueues(); RefreshQueuePickers();
        if (currentPage != "Queues") ShowPage("Queues");
        OpenQueue(saved.Id);
        if (QueueEditable) AddQueueVideos(sender, e);
    }
    private void RenameQueue(SavedQueue saved)
    {
        var name = Dialogs.Prompt(this, "Rename queue", "Queue name", saved.Name, action: "Save name");
        if (name is null || savedQueues.IndexOf(saved) is not (var index and >= 0)) return;
        savedQueues[index] = saved with { Name = name };
        SaveQueues(); RefreshQueuePickers();
    }
    private void DeleteQueue(SavedQueue saved)
    {
        if (!CanEditQueue() || !savedQueues.Remove(saved)) return;
        if (activeQueueId == saved.Id) SelectNoQueue();
        SaveQueues(); RefreshQueuePickers();
        SetStatus($"Deleted {saved.Name}.");
    }
    private void QueueMoreClick(object sender, RoutedEventArgs e)
    {
        if (OpenedQueue is not { } saved) return;
        var button = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var item in QueueActions(saved, includePlay: false)) menu.Items.Add(item);
        button.ContextMenu = menu; menu.IsOpen = true;
    }
    private IEnumerable<Control> QueueActions(SavedQueue saved, bool includePlay)
    {
        var canEdit = QueueEditable;
        if (includePlay)
        {
            var play = new MenuItem { Header = "Play", IsEnabled = canEdit && saved.MediaIds.Length > 0 }; play.Click += (_, e) => StartQueue(saved, play, e);
            yield return play; yield return new Separator();
        }
        var rename = new MenuItem { Header = "Rename…", IsEnabled = canEdit }; rename.Click += (_, _) => RenameQueue(saved);
        var delete = new MenuItem { Header = "Delete queue", IsEnabled = canEdit }; delete.Click += (_, _) => DeleteQueue(saved);
        yield return rename; yield return delete;
    }
    private async void PlaySavedQueue(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (OpenedQueue is not { } saved) return;
        var next = QueueUpNext(saved, MediaById());
        if (next < 0) StartQueue(saved, sender, e); else await PlaySavedQueueEntry(saved, next);
    });
    private void PlayQueueFromStart(object sender, RoutedEventArgs e) => StartQueue(OpenedQueue, sender, e);
    private void PlayPlayerQueue(object sender, RoutedEventArgs e) => StartQueue(PlayerQueuePicker.SelectedItem as SavedQueue, sender, e);
    private async void PlaySelectedSavedQueue(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (OpenedQueue is { } saved && SavedQueueItems.SelectedItem is QueueEntry entry)
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
        if (e.OriginalSource is not (ListBox or ListBoxItem)) return;
        if (sender == SavedQueueItems && OpenedQueue is { } saved && SavedQueueItems.SelectedItem is QueueEntry entry)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Up or Key.Down)
            {
                if (CanChangeQueue()) MoveSavedEntry(saved, entry.Index, entry.Index + (e.Key == Key.Up ? -1 : 1));
                e.Handled = true; return;
            }
            if (e.Key == Key.Delete) { RemoveSavedEntry(saved, entry); e.Handled = true; return; }
        }
        if (e.Key != Key.Enter) return;
        if (sender == SavedQueueItems) PlaySelectedSavedQueue(sender, e); else PlaySelectedQueued(sender, e);
        e.Handled = true;
    }
    private void QueueItemContextMenu(object sender, ContextMenuEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && node is not ListBoxItem) node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is not ListBoxItem { DataContext: QueueEntry entry } row || OpenedQueue is not { } saved) { e.Handled = true; return; }
        row.IsSelected = true;
        var canEdit = QueueChangeable; var last = saved.MediaIds.Length - 1;
        var menu = new ContextMenu { PlacementTarget = row };
        MenuItem Item(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = canEdit && enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        Item("Play from here", QueueEditable && entry.Media is { } media && QueueItemAvailable(media), () => PlaySelectedSavedQueue(row, new RoutedEventArgs()));
        menu.Items.Add(new Separator());
        Item("Move to top", entry.Index > 0, () => MoveSavedEntry(saved, entry.Index, 0));
        Item("Move up", entry.Index > 0, () => MoveSavedEntry(saved, entry.Index, entry.Index - 1)).InputGestureText = "Ctrl+Up";
        Item("Move down", entry.Index < last, () => MoveSavedEntry(saved, entry.Index, entry.Index + 1)).InputGestureText = "Ctrl+Down";
        Item("Move to bottom", entry.Index < last, () => MoveSavedEntry(saved, entry.Index, last));
        menu.Items.Add(new Separator());
        Item("Remove from queue", true, () => RemoveSavedEntry(saved, entry)).InputGestureText = "Delete";
        SavedQueueItems.ContextMenu = menu;
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
        if (ShowingHostQueues)
        {
            if (QueueSource.Contains(saved) && order.Distinct().Count() == order.Length && order.All(i => i >= 0 && i < saved.MediaIds.Length)) SendQueueOrder(saved, order);
            return;
        }
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
        SaveQueues(); RefreshQueuePickers();
    }
    private void RemoveSavedItem(object sender, RoutedEventArgs e)
    {
        if (OpenedQueue is not { } saved) return;
        var entry = (sender as FrameworkElement)?.Tag as QueueEntry ?? SavedQueueItems.SelectedItem as QueueEntry;
        if (entry is not null) RemoveSavedEntry(saved, entry);
    }
    private void RemoveSavedEntry(SavedQueue saved, QueueEntry entry)
    {
        var selectedIndex = SavedQueueItems.SelectedIndex;
        ApplyQueueOrder(saved, Enumerable.Range(0, saved.MediaIds.Length).Where(i => i != entry.Index).ToArray());
        if (selectedIndex == entry.Index && SavedQueueItems.Items.Count > 0) FocusSavedEntry(Math.Min(selectedIndex, SavedQueueItems.Items.Count - 1));
    }
    private void MoveSavedEntry(SavedQueue saved, int source, int destination)
    {
        if (source < 0 || destination < 0 || source >= saved.MediaIds.Length || destination >= saved.MediaIds.Length || source == destination) return;
        var order = Enumerable.Range(0, saved.MediaIds.Length).ToList(); order.RemoveAt(source); order.Insert(destination, source);
        ApplyQueueOrder(saved, order.ToArray()); FocusSavedEntry(destination);
    }
    // Rebuilding the rows drops keyboard focus; return it to the moved or following entry.
    private void FocusSavedEntry(int index)
    {
        SavedQueueItems.SelectedIndex = index;
        if (!SavedQueueItems.IsKeyboardFocusWithin) return;
        SavedQueueItems.ScrollIntoView(SavedQueueItems.SelectedItem);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (SavedQueueItems.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus());
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
        if (queueDragEntry is not { } entry || OpenedQueue is not { } saved) return;
        var point = e.GetPosition(SavedQueueItems);
        if (Math.Abs(point.X - queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        queueDragEntry = null;
        if (CanChangeQueue())
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
        e.Effects = drag is not null && ReferenceEquals(OpenedQueue, drag.Queue) && QueueChangeable ? DragDropEffects.Move : DragDropEffects.None;
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
        if (e.Data.GetData(typeof(QueueDrag)) is not QueueDrag drag || !ReferenceEquals(OpenedQueue, drag.Queue)) return;
        var insertion = QueueDropLocation(e).Index;
        MoveSavedEntry(drag.Queue, drag.Index, insertion > drag.Index ? insertion - 1 : insertion);
    }
    // Returns how many videos were added; a guest's additions are counted once the host applies them.
    private int AddToSavedQueue(SavedQueue saved, IEnumerable<MediaItem> source)
    {
        if (ShowingHostQueues) { var requested = source.ToArray(); SendQueueAdd(saved, requested); return 0; }
        if (!CanEditQueue()) return 0;
        var added = source.Where(x => x.Available && File.Exists(x.Path)).ToArray();
        if (added.Length == 0) { SetStatus("No available videos to add."); return 0; }
        var index = savedQueues.FindIndex(x => x.Id == saved.Id);
        if (index < 0) return 0;
        saved = savedQueues[index];
        savedQueues[index] = saved with { MediaIds = saved.MediaIds.Concat(added.Select(x => x.Id)).ToArray() };
        if (activeQueueId == saved.Id) { foreach (var item in added) queue.Add(item); PublishQueue(); }
        SaveQueues(); RefreshQueuePickers(); SetStatus($"Added {VideoCount(added.Length)} to {saved.Name}.");
        return added.Length;
    }
    // Each saved queue, then New queue…; the divider shows only when there are queues above it.
    private IEnumerable<Control> QueueChoices(MediaItem[] videos)
    {
        foreach (var saved in QueueSource)
        {
            var entry = new MenuItem { Header = saved.Name }; entry.Click += (_, _) => AddToSavedQueue(saved, videos); yield return entry;
        }
        if (QueueSource.Count > 0) yield return new Separator();
        var create = new MenuItem { Header = "New queue…" }; create.Click += (_, _) => CreateQueueWith(videos);
        yield return create;
    }
    private MenuItem QueueMenu(IEnumerable<MediaItem> source)
    {
        var menu = new MenuItem { Header = "Add to queue", IsEnabled = QueueChangeable };
        foreach (var choice in QueueChoices(source.ToArray())) menu.Items.Add(choice);
        return menu;
    }
    private void CreateQueueWith(MediaItem[] videos)
    {
        if (ShowingHostQueues)
        {
            if (Dialogs.Prompt(this, "New queue", "Queue name", action: "Create and add") is { } hostQueueName) SendQueueCreate(hostQueueName, videos);
            return;
        }
        if (!CanEditQueue()) return;
        if (!videos.Any(QueueItemAvailable)) { SetStatus("No available videos to add."); return; }
        var name = Dialogs.Prompt(this, "New queue", "Queue name", action: "Create and add"); if (name is null) return;
        var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []); savedQueues.Add(saved); AddToSavedQueue(saved, videos);
    }
    // The Add to queue buttons list the queues directly; with none yet, they go straight to naming one.
    private void ShowAddToQueueMenu(FrameworkElement target, IEnumerable<MediaItem> source)
    {
        var videos = source.ToArray();
        if (QueueSource.Count == 0) { CreateQueueWith(videos); return; }
        if (!CanChangeQueue()) return;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var choice in QueueChoices(videos)) menu.Items.Add(choice);
        target.ContextMenu = menu; menu.IsOpen = true;
    }
    private void CardContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (GuestLibrary)
        {
            // A guest can add the host's videos to the host's queues from a card.
            if (!ShowingHostQueues || sender is not Button { Tag: LibraryCard guestCard } guestButton || guestCard.Level is "series" or "season") { e.Handled = true; return; }
            var guestMenu = new ContextMenu { PlacementTarget = guestButton };
            guestMenu.Items.Add(QueueMenu([guestCard.Media]));
            guestButton.ContextMenu = guestMenu; guestMenu.IsOpen = true; e.Handled = true; return;
        }
        if (sender is not Button button || button.Tag is not LibraryCard card) return;
        var videos = LibraryCatalog.CardItems(items, card, displaySpecialsWithinSeasons, PreferredVersion(card.Media)).ToArray();
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
        if (AcquireMenu(card) is { } acquire) menu.Items.Add(acquire);
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
                var versions = LibraryCatalog.Versions(items, card.Media);
                if (versions.Length > 1)
                {
                    var versionMenu = new MenuItem { Header = "Choose version" };
                    foreach (var version in versions)
                    {
                        var label = LibraryCatalog.VersionLabel(version, versions);
                        var choice = new MenuItem { Header = label + (version.Available ? "" : " · Unavailable"), IsCheckable = true, IsChecked = version.Id == card.Media.Id, IsEnabled = version.Available, ToolTip = version.Path };
                        choice.Click += (_, _) => ChooseVersion(version, label); versionMenu.Items.Add(choice);
                    }
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
