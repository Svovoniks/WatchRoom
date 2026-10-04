using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private LibraryBrowseRequest guestBrowse = new();
    private long guestBrowseRequest;
    private bool guestBrowsePending, updatingLibraryPermission;
    private DateTime guestBrowseSent;
    private string? guestLibraryNotice;
    private CancellationTokenSource? guestSearchCancellation;
    private readonly LibraryCardCollection guestLibraryCards = [];
    private readonly Dictionary<LibraryBrowseRequest, CachedHostBrowse> guestLibraryCache = [];
    private CachedHostBrowse? guestCurrentBrowse;
    private bool guestHadLibraryLease;
    private string? guestParentTitle;
    private sealed class CachedHostBrowse
    {
        public DateTime Created { get; } = DateTime.UtcNow;
        public List<LibraryCardView> Cards { get; } = [];
        public int Total, NextPage;
        public string Location = "All titles";
    }
    private readonly DispatcherTimer guestBrowseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool GuestLibrary => room?.Identity?.Host == false;
    private bool OwnsLibrary => room?.IsConnected == true && room.Snapshot?.LibraryActivity?.Peer == room.Identity?.Peer;
    private void InitializeSharedLibrary()
    {
        guestBrowseTimer.Tick += (_, _) =>
        {
            if (guestBrowsePending && DateTime.UtcNow - guestBrowseSent > TimeSpan.FromSeconds(15))
            { guestBrowsePending = false; guestLibraryNotice = "The host did not respond. Select a category or search to try again."; }
            if (currentPage is "Library" or "Details" && OwnsLibrary) TryLibraryCommand(new("catalog-renew"));
            RefreshSharedLibrary();
        };
        guestBrowseTimer.Start();
    }
    private void TryLibraryCommand(WireMessage message)
    {
        try { room?.Send(message); }
        catch (IOException ex) { guestBrowsePending = false; guestLibraryNotice = ex.Message; SetStatus(ex.Message); }
    }
    private void RefreshSharedLibrary()
    {
        if (GuestLibraryStatus is null) return;
        var guest = GuestLibrary;
        if (guest && !OwnsLibrary && !guestBrowsePending) ClearHostArtworkRequests();
        LibraryStatusFilter.Visibility = RescanButton.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
        GuestLibraryStatus.Visibility = guest ? Visibility.Visible : Visibility.Collapsed;
        HostLibraryActivity.Visibility = room?.Identity?.Host == true ? Visibility.Visible : Visibility.Collapsed;
        var activity = room?.Snapshot?.LibraryActivity;
        HostLibraryActivityText.Text = activity is null ? room?.Snapshot?.LibraryBrowsing == true
            ? "No guest is browsing. Admitted guests can start from their Library tab."
            : "Guest browsing is off. Enable it in room settings."
            : $"{activity.Name} is browsing · {activity.Location}" + (activity.Query.Length == 0 ? "" : $" · Search: {activity.Query}");
        RefreshGuestMirror(activity);
        if (!guest)
        {
            if (PosterGrid.ItemsSource == guestLibraryCards)
            {
                PosterGrid.ItemsSource = libraryCards;
                guestLibraryCache.Clear(); guestLibraryCards.Clear(); guestCurrentBrowse = null;
                if (currentPage == "Library") FilterLibrary();
            }
            return;
        }
        librarySearchCancellation?.Cancel();
        PosterGrid.ItemsSource = guestLibraryCards;
        EmptyLibrary.Visibility = NoResults.Visibility = SeasonInformation.Visibility = Visibility.Collapsed;
        var connected = room?.IsConnected == true && room.DirectControlsReady.IsCompletedSuccessfully;
        var allowed = connected && room?.Snapshot?.LibraryBrowsing == true;
        if (guestHadLibraryLease && !OwnsLibrary)
        {
            guestLibraryCards.Clear(); guestLibraryCache.Clear(); guestCurrentBrowse = null;
            guestBrowsePending = false; ++guestBrowseRequest;
            guestLibraryNotice = "Browsing session ended. Search or select a category to browse again.";
        }
        guestHadLibraryLease = OwnsLibrary;
        if (!allowed || activity is not null && !OwnsLibrary)
        {
            guestLibraryCards.Clear(); guestLibraryCache.Clear(); guestCurrentBrowse = null;
            guestBrowsePending = false; ++guestBrowseRequest;
        }
        GuestLibraryStatus.Text = room?.IsConnected != true ? "Disconnected from the host. Rejoin the room to browse their library."
            : !connected ? "Waiting for admission and a direct connection to the host…"
            : room?.Snapshot?.LibraryBrowsing != true ? "The host has disabled library browsing. They can enable it in room settings."
            : guestLibraryNotice is not null ? guestLibraryNotice
            : activity is not null && !OwnsLibrary ? $"{activity.Name} is browsing now. Their library will load when the slot is available."
            : guestBrowsePending && guestLibraryCards.Count == 0 ? "Loading the host’s library…"
            : HostLibraryName;
        if (allowed && (activity is null || OwnsLibrary) && currentPage == "Library" &&
            !guestBrowsePending && guestCurrentBrowse is null && guestLibraryNotice is null)
            RequestHostLibrary();
    }
    private async Task FilterHostLibraryAsync(bool debounce)
    {
        guestSearchCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        guestSearchCancellation = request;
        try
        {
            if (debounce) await Task.Delay(200, request.Token);
            if (!request.IsCancellationRequested && GuestLibrary && currentPage == "Library") FilterHostLibrary();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (guestSearchCancellation == request) guestSearchCancellation = null; }
    }
    private void FilterHostLibrary()
    {
        if (!GuestLibrary || currentPage != "Library") return;
        guestLibraryNotice = null;
        var query = SearchBox.Text.Trim();
        guestBrowse = guestBrowse with { Query = query[..Math.Min(query.Length, 100)], Page = 0 };
        SearchHint.Text = guestBrowse.ParentId is null ? "Search host’s library" : guestBrowse.Season is null ? "Search seasons" : "Search episodes";
        SearchBox.ToolTip = SearchHint.Text + " (Ctrl+F)";
        ClearSearchButton.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateHostBreadcrumbs(); UpdateLibraryCategorySelection(true);
        var key = guestBrowse with { Page = 0 };
        LibraryScroll.ScrollToTop();
        if (guestLibraryCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Created < TimeSpan.FromMinutes(2) && OwnsLibrary)
        {
            ++guestBrowseRequest; guestBrowsePending = false; guestCurrentBrowse = cached;
            ShowCachedHostBrowse(); return;
        }
        guestLibraryCards.Clear(); guestCurrentBrowse = null;
        RequestHostLibrary();
    }
    private void RequestHostLibrary(bool more = false)
    {
        if (!GuestLibrary || currentPage != "Library" || room?.IsConnected != true ||
            !room.DirectControlsReady.IsCompletedSuccessfully || room.Snapshot?.LibraryBrowsing != true ||
            room.Snapshot.LibraryActivity is not null && !OwnsLibrary || more && guestBrowsePending) return;
        guestLibraryNotice = null; guestBrowsePending = true; guestBrowseSent = DateTime.UtcNow;
        guestBrowse = guestBrowse with { Page = more ? guestCurrentBrowse?.NextPage ?? 0 : 0 };
        TryLibraryCommand(new("catalog-browse", Number: ++guestBrowseRequest, Data: Wire.Serialize(guestBrowse)));
        RefreshSharedLibrary();
    }
    private void ReceiveHostLibrary(WireMessage message)
    {
        if (message.Number != guestBrowseRequest || currentPage is not ("Library" or "Details") || !GuestLibrary) return;
        guestBrowsePending = false;
        if (message.Type == "catalog-error") { guestLibraryNotice = message.Text; SetStatus(message.Text ?? "Library request failed."); RefreshSharedLibrary(); return; }
        if (!OwnsLibrary) { RefreshSharedLibrary(); return; }
        var page = Wire.Read<LibraryBrowsePage>(message.Data!);
        if (page.Page != guestBrowse.Page && page.Total > 0)
        {
            // The host collection shrank while scrolling. Restart this location.
            guestLibraryCache.Remove(guestBrowse with { Page = 0 }); guestCurrentBrowse = null; guestLibraryCards.Clear();
            RequestHostLibrary(); return;
        }
        var key = guestBrowse with { Page = 0 };
        if (page.Page == 0 || guestCurrentBrowse is null)
        {
            if (guestLibraryCache.Count >= 32) guestLibraryCache.Remove(guestLibraryCache.MinBy(pair => pair.Value.Created).Key);
            guestLibraryCache[key] = guestCurrentBrowse = new();
            guestLibraryCards.Clear();
        }
        var cache = guestCurrentBrowse;
        cache.Total = page.Total; cache.NextPage = page.Page + 1; cache.Location = page.Location;
        var cards = page.Entries.Select(LibraryCardView.FromSharedEntry)
            .Where(card => !cache.Cards.Any(existing => existing.Card.Media.Id == card.Card.Media.Id && existing.Card.Level == card.Card.Level)).ToArray();
        foreach (var card in cards) card.RemoteArtworkLoader = () => LoadHostArtwork(card.Card);
        cache.Cards.AddRange(cards); guestLibraryCards.AddBatch(cards);
        ScheduleGuestView();
        guestLibraryNotice = page.Total == 0 ? "No matching titles. Try another search or category." : null;
        UpdateHostResultCount(); UpdateHostBreadcrumbs(); RefreshSharedLibrary();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(LoadMoreHostTitles));
    }
    private void ShowCachedHostBrowse()
    {
        guestLibraryCards.Clear(); guestLibraryCards.AddBatch(guestCurrentBrowse!.Cards);
        PosterGrid.ItemsSource = guestLibraryCards;
        UpdateHostResultCount(); UpdateHostBreadcrumbs();
        ScheduleGuestView();
        // Renew host activity even when the data is already cached.
        TryLibraryCommand(new("catalog-view", Target: guestBrowse.ParentId, Data: Wire.Serialize(guestBrowse with { Page = 0 })));
        RefreshSharedLibrary();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(LoadMoreHostTitles));
    }
    private void UpdateHostResultCount() => ResultCount.Text = $"{guestCurrentBrowse?.Total ?? 0} items";
    private void LibraryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (GuestLibrary) { LoadMoreHostTitles(); ScheduleGuestView(); }
    }
    private void LoadMoreHostTitles()
    {
        if (!GuestLibrary || currentPage != "Library" || !OwnsLibrary || guestBrowsePending ||
            guestLibraryNotice is not null || guestCurrentBrowse is not { } cache ||
            cache.NextPage * SharedLibrary.PageSize >= cache.Total) return;
        if (PosterGrid.Template.FindName("LibraryScroll", PosterGrid) is ScrollViewer scroll &&
            scroll.VerticalOffset + scroll.ViewportHeight + 660 >= scroll.ExtentHeight)
            RequestHostLibrary(more: true);
    }
    private void ReleaseHostLibrary()
    {
        guestSearchCancellation?.Cancel();
        ClearHostArtworkRequests(); guestHoveredId = null;
        guestHadLibraryLease = false;
        if (GuestLibrary && (OwnsLibrary || guestBrowsePending)) TryLibraryCommand(new("catalog-release"));
        guestLibraryNotice = null; guestBrowsePending = false; ++guestBrowseRequest;
        guestCurrentBrowse = null; guestLibraryCards.Clear();
    }
    private void UpdateHostBreadcrumbs()
    {
        var crumbs = new List<LibraryBreadcrumb> { new("All", "category", Separator: "") };
        if (guestBrowse.Category != 0) crumbs.Add(new(CategoryName(guestBrowse.Category), "category", guestBrowse.Category));
        if (guestBrowse.ParentId is not null)
        {
            crumbs.Add(new(guestParentTitle ?? "Show", "show", guestBrowse.Category, guestBrowse.ParentId));
            if (guestBrowse.Season is { } season) crumbs.Add(new(SeasonName(season), "season", guestBrowse.Category, guestBrowse.ParentId, Season: season));
        }
        crumbs[^1] = crumbs[^1] with { IsCurrent = true };
        LibraryBreadcrumbs.ItemsSource = crumbs;
        DetailBreadcrumbs.ItemsSource = currentPage == "Details" && selected is not null ? crumbs.Concat(new[] { new LibraryBreadcrumb(selected.Title, "media", IsCurrent: true) }).ToArray() : crumbs;
    }
    private void NavigateHostBreadcrumb(LibraryBreadcrumb crumb)
    {
        libraryCategory = crumb.Category;
        guestBrowse = new(Category: crumb.Category, ParentId: crumb.Series, Season: crumb.Season);
        SearchBox.Clear(); ShowPage("Library"); FilterHostLibrary();
    }
    private void OpenHostCard(LibraryCard card)
    {
        if (!OwnsLibrary) return;
        if (card.Level is "series" or "season")
        {
            if (card.Level == "series") guestParentTitle = card.DisplayTitle;
            guestBrowse = guestBrowse with { ParentId = card.Level == "season" ? guestBrowse.ParentId : card.Media.Id,
                Season = card.Level == "season" ? card.Season : null, Query = "", Page = 0 };
            SearchBox.Clear(); FilterHostLibrary();
        }
        else OpenGuestVideo(card);
    }
    private void OpenLibraryPermissions(object sender, RoutedEventArgs e) { RememberCurrentRoom(); ShowPage("Rooms"); }
    private void EndLibrarySession(object sender, RoutedEventArgs e) => TryLibraryCommand(new("catalog-release"));
    private void SavedRoomLibraryChanged(object sender, RoutedEventArgs e)
    {
        if (updatingLibraryPermission || SavedRoomsList.SelectedItem is not SavedRoom saved || !SavedRoomLibraryBrowsing.IsEnabled) return;
        var enabled = SavedRoomLibraryBrowsing.IsChecked == true;
        library.Setting(SavedRoomSetting(saved, "libraryBrowsing"), enabled ? "true" : "false");
        if (room?.IsConnected == true && room.Identity?.Host == true && room.ServerAddress == saved.Server && room.Identity.Room == saved.Code)
            TryLibraryCommand(new("catalog-permission", Number: enabled ? 1 : 0));
    }
}
