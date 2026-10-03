using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow : Window
{
    private sealed record TrackChoice(int Id, string Name)
    {
        public override string ToString() => Name;
    }
    private readonly LibraryStore library;
    private readonly ConcurrentDictionary<string, MediaItem> libraryUpdates = new();
    private readonly ObservableCollection<LibraryCardView> libraryCards = [];
    private readonly EpisodeArtwork episodeArtwork = new();
    private readonly HashSet<string> episodeArtworkRequests = [];
    private bool libraryViewDirty;
    private string? detailPosterPath;
    private int detailPosterGeneration;
    private string? pendingScanStatus;
    private sealed class ScanProgressSink(MainWindow window) : IProgress<string>
    {
        public void Report(string value) => Interlocked.Exchange(ref window.pendingScanStatus, value);
    }
    private readonly DispatcherTimer libraryUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<LibraryFolder> folders = [];
    private readonly ObservableCollection<string> chat = [];
    private readonly Dictionary<string, Guid> pendingAdmissions = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly List<MediaItem> queue = [];
    private int queuePosition = -1;
    private readonly List<SavedQueue> savedQueues = [];
    private readonly ObservableCollection<string> exclusions = [];
    private string? activeQueueId;
    private bool updatingQueues;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer videoClickDelay = new();
    private int videoClickGeneration;
    private readonly DispatcherTimer scanDelay = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim loading = new(1);
    private readonly SemaphoreSlim librarySearchGate = new(1);
    private CancellationTokenSource? librarySearchCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private List<MediaItem> items = [];
    private MediaItem? selected;
    private MediaItem? playingItem;
    private bool roomBusy, preparing;
    private CancellationTokenSource? scanCancellation;
    private long localResume;
    private LibVLC? vlc;
    private LibVLCSharp.Shared.MediaPlayer? player;
    private RoomClient? room;
    private MediaBridge? bridge;
    private PlaybackState? target;
    private string? loadedMedia;
    private bool scanning, seeking, tracksUpdating, ready, closing, fullscreen, light;
    private long tick, appliedRevision = -1;
    private readonly PlaybackSettling settling = new();
    private readonly PlaybackPause playbackPause = new();
    private readonly PlaybackPosition playbackPosition = new();
    private readonly PlaybackRateControl playbackRate = new();
    private PlaybackBuffering buffering = new();
    private int loadGeneration;
    private long? pendingSeek;
    private long seekExpires, seekRevision;
    private WindowState previousState;
    private Rect previousBounds;
    private ResizeMode previousResizeMode;
    private bool roomPanelVisible;
    private double roomPanelWidth = 340;
    private long lastPlayerInteraction;
    private Point lastPlayerMouse;
    private string? browseSeries, browseKind;
    private int? browseSeason;
    private int libraryCategory;
    private IReadOnlyList<LibraryBreadcrumb> breadcrumbItems = [];
    private bool artworkBusy;
    private bool displaySpecialsWithinSeasons;

    public MainWindow()
    {
        InitializeComponent();
        RoomPage.SizeChanged += (_, _) => PlayerOptionsScroll.MaxHeight = Math.Clamp(RoomPage.ActualHeight / 3, 120, 260);
        InitializeBackNavigation();
        videoClickDelay.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime());
        videoClickDelay.Tick += (_, _) =>
        {
            videoClickDelay.Stop();
            if (!closing && RoomPage.Visibility == Visibility.Visible && videoClickGeneration == loadGeneration)
                TogglePlayback(this, new RoutedEventArgs());
        };
        // Keep the initial window inside the usable desktop on smaller displays.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        library = new LibraryStore(App.DataDirectory);
        InitializeUpdates();
        light = library.Setting("theme") == "light";
        ApplyTheme();
        ShowPage("Library");
        foreach (var f in Wire.Read<List<LibraryFolder>>(library.Setting("folders") ?? "[]")) folders.Add(f);
        FolderList.ItemsSource = folders; ChatList.ItemsSource = chat;
        DisplayNameBox.Text = library.Setting("name") ?? Environment.UserName;
        ServerBox.Text = library.Setting("server") ?? "https://watchroom-rooms.svovoniks.chatgpt.site";
        FfmpegBox.Text = library.Setting("ffmpeg") ?? "";
        try
        {
            MetadataToken.Password = MetadataCredential.Load(App.DataDirectory);
            MetadataKeyStatus.Text = MetadataToken.Password.Length > 0 ? "Saved key loaded." : "";
        }
        catch (Win32Exception) { SetStatus("Could not read the saved TMDB token. Enter it again in Settings."); }
        AutoArtwork.IsChecked = library.Setting("artwork") != "false";
        var metadataOptions = MetadataOptions.Load(library);
        MetadataProviders.Text = string.Join(", ", metadataOptions.ProviderOrder);
        MetadataLanguage.Text = metadataOptions.Language; MetadataCountry.Text = metadataOptions.Country;
        MetadataRefreshDays.Text = metadataOptions.RefreshDays.ToString();
        ImportMissing.IsChecked = metadataOptions.ImportMissing; ImportUpcoming.IsChecked = metadataOptions.ImportUpcoming;
        SaveNfo.IsChecked = metadataOptions.SaveNfo; DisplaySpecials.IsChecked = metadataOptions.DisplaySpecialsWithinSeasons;
        GroupShows.IsChecked = metadataOptions.GroupShowsByProvider;
        displaySpecialsWithinSeasons = metadataOptions.DisplaySpecialsWithinSeasons;
        foreach (var path in Wire.Read<string[]>(library.Setting("exclusions") ?? "[]")) exclusions.Add(path);
        ExcludedFolderList.ItemsSource = exclusions;
        try { savedQueues.AddRange(Wire.Read<List<SavedQueue>>(library.Setting("queues") ?? "[]")); }
        catch (System.Text.Json.JsonException) { }
        if (savedQueues.Count == 0)
        {
            string[] legacyIds = [];
            try { legacyIds = Wire.Read<string[]>(library.Setting("queue") ?? "[]"); } catch (System.Text.Json.JsonException) { }
            savedQueues.Add(new(Guid.NewGuid().ToString("N"), "Default queue", legacyIds));
        }
        activeQueueId = savedQueues.FirstOrDefault(x => x.Id == library.Setting("activeQueue"))?.Id ?? savedQueues[0].Id;
        var savedQueue = library.Setting("queue") ?? Wire.Serialize(savedQueues.First(x => x.Id == activeQueueId).MediaIds);
        RefreshLibrary(); WatchFolders();
        library.MediaSaved += QueueLibraryUpdate;
        libraryUpdateTimer.Tick += (_, _) => ApplyLibraryUpdates();
        libraryUpdateTimer.Start();
        InitializeSharedLibrary();
        try { foreach (var id in Wire.Read<string[]>(savedQueue).Take(10000)) if (items.FirstOrDefault(x => x.Id == id) is { } queued) queue.Add(queued); }
        catch (System.Text.Json.JsonException) { }
        if (int.TryParse(library.Setting("queuePosition"), out var cursor)) queuePosition = Math.Clamp(cursor, -1, queue.Count - 1);
        LoadSavedRooms(); RefreshQueuePickers(); PublishQueue();
        if (int.TryParse(library.Setting("volume"), out var savedVolume)) Volume.Value = Math.Clamp(savedVolume, 0, 100);
        timer.Tick += (_, _) => PlaybackTick(); timer.Start();
        scanDelay.Tick += async (_, _) => { scanDelay.Stop(); if (!scanning) await Guard(Scan); };
        Loaded += async (_, _) =>
        {
            await Guard(async () =>
            {
                await Task.Run(() => LibVLCSharp.Shared.Core.Initialize());
                vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--quiet");
                player = new(vlc) { Volume = (int)Volume.Value, EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
                Video.MediaPlayer = player;
                player.TimeChanged += (_, e) =>
                {
                    var mono = Environment.TickCount64; playbackPosition.Observe(e.Time, mono);
                    if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("native-time", new { position = e.Time });
                    Dispatcher.BeginInvoke(() => { if (!closing) settling.Observe(e.Time, mono); });
                };
                RefreshTracks();
                player.Playing += (_, _) => Dispatcher.BeginInvoke(() => { if (closing) return; ready = true; if (room is null && localResume > 0 && player is not null) { player.Time = localResume; localResume = 0; } RefreshTracks(); try { room?.Send(new("ready")); } catch { } if (playbackPause.Pending || target?.Playing == false) player?.SetPause(true); });
                player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => SetStatus("Playback failed. Check that the file is available and the connection is active."));
                player.Buffering += (_, e) => Dispatcher.BeginInvoke(() =>
                {
                    if (closing) return;
                    if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("buffer", new { cache = e.Cache, position = player?.Time, revision = target?.Revision });
                    if (buffering.Cache(e.Cache, Environment.TickCount64)) { try { room?.Send(new("ready")); } catch { } }
                });
                player.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (closing) return;
                    PlayButton.Content = "▶";
                    if (room is null && playingItem is not null) { library.Setting("position:" + playingItem.Id, "0"); library.Setting("watched:" + playingItem.Id, "true"); foreach (var card in libraryCards.Where(x => x.Card.Media.Id == playingItem.Id)) UpdateCardPlayback(card); }
                    if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player.Length));
                    if ((queue.Count > 0 || roomLibrary?.Queue.Entries.Length > 0) && (room is null || room.Identity?.Host == true)) NextQueued(this, new RoutedEventArgs());
                });
            });
            if (folders.Count == 0) ShowPage("Folders");
            else if (AutoArtwork.IsChecked == true) await FetchArtwork();
        };
    }
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.ToolTip = text;
        if (RoomPage.Visibility == Visibility.Visible) { ConnectionStatus.Text = text; ConnectionStatus.ToolTip = text; ShowPlayerControls(); }
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Operation cancelled or timed out."); }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Watchroom", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowPage(string name)
    {
        if (currentPage == "Library" && name != "Library") ReleaseHostLibrary();
        if (name != currentPage) RememberLocation();
        currentPage = name;
        if (name != "Room") videoClickDelay.Stop();
        if (fullscreen && name != "Room") SetFullscreen(false);
        // VideoView hosts its content in a separate native overlay window. Collapsing
        // only RoomPage does not update that window's visibility or hit testing.
        // Hide the view itself before changing the shell layout, and restore both
        // the view and its overlay content when returning to the player.
        var playerVisibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
        PlayerOverlay.Visibility = playerVisibility;
        Video.Visibility = playerVisibility;
        LibraryPage.Visibility = name == "Library" ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = name == "Details" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = name == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        RoomsPage.Visibility = name == "Rooms" ? Visibility.Visible : Visibility.Collapsed;
        QueuesPage.Visibility = name == "Queues" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        RoomPage.Visibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlayerLayout();
        UpdateSessionControls();
        RefreshSharedLibrary();
        if (name == "Library") FilterLibrary();
        if (name == "Rooms") RefreshSavedRooms();
        UpdateLibraryCategorySelection(name is "Library" or "Details");
        foreach (var button in new[] { LibraryNav, QueuesNav, RoomNav, FoldersNav, SettingsNav })
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,
                (string)button.Tag == (name == "Details" ? "Library" : name == "Room" ? "Rooms" : name) ? "Selected" : "");
    }
    private void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private static int CategoryFor(MediaItem item) => item.Kind == "Anime" ? 3 : item.Series is not null ? 2 : 1;
    private static string CategoryName(int category) => category switch { 1 => "Movies", 2 => "Shows", 3 => "Anime", _ => "All" };
    private static string SeasonName(int season) => season < 0 ? "Season unknown" : season == 0 ? "Specials" : $"Season {season}";
    private void UpdateLibraryCategorySelection(bool active)
    {
        foreach (var button in new[] { AllLibraryNav, AnimeLibraryNav, ShowsLibraryNav, MoviesLibraryNav })
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, active && int.Parse((string)button.Tag) == libraryCategory ? "Selected" : "");
    }
    private void LibraryMenuClick(object sender, RoutedEventArgs e)
    {
        var visible = LibraryCategories.Visibility == Visibility.Visible;
        LibraryCategories.Visibility = LibraryPage.Visibility == Visibility.Visible && visible ? Visibility.Collapsed : Visibility.Visible;
        LibraryChevron.Text = LibraryCategories.Visibility == Visibility.Visible ? "▾" : "▸";
        ShowPage("Library"); UpdateBreadcrumbs();
    }
    private void LibraryCategoryClick(object sender, RoutedEventArgs e) => NavigateLibrary(int.Parse((string)((Button)sender).Tag));
    private void NavigateLibrary(int category, string? series = null, string? kind = null, int? season = null)
    {
        if (GuestLibrary)
        {
            ShowPage("Library"); GuestLibraryCategory.SelectedIndex = category; GuestLibrarySearch.Clear();
            guestBrowse = new(Category: category);
            if (OwnsLibrary) RequestHostLibrary();
            return;
        }
        if (currentPage != "Library" || libraryCategory != category || browseSeries != series || browseKind != kind || browseSeason != season || SearchBox.Text.Length > 0 || LibraryStatusFilter.SelectedIndex != 0) RememberLocation();
        var restoring = restoringNavigation; restoringNavigation = true;
        libraryCategory = category; browseSeries = series; browseKind = kind; browseSeason = season;
        ShowPage("Library"); SearchBox.Clear(); LibraryStatusFilter.SelectedIndex = 0;
        restoringNavigation = restoring;
        FilterLibrary();
    }
    private void UpdateBreadcrumbs()
    {
        if (LibraryBreadcrumbs is null || DetailBreadcrumbs is null) return;
        var detail = Details.Visibility == Visibility.Visible ? selected : null;
        var show = detail?.Series is not null ? detail : browseSeries is null ? null : items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind));
        var category = detail is not null ? CategoryFor(detail) : libraryCategory;
        var crumbs = new List<LibraryBreadcrumb> { new("All", "category", Separator: "") };
        if (category != 0) crumbs.Add(new(CategoryName(category), "category", category));
        if (show?.Series is not null)
        {
            var showId = LibraryIdentity.ShowKey(show);
            crumbs.Add(new(show.Series, "show", category, showId, show.Kind));
            var season = detail is not null ? detail.Season ?? -1 : browseSeason;
            if (season is { } number) crumbs.Add(new(SeasonName(number), "season", category, showId, show.Kind, number));
        }
        if (detail is not null) crumbs.Add(new(detail.Series is null ? detail.DisplayTitle : detail.EpisodeDisplayTitle, "media", category, MediaId: detail.Id));
        crumbs[^1] = crumbs[^1] with { IsCurrent = true };
        // Metadata refreshes should not recreate an unchanged navigation bar.
        if (breadcrumbItems.SequenceEqual(crumbs)) return;
        breadcrumbItems = crumbs; LibraryBreadcrumbs.ItemsSource = crumbs; DetailBreadcrumbs.ItemsSource = crumbs;
    }
    private void BreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement element) return;
        var crumb = element.DataContext as LibraryBreadcrumb;
        if (crumb is null) return;
        if (crumb.Level == "media") { if (items.FirstOrDefault(x => x.Id == crumb.MediaId) is { } item) Select(item); }
        else NavigateLibrary(crumb.Category, crumb.Series, crumb.Kind, crumb.Season);
        e.Handled = true;
    }
    private void RefreshLibrary()
    {
        library.Prune(folders, exclusions);
        libraryUpdates.Clear();
        items = library.All();
        RefreshLibraryFromItems(items);
    }
    private void RefreshLibraryFromItems(List<MediaItem> refreshedItems)
    {
        room?.SetHostedLibrary(refreshedItems);
        items = refreshedItems;
        if (browseSeries is not null && !items.Any(x => LibraryIdentity.InShow(x, browseSeries, browseKind)))
        {
            var changed = items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries));
            if (changed is null) { browseSeries = browseKind = null; browseSeason = null; }
            else browseKind = changed.Kind;
        }
        FilterLibrary();
        for (int i = 0; i < savedQueues.Count; i++) savedQueues[i] = savedQueues[i] with { MediaIds = savedQueues[i].MediaIds.Where(id => items.Any(x => x.Id == id)).ToArray() };
        if (savedQueues.Count > 0) { SaveQueues(); RefreshQueuePickers(); }
        var priorCursorId = queuePosition >= 0 && queuePosition < queue.Count ? queue[queuePosition].Id : null;
        var refreshedQueue = queue.Select(x => items.FirstOrDefault(updated => updated.Id == x.Id)).OfType<MediaItem>().ToArray();
        queue.Clear(); foreach (var queued in refreshedQueue) queue.Add(queued);
        queuePosition = priorCursorId is null ? -1 : queue.FindIndex(x => x.Id == priorCursorId);
        PublishQueue();
        if (selected is not null && !items.Any(x => x.Id == selected.Id)) { selected = null; if (Details.Visibility == Visibility.Visible) ShowPage("Library"); }
        if (Details.Visibility == Visibility.Visible && selected is not null && items.FirstOrDefault(x => x.Id == selected.Id) is { } updated) Select(updated);
        SetStatus($"{items.Count(x => x.Available)} videos available · {folders.Count} {(folders.Count == 1 ? "folder" : "folders")}");
    }
    private void QueueLibraryUpdate(MediaItem item) => libraryUpdates[item.Id] = item;
    private void ApplyLibraryUpdates()
    {
        if (closing) return;
        if (Interlocked.Exchange(ref pendingScanStatus, null) is { } progress && scanning)
        { ScanStatus.Text = progress; SetStatus(progress); }
        if (libraryUpdates.IsEmpty)
        {
            if (libraryViewDirty && librarySearchCancellation is null) FilterLibrary(backgroundUpdate: true);
            return;
        }
        var updated = items.ToDictionary(item => item.Id);
        var changed = false;
        foreach (var id in libraryUpdates.Keys)
            if (libraryUpdates.TryRemove(id, out var item) && (!updated.TryGetValue(id, out var previous) || previous != item))
            { updated[id] = item; changed = true; }
        if (!changed) return;
        var browsedId = items.FirstOrDefault(item => LibraryIdentity.InShow(item, browseSeries, browseKind))?.Id;
        items = updated.Values.ToList();
        room?.SetHostedLibrary(items);
        if (browseSeries is not null && !items.Any(item => LibraryIdentity.InShow(item, browseSeries, browseKind)))
        {
            var current = browsedId is null ? null : updated.GetValueOrDefault(browsedId);
            if (current?.Series is not null) { browseSeries = LibraryIdentity.ShowKey(current); browseKind = current.Kind; }
            else { browseSeries = browseKind = null; browseSeason = null; }
        }
        libraryViewDirty = true;
        if (librarySearchCancellation is null) FilterLibrary(backgroundUpdate: true);
        if (selected is not null && items.FirstOrDefault(item => item.Id == selected.Id) is { } refreshed && refreshed != selected)
        {
            selected = refreshed;
            if (Details.Visibility == Visibility.Visible) Select(refreshed);
        }
    }
    private async void FilterLibrary(bool debounce = false, bool backgroundUpdate = false)
    {
        if (PosterGrid is null || LibraryStatusFilter is null || NoResults is null || library is null || closing) return;
        librarySearchCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        librarySearchCancellation = request;
        libraryViewDirty = false;
        var cancellation = request.Token;
        // Capture one immutable library and navigation state before leaving the UI thread.
        var status = LibraryStatusFilter.SelectedIndex;
        var source = status == 2 ? items.Where(x => !x.Available).ToList() : items;
        var series = browseSeries; var kind = browseKind; var season = browseSeason;
        var showTitle = items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind))?.Series ?? browseSeries;
        SearchHint.Text = browseSeries is null ? "Search your library" : browseSeason is null ? "Search seasons" : "Search episodes";
        SearchBox.ToolTip = browseSeries is null ? "Search your library (Ctrl+F)" : "Search within " + showTitle + (browseSeason is null ? "" : $" · Season {browseSeason}");
        System.Windows.Automation.AutomationProperties.SetName(SearchBox, SearchHint.Text);
        var filter = libraryCategory;
        var query = SearchBox.Text.Trim();
        if (!backgroundUpdate)
        {
            ResultCount.Text = "Searching…";

            NoResults.Visibility = Visibility.Collapsed;
        }
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateBreadcrumbs();
        UpdateLibraryCategorySelection(LibraryPage.Visibility == Visibility.Visible || Details.Visibility == Visibility.Visible);
        var seasonItem = browseSeason is null ? null : items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind) && (x.Season ?? -1) == browseSeason);
        SeasonInformation.Visibility = seasonItem is null ? Visibility.Collapsed : Visibility.Visible;
        SeasonSummary.Text = seasonItem is null ? "" : (string.IsNullOrWhiteSpace(seasonItem.SeasonTitle) ? "" : seasonItem.SeasonTitle + "\n\n") + (seasonItem.SeasonOverview ?? seasonItem.Overview ?? "No season overview available.") + (seasonItem.SeasonSource is null ? "" : "\n\nMetadata: " + seasonItem.SeasonSource);
        EmptyLibrary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            if (debounce) await Task.Delay(200, cancellation);
            // A slow artwork drive must not spawn overlapping searches on every keystroke.
            await librarySearchGate.WaitAsync(cancellation);
            List<LibraryCard> cards;
            try { cards = await LibraryCatalog.SearchAsync(source, query, filter, series, kind, season, cancellation, displaySpecialsWithinSeasons); }
            finally { librarySearchGate.Release(); }
            if (status == 1) cards = cards.Where(card => card.Poster is null).ToList();
            if (cancellation.IsCancellationRequested || librarySearchCancellation != request) return;
            if (PosterGrid.ItemsSource != libraryCards) PosterGrid.ItemsSource = libraryCards;
            var views = libraryCards.ToDictionary(view => view.Key);
            var desiredKeys = cards.Select(LibraryCardView.Identity).ToHashSet();
            var slice = Stopwatch.StartNew();
            async Task YieldForInput()
            {
                if (slice.ElapsedMilliseconds < 6) return;
                await Dispatcher.Yield(DispatcherPriority.Background);
                cancellation.ThrowIfCancellationRequested(); slice.Restart();
            }
            for (var index = libraryCards.Count - 1; index >= 0; index--)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!desiredKeys.Contains(libraryCards[index].Key)) libraryCards.RemoveAt(index);
                await YieldForInput();
            }
            for (var index = 0; index < cards.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!views.TryGetValue(LibraryCardView.Identity(cards[index]), out var view)) view = new(cards[index]);
                else view.Update(cards[index]);
                UpdateCardPlayback(view);
                if (index >= libraryCards.Count || libraryCards[index] != view)
                {
                    var existingIndex = libraryCards.IndexOf(view);
                    if (existingIndex >= 0) libraryCards.Move(existingIndex, index);
                    else libraryCards.Insert(index, view);
                }
                await YieldForInput();
            }
            ResultCount.Text = $"{cards.Count} {(cards.Count == 1 ? "item" : "items")}";
            NoResults.Visibility = items.Count > 0 && cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (librarySearchCancellation == request && !closing)
            {
                ResultCount.Text = "Search failed"; SetStatus("Library search failed: " + ex.Message);
            }
        }
        finally
        {
            if (librarySearchCancellation == request)
            {
                librarySearchCancellation = null;

            }
        }
    }
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        FilterLibrary(debounce: true);
    }
    private void ClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void SearchFrameClick(object sender, MouseButtonEventArgs e) => SearchBox.Focus();
    private void ResetFilters(object sender, RoutedEventArgs e)
    {
        NavigateLibrary(0);
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => FilterLibrary();
    private void CardArtworkFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // An unavailable or invalid image should use the same fallback as missing artwork.
        ((Image)sender).Source = null;
        e.Handled = true;
    }
    private void SelectMovie(object sender, RoutedEventArgs e)
    {
        var card = (LibraryCard)((Button)sender).Tag;
        if (card.Level == "series") NavigateLibrary(CategoryFor(card.Media), LibraryIdentity.ShowKey(card.Media), card.Media.Kind);
        else if (card.Level == "season") NavigateLibrary(libraryCategory, browseSeries, browseKind, card.Season);
        else { Select(card.Media); Details.ScrollToTop(); }
    }
    private void Select(MediaItem item)
    {
        if (currentPage != "Details" || selected?.Id != item.Id) RememberLocation();
        var restoring = restoringNavigation; restoringNavigation = true;
        selected = item; libraryCategory = CategoryFor(item);
        browseSeries = item.Series is null ? null : LibraryIdentity.ShowKey(item); browseKind = item.Series is null ? null : item.Kind;
        browseSeason = item.Series is null ? null : item.Season ?? -1;
        ShowPage("Details"); UpdateBreadcrumbs(); DetailTitle.Text = item.EpisodeDisplayTitle;
        restoringNavigation = restoring;
        DetailPlay.IsEnabled = DetailHost.IsEnabled = DetailQueue.IsEnabled = item.Available && File.Exists(item.Path);
        var resume = long.TryParse(library.Setting("position:" + item.Id), out var position) && position >= 10000 ? position : 0;
        DetailPlay.Content = resume > 0 ? "Resume at " + FormatTime(resume) : "Play locally";
        RestartButton.Visibility = resume > 0 ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.IsEnabled = DetailPlay.IsEnabled;
        DetailContext.Text = item.Series is null ? "MOVIE" : item.Series;
        DetailMeta.Text = item.Caption + " · " + Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant() + (string.IsNullOrWhiteSpace(item.AirDate) ? "" : " · " + item.AirDate) + (item.RuntimeMinutes is > 0 ? $" · {item.RuntimeMinutes} min" : "");
        _ = UpdateDetailPosterAsync(item);
        DetailFile.Text = item.Path;
        var overviewSource = !string.IsNullOrWhiteSpace(item.EpisodeOverview) ? item.EpisodeSource : !string.IsNullOrWhiteSpace(item.SeasonOverview) ? item.SeasonSource : item.PosterSource;
        DetailSummary.Text = (item.IsVirtual ? "This episode is not in your library. " : item.Available ? "" : "File unavailable — reconnect its drive. ") + (item.DetailOverview ?? "No overview available yet.") +
            (item.Genres?.Length > 0 ? "\n\nGenres: " + string.Join(", ", item.Genres) : "") + (item.Cast?.Length > 0 ? "\nCast: " + string.Join(", ", item.Cast) : "") +
            (item.Rating is > 0 ? $"\nRating: {item.Rating:0.0}/10" : "") + (item.NumberingConflict is null ? "" : "\n\n" + item.NumberingConflict) + (overviewSource is null ? "" : "\n\nMetadata: " + overviewSource);
        Episodes.Visibility = Visibility.Collapsed;
    }
    private async Task UpdateDetailPosterAsync(MediaItem item)
    {
        var generation = ++detailPosterGeneration;
        var path = await Task.Run(() => LibraryCatalog.EpisodeArtwork(item));
        if (closing || generation != detailPosterGeneration) return;
        if (path == detailPosterPath && (path is null || DetailPoster.Source is not null)) return;
        detailPosterPath = path; DetailPoster.Source = null;
        var view = new LibraryCardView(new(item.DisplayTitle, item.Caption, path, item, "episode"));
        await view.LoadPosterAsync();
        if (!closing && generation == detailPosterGeneration) DetailPoster.Source = view.Poster;
    }
    private void EpisodeChanged(object sender, SelectionChangedEventArgs e) { if (Episodes.SelectedItem is MediaItem item) { selected = item; DetailSummary.Text = item.Caption + (item.Available ? "" : " · File unavailable"); } }
    private void AddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a movie, show or anime folder", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames.Where(path => !folders.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            folders.Add(new(path, ((ComboBoxItem)FolderKind.SelectedItem).Content.ToString()!));
        SaveFolders();
        scanDelay.Stop(); scanDelay.Start(); SetStatus("Folder added. Scanning your library…");
    }
    private void RemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is LibraryFolder folder) { folders.Remove(folder); SaveFolders(); RefreshLibrary(); scanDelay.Stop(); scanDelay.Start(); SetStatus("Folder removed from library."); }
    }
    private void ChangeFolderKind(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not LibraryFolder folder) { SetStatus("Select a folder, then choose its category."); return; }
        folders[folders.IndexOf(folder)] = folder with { Kind = ((ComboBoxItem)FolderKind.SelectedItem).Content.ToString()! };
        SaveFolders(); scanDelay.Stop(); scanDelay.Start(); SetStatus("Folder category updated. Scanning your library…");
    }
    private void FolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoveFolderButton is null || ChangeFolderButton is null) return;
        RemoveFolderButton.IsEnabled = ChangeFolderButton.IsEnabled = !scanning && FolderList.SelectedItem is LibraryFolder;
    }
    private void SaveFolders() { library.Setting("folders", Wire.Serialize(folders)); WatchFolders(); }
    private void WatchFolders()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        foreach (var folder in folders.Where(x => Directory.Exists(x.Path)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, EnableRaisingEvents = true };
                void Changed(object? s, FileSystemEventArgs e) => Dispatcher.BeginInvoke(() => { scanDelay.Stop(); scanDelay.Start(); });
                watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (s, e) => Changed(s, e);
                watchers.Add(watcher);
            }
            catch (IOException) { }
        }
    }
    private async void ScanClick(object sender, RoutedEventArgs e)
    {
        if (scanning) { scanCancellation?.Cancel(); return; }
        await Guard(Scan);
    }
    private async Task Scan()
    {
        if (scanning) return; scanning = true;
        ScanButton.IsEnabled = false;
        AddFolderButton.IsEnabled = RemoveFolderButton.IsEnabled = ChangeFolderButton.IsEnabled = FolderKind.IsEnabled = ExcludeFolderButton.IsEnabled = RestoreFolderButton.IsEnabled = false;
        RescanButton.Content = "Cancel scan";
        RescanButton.ToolTip = "Stop scanning; keep videos already indexed";
        ScanProgress.Visibility = Visibility.Visible;
        scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        CancelScanButton.Visibility = Visibility.Visible;
        try
        {
            var progress = new ScanProgressSink(this);
            var scannedItems = await library.ScanAsync(folders.ToArray(), progress, scanCancellation.Token, exclusions.ToArray());
            Interlocked.Exchange(ref pendingScanStatus, null);
            libraryUpdates.Clear(); RefreshLibraryFromItems(scannedItems);
            ScanStatus.Text = $"Scan complete · {items.Count} videos indexed.";
            if (AutoArtwork.IsChecked == true && !scanCancellation.IsCancellationRequested) await FetchArtwork(scanCancellation.Token);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { var partial = await Task.Run(library.All); libraryUpdates.Clear(); RefreshLibraryFromItems(partial); ScanStatus.Text = "Scan cancelled. Videos already indexed remain in your library."; SetStatus(ScanStatus.Text); }
        finally
        {
            scanning = false;
            ScanButton.IsEnabled = RescanButton.IsEnabled = true;
            AddFolderButton.IsEnabled = FolderKind.IsEnabled = ExcludeFolderButton.IsEnabled = true;
            RestoreFolderButton.IsEnabled = ExcludedFolderList.SelectedItem is string;
            RemoveFolderButton.IsEnabled = ChangeFolderButton.IsEnabled = FolderList.SelectedItem is LibraryFolder;
            RescanButton.Content = "↻  Rescan";
            RescanButton.ToolTip = "Check your folders for new videos";
            ScanProgress.Visibility = Visibility.Collapsed;
            CancelScanButton.Visibility = Visibility.Collapsed;
            scanCancellation.Dispose(); scanCancellation = null;
        }
    }
    private void CancelScan(object sender, RoutedEventArgs e) => scanCancellation?.Cancel();
    private async void FetchArtworkClick(object sender, RoutedEventArgs e)
    {
        if (SaveMetadataToken()) await Guard(() => FetchArtwork(lifetime.Token, refresh: (MetadataRefresh)MetadataRefreshMode.SelectedIndex));
    }
    private bool SaveMetadataToken()
    {
        try { MetadataCredential.Save(App.DataDirectory, MetadataToken.Password); return true; }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        { SetStatus("Could not save the TMDB credential. " + ex.Message); return false; }
    }
    private void SaveMetadataKey(object sender, RoutedEventArgs e)
    {
        MetadataKeyStatus.Text = SaveMetadataToken()
            ? string.IsNullOrWhiteSpace(MetadataToken.Password) ? "Saved key removed." : "Key saved securely."
            : "Could not save the key. See the status message below.";
    }
    private void MetadataKeyChanged(object sender, RoutedEventArgs e) => MetadataKeyStatus.Text = "Unsaved changes.";
    private Task FetchArtwork() => FetchArtwork(lifetime.Token);
    private async Task FetchArtwork(CancellationToken cancellation, bool force = false, MetadataRefresh? refresh = null)
    {
        if (artworkBusy) return; artworkBusy = true;
        var token = MetadataToken.Password;
        var progress = new Progress<string>(SetStatus);
        try { var report = await Task.Run(() => AutomaticArtwork.FetchAsync(library, App.DataDirectory, token, progress, cancellation, force: force, refresh: refresh), cancellation); RefreshLibrary(); SetStatus(report); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { ApplyLibraryUpdates(); artworkBusy = false; }
    }
    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        try { RoomAddress.ValidateServer(ServerBox.Text); }
        catch (ArgumentException ex) { SetStatus(ex.Message); ServerBox.Focus(); return; }
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text)) { SetStatus("Enter your display name before saving."); DisplayNameBox.Focus(); return; }
        var providers = MetadataProviders.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).Distinct().ToArray();
        if (providers.Any(x => x is not ("tmdb" or "tvmaze" or "wikipedia")) || !int.TryParse(MetadataRefreshDays.Text, out var days) || days < 0 || days > 365 ||
            !System.Text.RegularExpressions.Regex.IsMatch(MetadataLanguage.Text.Trim(), @"^[a-z]{2}(?:-[A-Z]{2})?$") || !System.Text.RegularExpressions.Regex.IsMatch(MetadataCountry.Text.Trim(), @"^[A-Z]{2}$"))
        { SetStatus("Use providers tmdb, tvmaze, wikipedia; refresh days 0–365; language such as en-US; and a country such as US."); return; }
        if (!SaveMetadataToken()) return;
        new MetadataOptions(providers, MetadataLanguage.Text.Trim(), MetadataCountry.Text.Trim(), days, ImportMissing.IsChecked == true,
            ImportUpcoming.IsChecked == true, SaveNfo.IsChecked == true, DisplaySpecials.IsChecked == true, GroupShows.IsChecked == true).Save(library);
        displaySpecialsWithinSeasons = DisplaySpecials.IsChecked == true;
        library.Setting("name", DisplayNameBox.Text.Trim()); library.Setting("server", ServerBox.Text.Trim().TrimEnd('/'));
        library.Setting("artwork", AutoArtwork.IsChecked == true ? "true" : "false"); library.Setting("ffmpeg", FfmpegBox.Text.Trim()); SetStatus("Settings saved. TMDB token saved in Windows Credential Manager.");
    }
    private async void MatchClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null) return;
        if (MetadataToken.Password.Length == 0) { ShowPage("Settings"); SetStatus("Enter your TMDB API read access token, then return to Find artwork."); return; }
        if (!SaveMetadataToken()) return;
        var item = selected; var query = Dialogs.Prompt(this, "Find artwork", "Search by movie or series title", item.DisplayTitle); if (query is null) return;
        using var metadata = new MetadataClient(MetadataToken.Password, options: MetadataOptions.Load(library));
        var results = (await metadata.SearchAsync(query, true, lifetime.Token)).Concat(await metadata.SearchAsync(query, false, lifetime.Token)).ToList();
        if (results.Count == 0) { SetStatus("No matches. Try a different title or use a local poster."); return; }
        var match = Dialogs.Choose(this, results); if (match is null) return;
        await ApplyMatch(metadata, item, match); RefreshLibrary(); Select(items.First(x => x.Id == item.Id));
    });
    private async Task ApplyMatch(MetadataClient metadata, MediaItem item, MetadataMatch match)
    {
        match = await metadata.DetailsAsync(match, lifetime.Token);
        var poster = await metadata.CachePosterAsync(match, Path.Combine(App.DataDirectory, "posters"), lifetime.Token);
        foreach (var entry in items.Where(x => x.Id == item.Id || LibraryIdentity.SameShow(x, item)).ToArray())
            library.Save(AutomaticArtwork.MergeDetails(LibraryIdentity.WithProvider(entry with { ProviderIds = null, MetadataProvider = null, MetadataId = null }, "tmdb", match.Id), match) with { Title = match.Title, Series = match.Type == "tv" ? match.Title : null, Kind = match.Kind ?? (match.Type == "tv" ? "Show" : "Movie"),
                MetadataKind = match.Kind ?? (match.Type == "tv" ? "Show" : "Movie"), MetadataType = match.Type,
                Season = match.Type == "tv" ? entry.Season : null, Episode = match.Type == "tv" ? entry.Episode : null,
                Poster = poster ?? entry.Poster, Overview = match.Overview, Year = int.TryParse(match.Year, out var year) ? year : entry.Year, Matched = true, PosterSource = $"https://www.themoviedb.org/{match.Type}/{match.Id}",
                MetadataProvider = "tmdb", MetadataId = match.Id, MetadataFetchedAt = 0,
                SeasonTitle = null, SeasonOverview = null, EpisodeTitle = null, EpisodeOverview = null, EpisodePoster = null, AirDate = null, RuntimeMinutes = null,
                SeasonPoster = entry.SeasonSource is not null && entry.SeasonPoster is not null && LibraryStore.IsWithin(entry.SeasonPoster, Path.Combine(App.DataDirectory, "posters")) ? null : entry.SeasonPoster, SeasonSource = null, EpisodeSource = null });
        if (item.Series is not null && LibraryIdentity.InShow(item, browseSeries, browseKind)) { browseSeries = match.Type == "tv" ? LibraryIdentity.ShowKey(item) : null; browseKind = match.Kind; if (browseSeries is null) browseSeason = null; }
        if (match.Type == "tv")
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            SetStatus(await SeriesMetadata.FetchAsync(library, App.DataDirectory, MetadataToken.Password, new Progress<string>(SetStatus), lifetime.Token, http, true));
        }
    }
    private void EditTitle(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var item = selected;
        var title = Dialogs.Prompt(this, "Correct title", item.Series is null ? "Movie title" : "Series title (applies to every episode)", item.DisplayTitle,
            text => text.Length > 300 ? "Use a title with at most 300 characters." : null);
        if (title is null) return;
        foreach (var entry in items.Where(x => x.Id == item.Id || LibraryIdentity.SameShow(x, item)))
            library.Save(entry with { Title = title, Series = entry.Series is null ? null : title, Matched = true, LockedFields = (entry.LockedFields ?? []).Append("Title").Distinct().ToArray() });
        if (item.Series is not null && LibraryIdentity.InShow(item, browseSeries, browseKind)) browseSeries = LibraryIdentity.ShowKey(item);
        RefreshLibrary(); Select(items.First(x => x.Id == item.Id)); SetStatus("Title corrected. Rescanning will preserve your changes.");
    }
    private void LocalPoster(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var picker = new OpenFileDialog { Filter = "Poster image|*.jpg;*.jpeg;*.png" };
        if (picker.ShowDialog(this) != true) return;
        var dir = Path.Combine(App.DataDirectory, "posters"); Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, selected.Id + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(picker.FileName)); File.Copy(picker.FileName, dest, true);
        foreach (var entry in items.Where(x => x.Id == selected.Id || LibraryIdentity.SameShow(x, selected))) library.Save(entry with { Poster = dest, SeriesPoster = entry.Series is null ? null : dest, Matched = true, PosterSource = "Local artwork", LockedFields = (entry.LockedFields ?? []).Append("Poster").Distinct().ToArray() });
        RefreshLibrary();
    }
    private async void PlayLocal(object sender, RoutedEventArgs e) => await Guard(async () => { if (selected is not null) { queuePosition = -1; await PlayLocalItem(selected); PublishQueue(); } });
    private async Task PlayLocalItem(MediaItem item)
    {
        if (player is null) return;
        if (!File.Exists(item.Path)) throw new FileNotFoundException("Movie file is unavailable. Reconnect its drive and rescan.");
        await Disconnect(); target = null; loadedMedia = null; playingItem = item;
        localResume = long.TryParse(library.Setting("position:" + item.Id), out var saved) ? saved : 0;
        using var media = new Media(vlc!, item.Path, FromType.FromPath);
        ready = false; RoomHeading.Text = PlaybackTitle(item); RoomSubtitle.Text = "Local playback";
        ConnectionStatus.Text = "Local playback";
        await PrepareVideoSurface(); player.Play(media);
    }
    private void RestartLocal(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        library.Setting("position:" + selected.Id, "0");
        library.Setting("watched:" + selected.Id, "false");
        if (playingItem?.Id == selected.Id) playingItem = null;
        PlayLocal(sender, e);
    }
    private void UpdateCardPlayback(LibraryCardView view)
    {
        if (view.IsEpisode) view.SetPlayback(long.TryParse(library.Setting("position:" + view.Card.Media.Id), out var position) ? position : 0, library.Setting("watched:" + view.Card.Media.Id) == "true");
    }
    private void SavePlaybackPosition()
    {
        if (room is null && playingItem is not null && player is not null && player.Time >= 0)
        {
            library.Setting("position:" + playingItem.Id, player.Length > 0 && player.Time >= player.Length - 2000 ? "0" : Math.Max(0, player.Time).ToString());
            foreach (var card in libraryCards.Where(x => x.Card.Media.Id == playingItem.Id)) UpdateCardPlayback(card);
        }
    }
    private async void HostClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null || roomBusy) return;
        if (!File.Exists(selected.Path)) throw new FileNotFoundException("Movie file is unavailable.");
        if (room?.IsConnected == true && room.Identity?.Host == true)
        {
            playingItem = selected; room.SetHostedFile(selected.Path, PlaybackTitle(selected));
            room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); ShowPage("Room"); return;
        }
        RoomAddress.ValidateServer(ServerBox.Text);
        var item = selected; roomBusy = true; DetailHost.IsEnabled = false;
        try {
        await Disconnect(); playingItem = item; var client = NewRoom();
        client.SetHostedFile(item.Path, PlaybackTitle(item));
        SetStatus("Connecting to the room service…");
        try { await client.ConnectAsync(ServerBox.Text.Trim(), DisplayNameBox.Text, persistent: true); }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or OperationCanceledException)
        {
            await Disconnect();
            throw new IOException($"Cannot reach the room service at {ServerBox.Text.Trim()}. Check the server address in Settings. Localhost requires a server running on this computer.", ex);
        }
        RememberCurrentRoom(); ShowPage("Room"); SetStatus("Private room connected. Waiting for guests.");
        } finally { roomBusy = false; DetailHost.IsEnabled = selected?.Available == true && File.Exists(selected.Path); UpdateSessionControls(); }
    });
    private async void JoinClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy) return;
        var invitation = Dialogs.Prompt(this, "Join a watch room", "Paste the invitation link or room code", validate: text =>
        { try { RoomAddress.Parse(text, ServerBox.Text); return null; } catch (ArgumentException ex) { return ex.Message; } }); if (string.IsNullOrWhiteSpace(invitation)) return;
        var address = RoomAddress.Parse(invitation, ServerBox.Text);
        roomBusy = true;
        try {
            await Disconnect(); playingItem = null; var client = NewRoom(); SetStatus("Joining room…");
            try { await client.ConnectAsync(address.Server, DisplayNameBox.Text, address.Code); }
            catch { await Disconnect(); throw; }
            roomPanelVisible = true; RoomHeading.Text = "Your watch room";
            RememberCurrentRoom(); RoomSubtitle.Text = "Waiting for host approval"; ShowPage("Room");
        } finally { roomBusy = false; UpdateSessionControls(); }
    });
    private RoomClient NewRoom()
    {
        var client = new RoomClient(library); room = client;
        guestBrowse = new(); guestLibraryNotice = null; guestBrowsePending = false; ++guestBrowseRequest;
        client.SetHostedLibrary(items);
        client.Message += msg => Dispatcher.BeginInvoke(async () => { if (room == client) await Guard(() => OnRoomMessage(msg)); });
        client.LibraryPeerReady += id => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return; EnsureRoomLibrary();
            try { roomLibrary?.PeerReady(id); } catch (IOException ex) { SetStatus(ex.Message); }
        });
        client.LibraryMessage += (id, message) => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return; EnsureRoomLibrary();
            try { roomLibrary?.Receive(id, message); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { SetStatus("Invalid library request: " + ex.Message); }
        });
        client.Status += text => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return; ConnectionStatus.Text = text;
            if (text.StartsWith("Disconnected", StringComparison.Ordinal)) { player?.SetPause(true); target = null; SetStatus(text); RoomSubtitle.Text = "Disconnected · Rejoin with your invitation"; UpdateSessionControls(); }
        });
        return client;
    }
    private async Task OnRoomMessage(WireMessage message)
    {
        if (room is null) return;
        switch (message.Type)
        {
            case "catalog-page": case "catalog-error": ReceiveHostLibrary(message); break;
            case "welcome":
                EnsureRoomLibrary();
                RememberCurrentRoom();
                if (room.Identity!.Host && room.HostedMedia is not null)
                {
                    room.Send(new("media", Data: Wire.Serialize(room.HostedMedia.Media)));
                    PublishQueue();
                    RoomSubtitle.Text = "Private room · Share your invitation to bring friends";
                    SetThreadExecutionState(0x80000003);
                }
                break;
            case "admitted": RoomSubtitle.Text = "Admitted · Connecting to host…"; break;
            case "snapshot":
                var snapshot = Wire.Read<RoomSnapshot>(message.Data!);
                roomLibrary?.SetPeople(snapshot.People);
                SyncCurrentRoomName(snapshot);
                if (room.Identity?.Host == false)
                {
                    QueueStatus.Text = $"{snapshot.Queue?.Length ?? 0} videos in the host's queue";
                    QueueList.ItemsSource = (snapshot.Queue ?? []).Select(title => new { QueueTitle = title }).ToArray();
                }
                foreach (var id in pendingAdmissions.Keys.ToArray())
                {
                    var participant = snapshot.People.FirstOrDefault(p => p.Id == id);
                    if (participant is null || participant.Approved)
                    {
                        pendingAdmissions.Remove(id);
                        ShowRoomFeedback(participant is null ? "The guest left the room." : $"{participant.Name} admitted.");
                    }
                }
                RefreshPeople(snapshot);
                SharedControls.IsEnabled = room.Identity?.Host == true; SharedControls.IsChecked = snapshot.SharedControls;
                if (room.Identity?.Host != true && !snapshot.SharedControls) playbackPause.Clear();
                if (snapshot.Playback is not null) AcceptState(snapshot.Playback);
                if (snapshot.Media is not null && snapshot.Media.Id != loadedMedia) await LoadRoomMedia(snapshot.Media);
                UpdateSessionControls();
                RefreshSharedLibrary();
                break;
            case "playback": AcceptState(Wire.Read<PlaybackState>(message.Data!)); break;
            case "chat": case "notice":
                chat.Add(message.Text ?? ""); if (chat.Count > 150) chat.RemoveAt(0); ChatList.ScrollIntoView(chat.Last()); break;
            case "error": ShowRoomFeedback(message.Text ?? "Room error"); SetStatus(message.Text ?? "Room error"); break;
        }
    }
    private void AcceptState(PlaybackState state)
    {
        if (target is null || state.Revision > target.Revision)
        {
            if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("revision", new { state.Revision, state.Playing, state.PositionMs, state.AtUnixMs, serverMs = room?.ServerNowMs });
            target = state; appliedRevision = -1;
            playbackPause.Observe(state);
            ApplyRoomPlayback();
            if (room is { } client && state.AtUnixMs > client.ServerNowMs)
                _ = ApplyScheduledPlayback(client, state);
        }
    }
    private async void CardArtworkLoaded(object sender, RoutedEventArgs e)
    {
        if (((Image)sender).DataContext is LibraryCardView view) { await view.LoadPosterAsync(); await ImproveEpisodeArtwork(view); }
    }
    private async void CardArtworkUpdated(object sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (((Image)sender).DataContext is LibraryCardView view) await view.LoadPosterAsync();
    }
    private async Task ImproveEpisodeArtwork(LibraryCardView view)
    {
        var item = view.Card.Media;
        var token = MetadataToken.Password; var ffmpeg = FfmpegBox.Text.Trim();
        var options = MetadataOptions.Load(library);
        if (AutoArtwork.IsChecked != true) options = options with { Providers = [] };
        if (!view.IsEpisode || !episodeArtworkRequests.Add(item.Id + ":" + item.EpisodeSource + ":" + LibraryIdentity.Hash(token) + ":" + string.Join(',', options.ProviderOrder))) return;
        try
        {
            var improved = await Task.Run(() => episodeArtwork.ImproveAsync(item, App.DataDirectory, token, ffmpeg, lifetime.Token, options));
            if (closing || improved == item) return;
            var current = await Task.Run(() => library.All().FirstOrDefault(x => x.Id == item.Id));
            if (current is null || current.EpisodeSource != item.EpisodeSource || current.Path != item.Path ||
                current.EpisodePoster != item.EpisodePoster || current.Season != item.Season || current.Episode != item.Episode ||
                current.ShowId != item.ShowId || current.MetadataId != item.MetadataId || current.MetadataProvider != item.MetadataProvider ||
                current.NumberingOrder != item.NumberingOrder || LibraryIdentity.Locked(current, "EpisodePoster")) return;
            library.Save(current with { EpisodePoster = improved.EpisodePoster, EpisodePosterQualityPath = improved.EpisodePosterQualityPath,
                GeneratedEpisodePoster = improved.GeneratedEpisodePoster });
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or UnauthorizedAccessException) { }
    }
    private async Task ApplyScheduledPlayback(RoomClient client, PlaybackState state)
    {
        try
        {
            // Resume on the UI context at the server deadline, without waiting
            // for the next 250 ms UI refresh. Superseded commands do no work.
            while (room == client && ReferenceEquals(target, state) && client.IsConnected)
            {
                var remaining = state.AtUnixMs - client.ServerNowMs;
                if (remaining <= 0) { ApplyRoomPlayback(); return; }
                await Task.Delay((int)Math.Min(remaining, 1000), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private async Task LoadRoomMedia(SharedMedia media)
    {
        var client = room; if (client is null) return;
        var generation = ++loadGeneration; loadedMedia = media.Id;
        await loading.WaitAsync(lifetime.Token);
        try
        {
            if (client != room || generation != loadGeneration) return;
            ready = false; buffering = new(); player?.Stop();
            if (target?.Playing == true) client.Send(new("buffering"));
            if (bridge is not null) await bridge.DisposeAsync(); bridge = null;
            var source = await client.GetSourceAsync(media, lifetime.Token);
            if (client != room || generation != loadGeneration) return;
            Uri? streamUri = null;
            if (source is not ILocalMediaSource)
            {
                bridge = new(); streamUri = await bridge.StartAsync(source);
            }
            // The host owns the file already. Feeding it back through HTTP adds
            // network buffering and slow-seek behavior to otherwise local playback.
            using var vlcMedia = source is ILocalMediaSource local
                ? new Media(vlc!, local.LocalPath, FromType.FromPath)
                : new Media(vlc!, streamUri!);
            if (streamUri is not null) vlcMedia.AddOption(":network-caching=200");
            if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("media-input", new { local = source is ILocalMediaSource, media.Extension });
            // VLC's AVI demuxer reads slow-seekable HTTP inputs in 1.5-second
            // steps, exposing read-ahead time to the synchronization controller.
            // The avformat demuxer provides a regularly advancing timeline.
            if (media.Extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)) vlcMedia.AddOption(":demux=avformat");
            foreach (var subtitle in (media.Subtitles ?? []).Take(12))
            {
                if (subtitle.Length is <= 0 or > 8388608 || !new[] { ".srt", ".ass", ".ssa" }.Contains(subtitle.Extension.ToLowerInvariant())) continue;
                var asset = await client.GetSourceAsync(subtitle, lifetime.Token);
                var dir = Path.Combine(App.DataDirectory, "subtitles"); Directory.CreateDirectory(dir);
                var subtitlePath = Path.Combine(dir, Guid.NewGuid().ToString("N") + subtitle.Extension.ToLowerInvariant());
                await using (var output = File.Create(subtitlePath))
                    for (long offset = 0; offset < subtitle.Length;)
                    {
                        var bytes = await asset.ReadAsync(offset, (int)Math.Min(32768, subtitle.Length - offset), lifetime.Token);
                        if (bytes.Length == 0) throw new IOException("Subtitle transfer ended early. Rejoin the room to retry.");
                        await output.WriteAsync(bytes, lifetime.Token); offset += bytes.Length;
                    }
                vlcMedia.AddSlave(MediaSlaveType.Subtitle, 2, new Uri(subtitlePath).AbsoluteUri);
            }
            if (client != room || generation != loadGeneration) return;
            RoomHeading.Text = client.Snapshot?.Name ?? media.Title; RoomSubtitle.Text = media.Title + " · " + (client.Identity?.Host == true ? "You are hosting" : "Streaming from host");
            await PrepareVideoSurface(openPlayer: RoomPage.Visibility == Visibility.Visible);
            if (client != room || generation != loadGeneration) return;
            player!.Play(vlcMedia);
        }
        catch { if (generation == loadGeneration) loadedMedia = null; throw; }
        finally { loading.Release(); }
    }
    private async Task PrepareVideoSurface(bool openPlayer = true)
    {
        PlayerEmpty.Visibility = Visibility.Collapsed;
        roomPanelVisible = room is not null;
        if (!openPlayer) return;
        ShowPage("Room");
        await Dispatcher.InvokeAsync(() => Video.UpdateLayout(), DispatcherPriority.Render);
        PlayerOverlay.Focus();
    }
    private void PlaybackTick()
    {
        roomLibrary?.ExpireViews(Wire.Now);
        if (fullscreen && PlaybackControls.Visibility == Visibility.Visible &&
            Environment.TickCount64 - lastPlayerInteraction > 2600 && !PlaybackControls.IsMouseOver &&
            !seeking && PlayerOptions.Visibility != Visibility.Visible)
        {
            if (PlaybackControls.IsKeyboardFocusWithin) PlayerOverlay.Focus();
            PlaybackControls.Visibility = FullscreenHint.Visibility = Visibility.Collapsed;
            PlayerOverlay.Cursor = Cursors.None;
        }
        if (player is null) return;
        if (room is not null && ready && buffering.Poll(target?.Playing == true, Environment.TickCount64))
        { try { room.Send(new("buffering")); } catch { } }
        UpdateSessionControls();
        MuteButton.Content = player.Mute ? "\uE74F" : "\uE767";
        MuteButton.ToolTip = player.Mute ? "Unmute (M)" : "Mute (M)";
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, player.Mute ? "Unmute" : "Mute");
        VolumeText.Text = player.Mute ? "Muted" : $"{(int)Volume.Value}%";
        if (pendingSeek is { } requested && (Wire.Now >= seekExpires || (room is null || target?.Revision > seekRevision) && Math.Abs(player.Time - requested) < 1500)) pendingSeek = null;
        if (!seeking)
        {
            Timeline.Maximum = Math.Max(1, player.Length); Timeline.Value = pendingSeek ?? Math.Max(0, player.Time);
            PlaybackTime.Text = FormatTime(player.Time) + " / " + FormatTime(player.Length);
        }
        PlayButton.Content = player.IsPlaying ? "Ⅱ" : "▶";
        PlayButton.ToolTip = player.IsPlaying ? "Pause (Space)" : "Play (Space)";
        if (++tick % 8 == 0 && ready) RefreshTracks();
        if (tick % 20 == 0 && ready) SavePlaybackPosition();
        if (PlaybackDiagnostics.Enabled)
        {
            var serverMs = room?.ServerNowMs ?? Wire.Now;
            PlaybackDiagnostics.Record("sample", new { serverMs, position = player.Time,
                estimated = playbackPosition.Estimate(player.Time, player.IsPlaying, player.Rate, Environment.TickCount64), rate = player.Rate, policy = SyncMath.UseTunedPolicy ? "experimental" : "stable",
                state = player.State.ToString(), playing = player.IsPlaying, ready, revision = target?.Revision, targetPlaying = target?.Playing,
                desired = target is null ? (long?)null : SyncMath.TargetPosition(target, serverMs), connected = room?.IsConnected ?? false });
        }
        ApplyRoomPlayback();
    }
    private void ApplyRoomPlayback()
    {
        if (player is null || room is null || !room.IsConnected || target is null || loadedMedia != target.MediaId || !ready) return;
        // Pause before clock checks, settling, or a potentially slow remote seek.
        if (playbackPause.Pending || !target.Playing) player.SetPause(true);
        if (playbackPause.Pending || seeking || pendingSeek is not null && target.Revision <= seekRevision) return;
        long now = room.ServerNowMs;
        if (now < target.AtUnixMs) { player.SetPause(true); return; }
        long desired = SyncMath.TargetPosition(target, now);
        long drift = desired - playbackPosition.Estimate(player.Time, player.IsPlaying, player.Rate, Environment.TickCount64);
        if (settling.Waiting(target.Revision, Math.Max(0, player.Time), Environment.TickCount64)) return;
        if (appliedRevision != target.Revision || Math.Abs(drift) > SyncMath.HardSeekMs)
        {
            if ((player.State is VLCState.Ended or VLCState.Stopped) && (target.Playing || desired < player.Length - 250))
            {
                // Like local replay, reset an ended input before starting it again.
                // Native Playing will mark it ready; apply this revision afterward.
                var previous = player.State.ToString(); ready = false; player.Stop();
                var restarted = player.Play();
                if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("restart", new { revision = target.Revision, previous, restarted, desired });
                if (!restarted) SetStatus("Playback could not restart. Reopen the video to try again.");
                return;
            }
            if (target.Playing && appliedRevision != target.Revision) settling.Seek(target.Revision, desired, Environment.TickCount64);
            if (Math.Abs(drift) > 200)
            {
                if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("seek", new { serverMs = now, revision = target.Revision, position = player.Time, desired, drift });
                settling.Seek(target.Revision, desired, Environment.TickCount64); player.Time = desired;
            }
            playbackRate.Reset();
            player.SetPause(!target.Playing); ApplyPlaybackRate(1); appliedRevision = target.Revision;
            if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("applied", new { revision = appliedRevision, serverMs = now, desired, drift });
        }
        else if (target.Playing) ApplyPlaybackRate(SyncMath.UseTunedPolicy ? SyncMath.Correction(drift) : playbackRate.Update(drift, Environment.TickCount64));
    }
    private void ApplyPlaybackRate(float rate)
    {
        if (player is null || Math.Abs(player.Rate - rate) < .001f) return;
        var result = player.SetRate(rate);
        if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("rate", new { requested = rate, actual = player.Rate, result });
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(milliseconds >= 3600000 ? @"h\:mm\:ss" : @"mm\:ss");
    private void SendPlayback(bool playing, long position)
    {
        if (room?.Snapshot?.Media is not { } media) return;
        if (room.Identity?.Host != true && room.Snapshot.SharedControls != true) { SetStatus("The host controls playback."); return; }
        var command = new PlaybackState(0, media.Id, playing, position, 0, Guid.NewGuid().ToString("N"));
        room.Send(new("playback", Data: Wire.Serialize(command)));
        playbackPause.Request(command, target?.Revision ?? -1);
        if (!playing)
        {
            player?.SetPause(true);
            PlayButton.Content = "▶";
        }
        if (PlaybackDiagnostics.Enabled) PlaybackDiagnostics.Record("control-request", new { command.CommandId, playing, position, revision = target?.Revision });
    }
    private void TogglePlayback(object sender, RoutedEventArgs e)
    {
        videoClickDelay.Stop();
        if (player is null || PlayerEmpty.Visibility == Visibility.Visible) return;
        if (room is not null) SendPlayback(!player.IsPlaying, player.Length > 0 && player.Time >= player.Length - 250 ? 0 : Math.Max(0, player.Time));
        else if (player.IsPlaying) player.SetPause(true); else { if (player.State == VLCState.Ended) player.Stop(); player.Play(); }
    }
    private bool CanSeek() => player is not null && player.Length > 0 && (room is null || room.Identity?.Host == true || room.Snapshot?.SharedControls == true);
    private void SeekStarted(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!CanSeek()) { SetStatus("The host controls playback, or the video is not ready to seek."); return; }
        seeking = true; Timeline.CaptureMouse(); UpdateScrub(e.GetPosition(Timeline).X);
    }
    private void UpdateScrub(double x)
    {
        // Account for the thumb inset so both ends remain reachable.
        var fraction = Math.Clamp((x - 8) / Math.Max(1, Timeline.ActualWidth - 16), 0, 1);
        Timeline.Value = fraction * Timeline.Maximum;
        PlaybackTime.Text = FormatTime((long)Timeline.Value) + " / " + FormatTime(player?.Length ?? 0);
    }
    private void SeekMoved(object sender, MouseEventArgs e) { if (seeking && Timeline.IsMouseCaptured) { UpdateScrub(e.GetPosition(Timeline).X); e.Handled = true; } }
    private void CommitSeek()
    {
        if (!seeking) return;
        seeking = false;
        if (CanSeek())
        {
            var position = (long)Timeline.Value;
            pendingSeek = position; seekRevision = target?.Revision ?? -1; seekExpires = Wire.Now + 5000;
            if (room is null) player!.Time = position; else SendPlayback(target?.Playing == true, position);
        }
        if (Timeline.IsMouseCaptured) Timeline.ReleaseMouseCapture();
    }
    private void SeekFinished(object sender, MouseButtonEventArgs e) { if (seeking) { UpdateScrub(e.GetPosition(Timeline).X); CommitSeek(); e.Handled = true; } }
    private void SeekCaptureLost(object sender, MouseEventArgs e) => CommitSeek();
    private void SeekKeyStarted(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        e.Handled = true; if (!CanSeek()) return; seeking = true;
        Timeline.Value = e.Key switch { Key.Home => 0, Key.End => Timeline.Maximum, Key.Left => Math.Max(0, Timeline.Value - 5000), _ => Math.Min(Timeline.Maximum, Timeline.Value + 5000) };
    }
    private void SeekKeyFinished(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) { CommitSeek(); e.Handled = true; } }
    private void MovePlayback(long offset) { if (player is null || player.Length <= 0) return; var position = Math.Clamp(player.Time + offset, 0, player.Length); if (room is null) player.Time = position; else SendPlayback(target?.Playing == true, position); }
    private void SkipBackward(object sender, RoutedEventArgs e) => MovePlayback(-10000);
    private void SkipForward(object sender, RoutedEventArgs e) => MovePlayback(10000);
    private void StopPlayback(object sender, RoutedEventArgs e) { if (player is null) return; if (room is null) { player.Stop(); ready = false; } else SendPlayback(false, 0); }
    private void MutePlayback(object sender, RoutedEventArgs e) { if (player is not null) player.Mute = !player.Mute; }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player is not null) { player.Volume = (int)e.NewValue; if (e.NewValue > 0) player.Mute = false; } }
    private void RefreshTracks()
    {
        if (player is null) return; tracksUpdating = true;
        try
        {
            var audio = player.AudioTrackDescription.Select(x => new TrackChoice(x.Id, x.Name)).ToArray();
            var subs = player.SpuDescription.Select(x => new TrackChoice(x.Id, x.Name)).ToArray();
            if (!(AudioTracks.ItemsSource is TrackChoice[] currentAudio) || !currentAudio.SequenceEqual(audio)) AudioTracks.ItemsSource = audio;
            if (!(SubtitleTracks.ItemsSource is TrackChoice[] currentSubs) || !currentSubs.SequenceEqual(subs)) SubtitleTracks.ItemsSource = subs;
            AudioTracks.SelectedItem = audio.FirstOrDefault(x => x.Id == player.AudioTrack);
            SubtitleTracks.SelectedItem = subs.FirstOrDefault(x => x.Id == player.Spu);
            AudioTracks.IsEnabled = audio.Length > 0; SubtitleTracks.IsEnabled = subs.Length > 0;
            var audioCount = audio.Count(x => x.Id >= 0); var subtitleCount = subs.Count(x => x.Id >= 0);
            TrackSummary.Text = $"{audioCount} audio {(audioCount == 1 ? "track" : "tracks")} · {subtitleCount} subtitle {(subtitleCount == 1 ? "track" : "tracks")}. External subtitles can be loaded below.";
        }
        finally { tracksUpdating = false; }
    }
    private void AudioChanged(object sender, SelectionChangedEventArgs e) { if (!tracksUpdating && AudioTracks.SelectedItem is TrackChoice t) player?.SetAudioTrack(t.Id); }
    private void SubtitlesChanged(object sender, SelectionChangedEventArgs e) { if (!tracksUpdating && SubtitleTracks.SelectedItem is TrackChoice t) player?.SetSpu(t.Id); }
    private void LoadSubtitles(object sender, RoutedEventArgs e)
    {
        if (player is null || !ready) return;
        var picker = new OpenFileDialog { Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub" };
        if (picker.ShowDialog(this) == true)
        {
            if (!player.AddSlave(MediaSlaveType.Subtitle, new Uri(picker.FileName).AbsoluteUri, true)) SetStatus("Subtitles could not be loaded. Check the file format and try again.");
            else { RefreshTracks(); SetStatus("Subtitles loaded for your player."); }
        }
    }
    private void CopyInvite(object sender, RoutedEventArgs e)
    {
        if (room?.IsConnected != true || room.Identity is null) { SetStatus("Create a room from the library first."); return; }
        try { Clipboard.SetText(room.ServerAddress + "#" + room.Identity.Room); SetStatus("Invitation copied. Guests need the Windows app and host approval."); }
        catch (ExternalException) { SetStatus("Clipboard is busy. Try Copy invite again."); }
    }
    private void ShowRoomFeedback(string text)
    {
        RoomFeedback.Text = text; RoomFeedback.Visibility = Visibility.Visible;
    }
    private void RefreshPeople(RoomSnapshot? snapshot = null)
    {
        snapshot ??= room?.Snapshot;
        if (snapshot is null) return;
        PeopleList.ItemsSource = snapshot.People.Select(p => new
        {
            p.Id, p.Name,
            Status = !p.Approved ? (pendingAdmissions.ContainsKey(p.Id) ? "Waiting for admission confirmation…" : "Waiting for approval")
                : (p.IsHost ? "Host · " : "Admitted · ") + (p.Ready ? "Ready" : "Buffering"),
            Actions = room?.Identity?.Host == true && !p.IsHost ? Visibility.Visible : Visibility.Collapsed,
            AdmitVisibility = p.Approved ? Visibility.Collapsed : Visibility.Visible,
            AdmitLabel = pendingAdmissions.ContainsKey(p.Id) ? "Admitting…" : "Admit",
            CanAdmit = !pendingAdmissions.ContainsKey(p.Id)
        }).ToList();
        RefreshRoomSettings();
    }
    private async void Admit(object sender, RoutedEventArgs e)
    {
        var client = room;
        if (client?.Identity?.Host != true || ((Button)sender).Tag is not string id || pendingAdmissions.ContainsKey(id)) return;
        if (client.Snapshot?.People.Count(p => p.Approved) >= 5)
        {
            ShowRoomFeedback("The room is full (5 participants). Remove someone before admitting another guest."); return;
        }
        var request = Guid.NewGuid();
        pendingAdmissions[id] = request;
        ShowRoomFeedback("Admitting guest…"); RefreshPeople();
        try
        {
            client.Send(new("admit", Target: id));
            await Task.Delay(TimeSpan.FromSeconds(10), lifetime.Token);
            if (room == client && pendingAdmissions.GetValueOrDefault(id) == request)
            {
                pendingAdmissions.Remove(id); RefreshPeople();
                ShowRoomFeedback("Admission has not been confirmed. Check the connection and try again.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (room != client || pendingAdmissions.GetValueOrDefault(id) != request) return;
            pendingAdmissions.Remove(id); RefreshPeople(); ShowRoomFeedback(ex.Message);
        }
    }
    private void RemovePeer(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = (string)((Button)sender).Tag;
            if (room?.Snapshot?.People.Any(p => p.Id == id && p.Approved) == true) roomLibrary?.SetAccess(id, new());
            room?.Send(new("remove", Target: id));
        }
        catch (IOException ex) { ShowRoomFeedback(ex.Message); SetStatus(ex.Message); }
    }
    private void ControlsChanged(object sender, RoutedEventArgs e)
    {
        if (room?.Identity?.Host == true) room.Send(new("controls", Number: SharedControls.IsChecked == true ? 1 : 0));
    }
    private void SendChat(object sender, RoutedEventArgs e)
    {
        if (room?.IsConnected != true || string.IsNullOrWhiteSpace(ChatInput.Text)) return;
        room.Send(new("chat", Text: ChatInput.Text.Trim())); ChatInput.Clear();
    }
    private void ChatKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SendChat(sender, e); e.Handled = true; } }
    private void QueueClick(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        if (!File.Exists(selected.Path)) { SetStatus("This file is unavailable. Reconnect its drive and rescan."); return; }
        if (room?.Identity?.Host == false) { room.Send(new("chat", Text: "Suggestion: " + selected.Title)); SetStatus("Suggested title to the room"); return; }
        ShowAddToQueueMenu(DetailQueue, [selected]);
    }

    private void PublishQueue()
    {
        var titles = queue.Select(PlaybackTitle).ToArray(); QueueStatus.Text = titles.Length == 0 ? "Queue is empty." : queuePosition >= 0 && queuePosition < queue.Count ? $"{queuePosition + 1} / {queue.Count} · {titles[queuePosition]}" : $"{queue.Count} videos";
        QueueList.ItemsSource = queue.ToArray(); QueueList.SelectedIndex = queuePosition; UpdateSessionControls();
        library.Setting("queuePosition", queuePosition.ToString());
        library.Setting("queue", Wire.Serialize(queue.Select(x => x.Id).ToArray()));
        if (room?.Identity?.Host == true) room.Send(new("queue", Data: Wire.Serialize(titles.Take(150).ToArray())));
    }
    private async void NextQueued(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (roomLibrary?.Queue.Entries.Length > 0) { roomLibrary.Request(HostPeerId(), "next"); return Task.CompletedTask; }
        return PlayQueueStep(1);
    });
    private async void PreviousQueued(object sender, RoutedEventArgs e) => await Guard(() => PlayQueueStep(-1));
    private async Task PlayQueueStep(int direction)
    {
        if (!CanEditQueue()) return;
        var index = QueuePlayback.FindNext(queue, queuePosition, direction, x => File.Exists(x.Path));
        if (index < 0) { SetStatus(direction > 0 ? "End of queue." : "Start of queue."); return; }
        queuePosition = index; selected = queue[index];
        if (room is null) await PlayLocalItem(selected);
        else { playingItem = selected; room.SetHostedFile(selected.Path, PlaybackTitle(selected)); room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); ShowPage("Room"); }
        PublishQueue();
    }
    private async void LeaveRoom(object sender, RoutedEventArgs e) => await Guard(async () => { var wasRoom = room is not null; await Disconnect(); player?.Stop(); RoomHeading.Text = "Your watch room"; RoomSubtitle.Text = "Room closed"; ShowPage(wasRoom ? "Rooms" : "Library"); });
    private async Task Disconnect()
    {
        libraryWindow?.Shutdown(); libraryWindow = null; roomLibrary = null; sharedItems.Clear(); sharedIds.Clear(); sharedKinds = [];
        videoClickDelay.Stop();
        SavePlaybackPosition(); localResume = 0;
        if (fullscreen) SetFullscreen(false);
        PlayerEmpty.Visibility = Visibility.Visible;
        pendingSeek = null; seeking = false; playbackPause.Clear(); ++loadGeneration; var old = room; room = null; target = null; loadedMedia = null; ready = false;
        player?.Stop(); if (old is not null) await old.DisposeAsync();
        playingItem = null;
        await loading.WaitAsync();
        try { if (bridge is not null) await bridge.DisposeAsync(); bridge = null; } finally { loading.Release(); }
        PeopleList.ItemsSource = null; pendingAdmissions.Clear(); RoomFeedback.Text = ""; RoomFeedback.Visibility = Visibility.Collapsed;
        RoomTabs.SelectedIndex = 0; chat.Clear(); SetThreadExecutionState(0x80000000); ConnectionStatus.Text = "Local playback";
        guestBrowsePending = false; ++guestBrowseRequest; RefreshSharedLibrary();
        UpdateSessionControls();
    }
    private async void PrepareCopy(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (playingItem is null || room?.Identity?.Host == false || preparing) return;
        var exe = FfmpegBox.Text.Trim();
        if (!File.Exists(exe)) { ShowPage("Settings"); SetStatus("Choose an FFmpeg executable in Settings to prepare a smaller copy."); return; }
        var item = playingItem; var preparingRoom = room; var generation = loadGeneration;
        preparing = true; PrepareButton.IsEnabled = false;
        try {
        var dir = Path.Combine(App.DataDirectory, "prepared"); Directory.CreateDirectory(dir);
        var output = Path.Combine(dir, item.Id + "-" + File.GetLastWriteTimeUtc(item.Path).Ticks + "-720p.mkv");
        if (!File.Exists(output))
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-nostdin", "-y", "-i", item.Path, "-map", "0:v:0", "-map", "0:a:0?", "-map", "0:s?", "-map", "0:t?", "-vf", "scale=-2:min(720\\,ih)", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-maxrate", "3000k", "-bufsize", "6000k", "-c:a", "aac", "-b:a", "160k", "-c:s", "copy", "-c:t", "copy", "-f", "matroska", output + ".partial" }) info.ArgumentList.Add(arg);
            SetStatus("Preparing a 720p copy. Original file stays unchanged; playback can start when preparation finishes.");
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync(lifetime.Token);
            try { await process.WaitForExitAsync(lifetime.Token); } catch { if (!process.HasExited) process.Kill(true); throw; }
            var log = await errors; if (process.ExitCode != 0) throw new IOException("FFmpeg could not prepare this file: " + log[^Math.Min(600, log.Length)..]);
            File.Move(output + ".partial", output, true);
        }
        if (generation != loadGeneration || room != preparingRoom || playingItem != item) { SetStatus("720p copy saved. Playback changed during preparation."); return; }
        if (room?.Identity?.Host == true) { room.SetHostedFile(output, PlaybackTitle(item) + " · 720p"); room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); }
        else { await PrepareVideoSurface(); using var media = new Media(vlc!, output, FromType.FromPath); player?.Play(media); }
        SetStatus("720p copy ready.");
        } finally { preparing = false; UpdateSessionControls(); }
    });
    private static string PlaybackTitle(MediaItem item) => item.QueueTitle;
    private void RemoveQueued(object sender, RoutedEventArgs e)
    {
        if (room is not null && room.Identity?.Host != true) return;
        if (QueueList.SelectedIndex < 0) return;
        var removedIndex = QueueList.SelectedIndex;
        queue.RemoveAt(removedIndex);
        if (removedIndex <= queuePosition) queuePosition--;
        SaveActiveQueueEdits(); PublishQueue();
    }
    private void QueueSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSessionControls();
    private void MoveQueued(object sender, RoutedEventArgs e)
    {
        if (room is not null && room.Identity?.Host != true) return;
        var index = QueueList.SelectedIndex;
        var destination = index + (((string)((Button)sender).Tag) == "Earlier" ? -1 : 1);
        if (index < 0 || destination < 0 || destination >= queue.Count) return;
        var reordered = queue.ToArray(); (reordered[index], reordered[destination]) = (reordered[destination], reordered[index]);
        queue.Clear(); queue.AddRange(reordered);
        if (queuePosition == index) queuePosition = destination; else if (queuePosition == destination) queuePosition = index;
        SaveActiveQueueEdits(); PublishQueue(); QueueList.SelectedIndex = destination;
    }
    private void UpdateSessionControls()
    {
        if (InviteButton is null || QueueList is null) return;
        var connectedRoom = room?.IsConnected == true;
        SharedControls.Visibility = connectedRoom && room!.Identity?.Host == true ? Visibility.Visible : Visibility.Collapsed;
        SharedControls.IsEnabled = connectedRoom && room!.Identity?.Host == true;
        NowPlayingStrip.Visibility = ready || room is not null ? Visibility.Visible : Visibility.Collapsed;
        NowPlayingLabel.Text = "Now watching · " + RoomHeading.Text;
        var canControl = room is null || connectedRoom && (room!.Identity?.Host == true || room.Snapshot?.SharedControls == true);
        InviteButton.Visibility = RoomToggle.Visibility = connectedRoom ? Visibility.Visible : Visibility.Collapsed;
        RoomLibraryButton.Visibility = connectedRoom ? Visibility.Visible : Visibility.Collapsed;
        RoomLibraryButton.Content = room?.Identity?.Host == true ? "Library & guest access" : "Host library";
        LeaveButton.Content = room is null ? "Close player" : "Leave room";
        ChatInput.IsEnabled = SendChatButton.IsEnabled = connectedRoom && (room!.Identity?.Host == true || room.Snapshot?.People.Any(p => p.Id == room.Identity?.Peer && p.Approved) == true);
        PlayButton.IsEnabled = StopButton.IsEnabled = canControl && (ready || playingItem is not null && player?.State is VLCState.Stopped or VLCState.Ended);
        Timeline.IsEnabled = BackwardButton.IsEnabled = ForwardButton.IsEnabled = canControl && player?.Length > 0;
        NextButton.IsEnabled = roomLibrary?.Queue.Entries.Length > 0
            ? connectedRoom && (room!.Identity?.Host == true || roomLibrary.OwnAccess.Start)
            : QueuePlayback.FindNext(queue, queuePosition, 1, x => File.Exists(x.Path)) >= 0 && (room is null || connectedRoom && room!.Identity?.Host == true);
        PreviousButton.IsEnabled = QueuePlayback.FindNext(queue, queuePosition, -1, x => File.Exists(x.Path)) >= 0 && (room is null || connectedRoom && room!.Identity?.Host == true);
        ReturnToRoomButton.IsEnabled = ready || room is not null;
        RemoveQueueButton.IsEnabled = QueueList.SelectedIndex >= 0 && (room is null || connectedRoom && room!.Identity?.Host == true);
        QueueEarlier.IsEnabled = RemoveQueueButton.IsEnabled && QueueList.SelectedIndex > 0;
        QueueLater.IsEnabled = RemoveQueueButton.IsEnabled && QueueList.SelectedIndex < queue.Count - 1;
        PrepareButton.IsEnabled = playingItem is not null && !preparing && (room is null || connectedRoom && room!.Identity?.Host == true);
        LoadSubtitlesButton.IsEnabled = ready;
    }
    private void Fullscreen(object sender, RoutedEventArgs e)
    {
        SetFullscreen(!fullscreen);
    }
    private void ApplyPlayerLayout()
    {
        bool inPlayer = RoomPage.Visibility == Visibility.Visible;
        Rail.Visibility = inPlayer ? Visibility.Collapsed : Visibility.Visible;
        RailWidth.Width = new GridLength(inPlayer ? 0 : 208);
        ContentArea.Margin = inPlayer ? new Thickness(0) : new Thickness(30, 24, 30, 18);
        AppStatusBar.Visibility = inPlayer ? Visibility.Collapsed : Visibility.Visible;
        StatusRow.Height = new GridLength(inPlayer ? 0 : 34);
        RoomHeader.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        RoomSide.Visibility = roomPanelVisible && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        var showRoomPanel = roomPanelVisible && !fullscreen;
        RoomSplitter.Visibility = showRoomPanel ? Visibility.Visible : Visibility.Collapsed;
        RoomSplitterWidth.Width = new GridLength(showRoomPanel ? 6 : 0);
        RoomSideWidth.MinWidth = showRoomPanel ? 280 : 0;
        RoomSideWidth.Width = new GridLength(showRoomPanel ? roomPanelWidth : 0);
        RoomToggle.ToolTip = roomPanelVisible ? "Hide participants and chat" : "Show participants and chat";
    }
    private void RoomPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (roomPanelVisible && !fullscreen && RoomSideWidth.ActualWidth >= 280)
            roomPanelWidth = Math.Clamp(RoomSideWidth.ActualWidth, 280, 600);
    }
    private void SetFullscreen(bool value)
    {
        if (fullscreen == value || RoomPage.Visibility != Visibility.Visible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (value)
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
            previousState = WindowState;
            previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            previousResizeMode = ResizeMode;
            fullscreen = true;
            // Use the entire monitor, including the taskbar area. Maximizing uses the work area.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            DockedControlsHost.Children.Remove(PlaybackControls);
            FullscreenControlsHost.Children.Add(PlaybackControls);
            ApplyPlayerLayout();
            SetWindowPos(handle, IntPtr.Zero, monitor.Monitor.Left, monitor.Monitor.Top,
                monitor.Monitor.Right - monitor.Monitor.Left, monitor.Monitor.Bottom - monitor.Monitor.Top, 0x0020 | 0x0040);
        }
        else
        {
            fullscreen = false;
            FullscreenControlsHost.Children.Remove(PlaybackControls);
            DockedControlsHost.Children.Add(PlaybackControls);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = previousResizeMode;
            Left = previousBounds.Left; Top = previousBounds.Top;
            Width = previousBounds.Width; Height = previousBounds.Height;
            WindowState = previousState;
            ApplyPlayerLayout();
        }
        PlayerOptions.Visibility = Visibility.Collapsed;
        FullscreenButton.Content = fullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F or double-click video)";
        System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, fullscreen ? "Exit fullscreen" : "Fullscreen");
        ShowPlayerControls();
        PlayerOverlay.Focus();
    }
    private void ToggleRoomPanel(object sender, RoutedEventArgs e) { roomPanelVisible = !roomPanelVisible; ApplyPlayerLayout(); }
    private void TogglePlayerOptions(object sender, RoutedEventArgs e)
    {
        PlayerOptions.Visibility = PlayerOptions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowPlayerControls();
    }
    private void ShowPlayerControls()
    {
        lastPlayerInteraction = Environment.TickCount64;
        PlaybackControls.Visibility = Visibility.Visible;
        FullscreenHint.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        PlayerOverlay.Cursor = Cursors.Arrow;
    }
    private void PlayerMouseMoved(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(PlayerOverlay);
        if ((position - lastPlayerMouse).Length < 2) return;
        lastPlayerMouse = position;
        if (fullscreen) ShowPlayerControls();
    }
    private void ControlsMouseMoved(object sender, MouseEventArgs e) { if (fullscreen) ShowPlayerControls(); }
    private void ControlsKeyPressed(object sender, KeyEventArgs e) { if (fullscreen) ShowPlayerControls(); }
    private void VideoClicked(object sender, MouseButtonEventArgs e)
    {
        // Controls and menus inside the native-video overlay keep their own clicks.
        if (e.OriginalSource != PlayerOverlay) return;
        PlayerOverlay.Focus();
        ShowPlayerControls();
        videoClickDelay.Stop();
        if (e.ClickCount == 2) SetFullscreen(!fullscreen);
        else if (e.ClickCount == 1)
        {
            // Wait through the system double-click interval so fullscreen does not
            // also pause playback. Discard the click if the video changes meanwhile.
            videoClickGeneration = loadGeneration;
            videoClickDelay.Start();
        }
        e.Handled = true;
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key is Key.BrowserBack or Key.BrowserForward || Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        { BrowseHistory(e.Key == Key.BrowserBack || e.SystemKey == Key.Left); e.Handled = true; return; }
        if (e.Key == Key.Escape && fullscreen) { SetFullscreen(false); e.Handled = true; return; }
        if (fullscreen && e.Key == Key.Tab) ShowPlayerControls();
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !fullscreen)
        {
            ShowPage("Library");
            var search = GuestLibrary ? GuestLibrarySearch : SearchBox;
            search.Focus(); search.SelectAll(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape && LibraryPage.Visibility == Visibility.Visible && SearchBox.IsKeyboardFocused)
        { SearchBox.Clear(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox || RoomPage.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Space && Keyboard.FocusedElement is Button) return;
        if (e.Key is Key.F or Key.F11) { Fullscreen(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not Slider && e.Key is Key.Left or Key.Right) { MovePlayback(e.Key == Key.Left ? -10000 : 10000); e.Handled = true; }
        else if (e.Key == Key.M) { MutePlayback(sender, e); e.Handled = true; }
        else if (e.Key == Key.S) { StopPlayback(sender, e); e.Handled = true; }
        else if (e.Key == Key.P) { PreviousQueued(sender, e); e.Handled = true; }
        else if (e.Key == Key.N) { NextQueued(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not Slider && e.Key is Key.Up or Key.Down) { Volume.Value = Math.Clamp(Volume.Value + (e.Key == Key.Up ? 5 : -5), 0, 100); e.Handled = true; }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        light = !light;
        library.Setting("theme", light ? "light" : "dark");
        ApplyTheme();
    }
    private void ApplyTheme()
    {
        var colors = light ? new[] { "#F4F5FA", "#FFFFFF", "#EAECF4", "#202433", "#586175", "#D6DAE7", "#6950C9", "#FFFFFF" } : new[] { "#101218", "#191C26", "#232736", "#F2F1F8", "#A5ABBE", "#303547", "#BEABFF", "#241A40" };
        var keys = new[] { "Canvas", "Surface", "Elevated", "Ink", "Muted", "Line", "Accent", "AccentInk" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        ApplyTitleBarTheme();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(NavigationWindowMessage);
        ApplyTitleBarTheme();
        Activated += (_, _) => ApplyTitleBarTheme();
        Deactivated += (_, _) => ApplyTitleBarTheme();
    }
    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        // Native chrome keeps Windows drag, resize, snap, and caption buttons.
        // Unsupported attributes on older Windows versions leave the system defaults.
        uint dark = !light && !SystemParameters.HighContrast ? 1u : 0u;
        DwmSetWindowAttribute(handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(uint));
        uint ColorRef(string resource)
        {
            if (SystemParameters.HighContrast) return uint.MaxValue; // DWMWA_COLOR_DEFAULT
            var color = ((SolidColorBrush)Application.Current.Resources[resource]).Color;
            return (uint)(color.R | color.G << 8 | color.B << 16);
        }
        var caption = ColorRef("Canvas"); var text = ColorRef(IsActive ? "Ink" : "Muted"); var border = ColorRef("Line");
        DwmSetWindowAttribute(handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(uint));
        DwmSetWindowAttribute(handle, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(uint));
        DwmSetWindowAttribute(handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(uint));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true; closing = true; lifetime.Cancel(); timer.Stop(); scanDelay.Stop(); videoClickDelay.Stop();
        guestBrowseTimer.Stop(); libraryUpdateTimer.Stop(); updateTimer.Stop(); library.MediaSaved -= QueueLibraryUpdate;
        library.Setting("volume", ((int)Volume.Value).ToString());
        await Task.Yield(); // Let WPF finish the first Closing event before calling Close again.
        foreach (var watcher in watchers) watcher.Dispose();
        try { await Disconnect(); } finally { Video.MediaPlayer = null; player?.Dispose(); vlc?.Dispose(); Close(); }
    }
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
