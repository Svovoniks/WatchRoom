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
    private async void OpenGuestVideo(LibraryCard card)
    {
        selected = card.Media; ShowPage("Details"); UpdateHostBreadcrumbs();
        DetailTitle.Text = card.DisplayTitle; DetailMeta.Text = card.Caption; DetailContext.Text = HostLibraryName;
        DetailSummary.Text = card.Media.Overview ?? ""; DetailFile.Text = "";
        DetailPlay.Content = "Play in room"; DetailPlay.IsEnabled = OwnsLibrary;
        DetailHost.Visibility = DetailQueue.Visibility = RestartButton.Visibility = Visibility.Collapsed;
        Episodes.Visibility = Visibility.Collapsed; DetailPoster.Source = null;
        ScheduleGuestView();
        var view = guestLibraryCards.FirstOrDefault(view => view.Card == card);
        if (view is not null) { await view.LoadPosterAsync(); if (selected?.Id == card.Media.Id && GuestLibrary) DetailPoster.Source = view.Poster; }
    }
    private void WatchGuestVideo()
    {
        if (!OwnsLibrary || selected is null) return;
        TryLibraryCommand(new("catalog-watch", Target: selected.Id, Number: ++guestBrowseRequest));
        SetStatus("Starting " + selected.Title + " in the room…");
    }
    private long hostArtworkSequence;
    private readonly SemaphoreSlim hostArtworkWorkers = new(2);
    private readonly Dictionary<string, Task<string?>> hostArtworkCache = [];
    private readonly Dictionary<long, (TaskCompletionSource<string?> Completion, string Key, SortedDictionary<int, string> Chunks)> hostArtworkPending = [];
    private Task<string?> LoadHostArtwork(LibraryCard card)
    {
        var key = card.Media.Id + ":" + card.Level + ":" + card.Season;
        if (hostArtworkCache.TryGetValue(key, out var cached)) return cached;
        return hostArtworkCache[key] = FetchHostArtwork(card, key);
    }
    private async Task<string?> FetchHostArtwork(LibraryCard card, string key)
    {
        await hostArtworkWorkers.WaitAsync();
        var sequence = -++hostArtworkSequence;
        try
        {
            if (!OwnsLibrary) return null;
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostArtworkPending[sequence] = (completion, key, []);
            TryLibraryCommand(new("catalog-artwork", Number: sequence, Data: Wire.Serialize(new LibraryArtworkRequest(card.Media.Id, card.Level, card.Season))));
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { hostArtworkCache.Remove(key); return null; }
        }
        finally { hostArtworkPending.Remove(sequence); hostArtworkWorkers.Release(); }
    }
    private void ReceiveHostArtwork(WireMessage message)
    {
        if (!OwnsLibrary || !hostArtworkPending.TryGetValue(message.Number, out var pending)) return;
        var chunk = Wire.Read<LibraryArtworkChunk>(message.Data!);
        if (chunk.Key != pending.Key || chunk.Count is < 1 or > 25 || chunk.Index < 0 || chunk.Index >= chunk.Count || chunk.Data.Length > 8000) return;
        pending.Chunks[chunk.Index] = chunk.Data;
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
        hostArtworkPending.Clear(); hostArtworkCache.Clear();
    }

    private bool followGuest = true, applyingGuestMirror;
    private string? followedPeer;
    private LibraryBrowseView? mirroredView;
    private readonly LibraryCardCollection mirroredCards = [];
    private bool FollowingGuest => room?.Identity?.Host == true && room.Snapshot?.LibraryActivity?.View is not null && followGuest;
    private void ResetGuestMirror() { followGuest = true; followedPeer = null; mirroredView = null; mirroredCards.Clear(); ClearHostArtworkRequests(); }
    private void RefreshGuestMirror(LibraryBrowseActivity? activity)
    {
        if (room?.Identity?.Host != true) return;
        if (activity?.Peer != followedPeer) { followedPeer = activity?.Peer; followGuest = true; mirroredView = null; }
        HostLibraryActivityText.Text = activity is null ? room.Snapshot?.LibraryBrowsing == true ? "Guest browsing available" : "Guest browsing is off"
            : activity.Name + " · " + activity.Location + (activity.Query.Length == 0 ? "" : " · " + activity.Query);
        BrowseWithGuestButton.Content = "Browse with " + activity?.Name;
        BrowseWithGuestButton.Visibility = activity is not null && !followGuest ? Visibility.Visible : Visibility.Collapsed;
        if (FollowingGuest) ApplyGuestMirror();
        else if (activity is null && PosterGrid.ItemsSource == mirroredCards) { PosterGrid.ItemsSource = libraryCards; FilterLibrary(); }
    }
    private void DetachGuestMirror()
    {
        if (applyingGuestMirror || !FollowingGuest) return;
        followGuest = false; RefreshGuestMirror(room?.Snapshot?.LibraryActivity);
    }
    private void HostLibraryScrollInput(object sender, MouseWheelEventArgs e) => DetachGuestMirror();
    private void HostLibraryPointerInput(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is System.Windows.Media.Visual; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RepeatButton) { DetachGuestMirror(); break; }
    }
    private void HostLibraryTouchInput(object sender, TouchEventArgs e) => DetachGuestMirror();
    private void BrowseWithGuest(object sender, RoutedEventArgs e) { followGuest = true; ShowPage("Library"); mirroredView = null; ApplyGuestMirror(); }
    private void ApplyGuestMirror()
    {
        if (applyingGuestMirror || !FollowingGuest || currentPage is not ("Library" or "Details")) return;
        var view = room!.Snapshot!.LibraryActivity!.View!;
        applyingGuestMirror = true;
        try
        {
            librarySearchCancellation?.Cancel();
            var source = items.Where(item => item.Available && !item.IsVirtual && !item.IsExtra);
            var parent = items.FirstOrDefault(item => item.Id == view.Browse.ParentId);
            var show = parent is null ? null : LibraryIdentity.ShowKey(parent);
            if (mirroredView?.Browse != view.Browse || mirroredView.LoadedCount != view.LoadedCount)
            {
                var cards = LibraryCatalog.Search(source.Where(item => view.Browse.Category switch { 1 => item.Kind == "Movie", 2 => item.Kind == "Show", 3 => item.Kind == "Anime", _ => true }),
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
