using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly DispatcherTimer guestBrowseTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool GuestLibrary => room?.Identity?.Host == false;
    private bool OwnsLibrary => room?.IsConnected == true && room.Snapshot?.LibraryActivity?.Peer == room.Identity?.Peer;
    private void InitializeSharedLibrary()
    {
        guestBrowseTimer.Tick += (_, _) =>
        {
            if (guestBrowsePending && DateTime.UtcNow - guestBrowseSent > TimeSpan.FromSeconds(15))
            { guestBrowsePending = false; guestLibraryNotice = "The host did not respond. Try browsing again; both devices need the updated app."; }
            if (currentPage == "Library" && OwnsLibrary) TryLibraryCommand(new("catalog-renew"));
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
        if (GuestLibraryPanel is null) return;
        var guest = GuestLibrary;
        GuestLibraryPanel.Visibility = guest ? Visibility.Visible : Visibility.Collapsed;
        LocalLibraryHeader.Visibility = LocalLibraryContent.Visibility = guest ? Visibility.Collapsed : Visibility.Visible;
        HostLibraryActivity.Visibility = room?.Identity?.Host == true ? Visibility.Visible : Visibility.Collapsed;
        var activity = room?.Snapshot?.LibraryActivity;
        HostLibraryActivityText.Text = activity is null ? room?.Snapshot?.LibraryBrowsing == true
            ? "No guest is browsing. Admitted guests can start from their Library tab."
            : "Guest browsing is off. Enable it in room settings."
            : $"{activity.Name} is browsing · {activity.Location}" + (activity.Query.Length == 0 ? "" : $" · Search: {activity.Query}");
        EndLibrarySessionButton.IsEnabled = activity is not null;
        var owns = guest && OwnsLibrary;
        var connected = room?.IsConnected == true && room.DirectControlsReady.IsCompletedSuccessfully;
        StartLibraryBrowseButton.IsEnabled = guest && connected && room?.Snapshot?.LibraryBrowsing == true && (activity is null || owns) && !guestBrowsePending;
        StartLibraryBrowseButton.Content = owns ? "Refresh titles" : "Browse host’s library";
        FinishLibraryBrowseButton.IsEnabled = owns || guestBrowsePending;
        GuestLibraryTools.Visibility = GuestLibraryPaging.Visibility = owns ? Visibility.Visible : Visibility.Collapsed;
        GuestLibraryTools.IsEnabled = GuestLibraryPaging.IsEnabled = !guestBrowsePending;
        if (!owns) GuestLibraryEntries.ItemsSource = null;
        GuestLibraryStatus.Text = room?.IsConnected != true ? "Disconnected from the host. Rejoin the room to browse their library."
            : !connected ? "Waiting for admission and a direct connection to the host…"
            : room?.Snapshot?.LibraryBrowsing != true ? "The host has disabled library browsing. They can enable it in room settings."
            : guestBrowsePending ? "Loading titles from the host…"
            : guestLibraryNotice is not null ? guestLibraryNotice
            : owns ? "You have the browsing slot. Finish browsing to let another guest explore."
            : activity is not null ? $"{activity.Name} is browsing now. The slot becomes available when they finish."
            : "Explore the host’s movies, shows and anime. One guest can browse at a time.";
    }
    private void RequestHostLibrary()
    {
        if (!GuestLibrary || room?.IsConnected != true) return;
        guestLibraryNotice = null; guestBrowsePending = true; guestBrowseSent = DateTime.UtcNow;
        TryLibraryCommand(new("catalog-browse", Number: ++guestBrowseRequest, Data: Wire.Serialize(guestBrowse)));
        RefreshSharedLibrary();
    }
    private void ReceiveHostLibrary(WireMessage message)
    {
        if (message.Number != guestBrowseRequest || currentPage != "Library" || !GuestLibrary) return;
        guestBrowsePending = false; RefreshSharedLibrary();
        if (message.Type == "catalog-error") { guestLibraryNotice = message.Text; RefreshSharedLibrary(); return; }
        if (!OwnsLibrary) return;
        var page = Wire.Read<LibraryBrowsePage>(message.Data!);
        guestBrowse = guestBrowse with { Page = page.Page };
        GuestLibraryEntries.ItemsSource = page.Entries;
        GuestLibraryLocation.Text = page.Location;
        GuestLibraryBack.IsEnabled = guestBrowse.ParentId is not null;
        GuestLibraryPrevious.IsEnabled = page.Page > 0;
        GuestLibraryNext.IsEnabled = (page.Page + 1) * SharedLibrary.PageSize < page.Total;
        GuestLibraryCount.Text = page.Total == 0 ? "No matching titles" : $"{page.Total} titles · Page {page.Page + 1} of {Math.Max(1, (page.Total + SharedLibrary.PageSize - 1) / SharedLibrary.PageSize)}";
        guestLibraryNotice = page.Total == 0 ? "No titles here. Try another search or category; the host may need to scan their folders." : null;
        RefreshSharedLibrary();
    }
    private void ReleaseHostLibrary()
    {
        if (GuestLibrary && (OwnsLibrary || guestBrowsePending)) TryLibraryCommand(new("catalog-release"));
        guestLibraryNotice = null; guestBrowsePending = false; ++guestBrowseRequest; GuestLibraryEntries.ItemsSource = null;
    }
    private void StartLibraryBrowse(object sender, RoutedEventArgs e) => RequestHostLibrary();
    private void FinishLibraryBrowse(object sender, RoutedEventArgs e) { ReleaseHostLibrary(); RefreshSharedLibrary(); }
    private void SearchHostLibrary(object sender, RoutedEventArgs e)
    {
        var category = GuestLibraryCategory.SelectedIndex;
        guestBrowse = guestBrowse with { Query = GuestLibrarySearch.Text.Trim(), Category = category, Page = 0,
            ParentId = category != guestBrowse.Category ? null : guestBrowse.ParentId, Season = category != guestBrowse.Category ? null : guestBrowse.Season };
        RequestHostLibrary();
    }
    private void GuestLibrarySearchKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SearchHostLibrary(sender, e); e.Handled = true; } }
    private void HostLibraryRoot(object sender, RoutedEventArgs e)
    {
        guestBrowse = new(); GuestLibrarySearch.Clear(); GuestLibraryCategory.SelectedIndex = 0; RequestHostLibrary();
    }
    private void HostLibraryBack(object sender, RoutedEventArgs e)
    {
        guestBrowse = guestBrowse with { ParentId = guestBrowse.Season is null ? null : guestBrowse.ParentId, Season = null, Query = "", Page = 0 };
        GuestLibrarySearch.Clear(); RequestHostLibrary();
    }
    private void HostLibraryPage(object sender, RoutedEventArgs e)
    { guestBrowse = guestBrowse with { Page = Math.Max(0, guestBrowse.Page + int.Parse((string)((Button)sender).Tag)) }; RequestHostLibrary(); }
    private void OpenHostLibraryEntry(object sender, RoutedEventArgs e)
    {
        if (!OwnsLibrary || guestBrowsePending || ((Button)sender).Tag is not SharedLibraryEntry entry) return;
        if (entry.Level is "series" or "season")
        {
            guestBrowse = guestBrowse with { ParentId = entry.Id, Season = entry.Level == "season" ? entry.Season : null, Query = "", Page = 0 };
            GuestLibrarySearch.Clear(); RequestHostLibrary();
        }
        else { TryLibraryCommand(new("catalog-view", Target: entry.Id)); GuestLibraryLocation.Text = entry.Title; }
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
