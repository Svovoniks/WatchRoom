using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class MainWindow
{
    private string HostLibraryName => (room?.Snapshot?.People.FirstOrDefault(person => person.IsHost)?.Name ?? "Host") + "’s library";
    private string? guestHoveredId;
    private readonly DispatcherTimer guestViewDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private bool guestViewDelayInitialized;
    private void ScheduleGuestView()
    {
        if (!GuestLibrary || !OwnsLibrary) return;
        if (!guestViewDelayInitialized) { guestViewDelay.Tick += (_, _) => { guestViewDelay.Stop(); SendGuestView(); }; guestViewDelayInitialized = true; }
        if (!guestViewDelay.IsEnabled) guestViewDelay.Start();
    }
    private void SendGuestView()
    {
        if (!GuestLibrary || !OwnsLibrary || currentPage is not ("Library" or "Details")) return;
        var scroll = LibraryScroll;
        var fraction = scroll.ScrollableHeight > 0 ? Math.Clamp(scroll.VerticalOffset / scroll.ScrollableHeight, 0, 1) : 0;
        TryLibraryCommand(new("catalog-follow", Data: Wire.Serialize(new LibraryBrowseView(guestBrowse with { Page = 0 },
            guestLibraryCards.Count, fraction, guestHoveredId, currentPage == "Details" ? selected?.Id : null))));
    }
    private void LibraryCardHoverEnter(object sender, MouseEventArgs e)
    {
        if (GuestLibrary && sender is Button { Tag: LibraryCard card }) { guestHoveredId = card.Media.Id; ScheduleGuestView(); }
    }
    private void LibraryCardHoverLeave(object sender, MouseEventArgs e)
    {
        if (GuestLibrary) { guestHoveredId = null; ScheduleGuestView(); }
    }
    private bool guestHomeResume;
    private async void OpenGuestVideo(LibraryCard card, bool resume = false)
    {
        guestHomeResume = resume;
        selected = card.Media; ShowPage("Details"); UpdateHostBreadcrumbs();
        DetailTitle.Text = card.DisplayTitle; DetailMeta.Text = card.Caption; DetailContext.Text = HostLibraryName;
        DetailSummary.Text = card.Media.Overview ?? ""; DetailFile.Text = ""; DetailTracks.Visibility = Visibility.Collapsed;
        DetailAvailability.Visibility = Visibility.Collapsed;
        DetailPlay.Content = "Play in room"; DetailPlay.IsEnabled = OwnsLibrary;
        DetailHost.Visibility = DetailWatched.Visibility = RestartButton.Visibility = DetailRemove.Visibility = Visibility.Collapsed;
        // The host's videos can go into the host's queues.
        DetailQueue.Visibility = ShowingHostQueues ? Visibility.Visible : Visibility.Collapsed;
        Episodes.Visibility = DetailVersions.Visibility = DetailExtrasSection.Visibility = Visibility.Collapsed; DetailPoster.Source = null;
        ScheduleGuestView();
        var view = guestLibraryCards.FirstOrDefault(view => view.Card == card);
        if (view is not null) { await view.LoadPosterAsync(); if (selected?.Id == card.Media.Id && GuestLibrary) DetailPoster.Source = view.Poster; }
        if (selected?.Id == card.Media.Id && GuestLibrary) await UpgradeGuestDetailArtwork(card);
    }
    private void WatchGuestVideo()
    {
        if (!OwnsLibrary || selected is null) return;
        TryLibraryCommand(new("catalog-watch", Target: selected.Id, Number: ++guestBrowseRequest, Text: guestHomeResume ? "resume" : null));
        SetStatus("Starting " + selected.Title + " in the room…");
    }
    private long hostArtworkSequence;
    private readonly SemaphoreSlim hostArtworkWorkers = new(2);
    private readonly Dictionary<string, Task<string?>> hostArtworkCache = [];
    // Full HD copies are large; keep only the most recent few.
    private readonly Dictionary<string, Task<string?>> hostFullArtworkCache = [];
    private readonly Queue<string> hostFullArtworkOrder = new();
    private sealed class ArtworkDownload(string key)
    {
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Key { get; } = key;
        public SortedDictionary<int, string> Chunks { get; } = [];
        public long Started { get; } = Environment.TickCount64;
    }
    private readonly Dictionary<long, ArtworkDownload> hostArtworkPending = [];
    private Task<string?> LoadHostArtwork(LibraryCard card)
    {
        var key = card.Media.Id + ":" + card.Level + ":" + card.Season;
        if (hostArtworkCache.TryGetValue(key, out var cached)) return cached;
        return hostArtworkCache[key] = FetchHostArtwork(card, key, false);
    }
    private Task<string?> LoadHostFullArtwork(LibraryCard card)
    {
        var key = card.Media.Id + ":" + card.Level + ":" + card.Season;
        if (hostFullArtworkCache.TryGetValue(key, out var cached)) return cached;
        while (hostFullArtworkOrder.Count >= 6) hostFullArtworkCache.Remove(hostFullArtworkOrder.Dequeue());
        hostFullArtworkOrder.Enqueue(key);
        return hostFullArtworkCache[key] = FetchHostArtwork(card, key, true);
    }
    private async Task<string?> FetchHostArtwork(LibraryCard card, string key, bool fullHd)
    {
        await hostArtworkWorkers.WaitAsync();
        var sequence = -++hostArtworkSequence;
        try
        {
            if (!OwnsLibrary) return null;
            var download = new ArtworkDownload(key);
            hostArtworkPending[sequence] = download;
            TryLibraryCommand(new("catalog-artwork", Number: sequence, Data: Wire.Serialize(new LibraryArtworkRequest(card.Media.Id, card.Level, card.Season, fullHd))));
            try
            {
                var data = await download.Completion.Task.WaitAsync(TimeSpan.FromSeconds(fullHd ? 30 : 15));
                if (data is { Length: > 0 }) artworkBandwidth.Record(data.Length, Environment.TickCount64 - download.Started);
                // An empty full HD reply means the host is busy with video; try again later.
                else if (fullHd) { fullArtworkDeclinedUntil = Environment.TickCount64 + 30000; RemoveFullArtwork(key); }
                return data is { Length: > 0 } ? data : null;
            }
            catch (TimeoutException) { if (fullHd) { artworkBandwidth.Slowed(); RemoveFullArtwork(key); } else hostArtworkCache.Remove(key); return null; }
        }
        finally { hostArtworkPending.Remove(sequence); hostArtworkWorkers.Release(); }
    }
    private void RemoveFullArtwork(string key) => hostFullArtworkCache.Remove(key);
    private void ReceiveHostArtwork(WireMessage message)
    {
        if (!OwnsLibrary || !hostArtworkPending.TryGetValue(message.Number, out var pending)) return;
        var chunk = Wire.Read<LibraryArtworkChunk>(message.Data!);
        if (chunk.Key != pending.Key || !ArtworkTransfer.ValidChunk(chunk)) return;
        pending.Chunks[chunk.Index] = chunk.Data;
        if (chunk.Paced) AcknowledgeArtwork(message.Number);
        if (pending.Chunks.Count == chunk.Count) pending.Completion.TrySetResult(string.Concat(pending.Chunks.Values));
    }
    private bool ReceiveArtworkError(WireMessage message)
    {
        if (message.Type != "catalog-error" || !hostArtworkPending.TryGetValue(message.Number, out var pending)) return false;
        pending.Completion.TrySetResult(null); return true;
    }
    private void ClearHostArtworkRequests()
    {
        guestViewDelay.Stop();
        foreach (var pending in hostArtworkPending.Values) pending.Completion.TrySetResult(null);
        hostArtworkPending.Clear(); hostArtworkCache.Clear(); hostFullArtworkCache.Clear(); hostFullArtworkOrder.Clear();
    }

    // Large artwork is an upgrade: fetch it only when earlier transfers were fast and the room's video has buffer to spare.
    private readonly ArtworkBandwidth artworkBandwidth = new();
    private long fullArtworkDeclinedUntil;
    private bool CanUpgradeArtwork()
    {
        if (room?.IsConnected != true || Environment.TickCount64 < fullArtworkDeclinedUntil || !artworkBandwidth.AllowsFullHd()) return false;
        if (bridge is not { } stream || target?.WantsPlayback != true) return true;
        if (room.Snapshot?.Media is not { } media || player is not { Length: > 0 } native) return false;
        var wanted = StreamingBufferOptions.ReadAheadBytes(activeStreamingBufferSeconds, media.Length, native.Length);
        return stream.BufferedAheadBytes >= wanted / 2;
    }
    private void AcknowledgeArtwork(long number)
    {
        try { room?.Send(new("artwork-ack", Number: number)); }
        catch (IOException) { }
    }
    private async Task UpgradeGuestDetailArtwork(LibraryCard card)
    {
        if (!CanUpgradeArtwork()) return;
        var data = await LoadHostFullArtwork(card);
        if (data is null) return;
        var image = await Task.Run(() => DecodeArtwork(data));
        if (image is not null && GuestLibrary && currentPage == "Details" && selected?.Id == card.Media.Id) DetailPoster.Source = image;
    }
    private static System.Windows.Media.Imaging.BitmapSource? DecodeArtwork(string data)
    {
        try
        {
            using var stream = new System.IO.MemoryStream(Convert.FromBase64String(data));
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or NotSupportedException or ArgumentException) { return null; }
    }

    // The host browses their own library; following a guest is opt-in.
    private bool followGuest, applyingGuestMirror;
    private string? followedPeer;
    private LibraryBrowseView? mirroredView;
    private readonly LibraryCardCollection mirroredCards = [];
    private bool FollowingGuest => room?.Identity?.Host == true && room.Snapshot?.LibraryActivity?.View is not null && followGuest;
    private void ResetGuestMirror() { followGuest = false; followedPeer = null; mirroredView = null; mirroredCards.Clear(); ClearHostArtworkRequests(); }
    private void RefreshGuestMirror(LibraryBrowseActivity? activity)
    {
        if (room?.Identity?.Host != true) return;
        if (activity?.Peer != followedPeer) { followedPeer = activity?.Peer; followGuest = false; mirroredView = null; }
        HostLibraryActivityText.Text = activity is null ? room.Snapshot?.LibraryBrowsing == true ? "Guest browsing available" : "Guest browsing is off"
            : activity.Name + " · " + activity.Location + (activity.Query.Length == 0 ? "" : " · " + activity.Query);
        BrowseWithGuestButton.Content = followGuest ? "Stop following" : "Follow " + activity?.Name;
        BrowseWithGuestButton.Visibility = activity?.View is not null ? Visibility.Visible : Visibility.Collapsed;
        if (FollowingGuest) ApplyGuestMirror();
        else if (PosterGrid.ItemsSource == mirroredCards && !applyingGuestMirror) { PosterGrid.ItemsSource = libraryCards; FilterLibrary(); }
    }
    // Stopping keeps the guest's location, and scrolling away keeps the host's place in it, in the host's own full library.
    private void DetachGuestMirror(bool keepPlace = false)
    {
        if (applyingGuestMirror || !FollowingGuest) return;
        followGuest = false;
        if (keepPlace && PosterGrid.ItemsSource == mirroredCards) libraryOffsetAfterFilter = LibraryScroll.VerticalOffset;
        RefreshGuestMirror(room?.Snapshot?.LibraryActivity);
    }
    private void HostLibraryScrollInput(object sender, MouseWheelEventArgs e) => DetachGuestMirror(keepPlace: true);
    private void HostLibraryPointerInput(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is System.Windows.Media.Visual; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton) { DetachGuestMirror(keepPlace: true); break; }
    }
    private void HostLibraryTouchInput(object sender, TouchEventArgs e) => DetachGuestMirror(keepPlace: true);
    private void BrowseWithGuest(object sender, RoutedEventArgs e)
    {
        if (FollowingGuest) { DetachGuestMirror(keepPlace: true); return; }
        followGuest = true; ShowPage("Library"); mirroredView = null; ApplyGuestMirror();
    }
    private void ApplyGuestMirror()
    {
        if (applyingGuestMirror || !FollowingGuest || currentPage is not ("Library" or "Details")) return;
        var view = room!.Snapshot!.LibraryActivity!.View!;
        if (view.Browse.Home) return;
        applyingGuestMirror = true;
        try
        {
            librarySearchCancellation?.Cancel();
            var source = items.Where(item => item.Available && !item.IsVirtual && !item.IsExtra);
            var parent = items.FirstOrDefault(item => item.Id == view.Browse.ParentId);
            var show = parent is null ? null : LibraryIdentity.ShowKey(parent);
            if (mirroredView?.Browse != view.Browse || mirroredView.LoadedCount != view.LoadedCount)
            {
                var cards = LibraryCatalog.Search(source.Where(item => view.Browse.Category switch { 1 => item.Kind == "Movie", 2 => item.Kind == "Show", 3 => item.Kind == "Anime",
                    LibraryCatalog.Music => item.IsMusic, _ => parent is not null || !item.IsMusic }),
                    view.Browse.Query, show, parent?.Kind, view.Browse.Season).Take(view.LoadedCount);
                mirroredCards.Clear(); mirroredCards.AddBatch(cards.Select(card => new LibraryCardView(card)));
                libraryCategory = view.Browse.Category; browseSeries = show; browseKind = parent?.Kind; browseSeason = view.Browse.Season;
                SearchBox.Text = view.Browse.Query; UpdateBreadcrumbs(); UpdateLibraryCategorySelection(true);
                ResultCount.Text = mirroredCards.Count + " items";
            }
            foreach (var card in mirroredCards) card.SetGuestHovered(card.Card.Media.Id == view.HoveredId);
            if (view.SelectedId is { } id && items.FirstOrDefault(item => item.Id == id) is { } selectedItem)
            {
                if (currentPage != "Details" || selected?.Id != id) Select(selectedItem);
            }
            else
            {
                if (currentPage == "Details") ShowPage("Library");
                libraryCategory = view.Browse.Category; browseSeries = show; browseKind = parent?.Kind; browseSeason = view.Browse.Season;
                UpdateBreadcrumbs();
                PosterGrid.ItemsSource = mirroredCards; EmptyLibrary.Visibility = NoResults.Visibility = Visibility.Collapsed;
                var fraction = view.ScrollFraction;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (FollowingGuest) LibraryScroll.ScrollToVerticalOffset(LibraryScroll.ScrollableHeight * fraction); }));
            }
            mirroredView = view;
        }
        finally { applyingGuestMirror = false; }
    }
}
