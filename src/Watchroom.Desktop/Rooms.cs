using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<SavedRoom> savedRooms = [];
    private string? pendingRoomName;
    private void LoadSavedRooms()
    {
        try { foreach (var saved in Wire.Read<SavedRoom[]>(library.Setting("rooms") ?? "[]")) savedRooms.Add(saved); }
        catch (System.Text.Json.JsonException) { }
        SavedRoomsList.ItemsSource = savedRooms;
        SavedRoomsList.ItemContainerGenerator.StatusChanged += (_, _) => MarkLiveRoomPill();
        if (savedRooms.Count > 0) SavedRoomsList.SelectedIndex = 0;
        RefreshSavedRooms();
    }
    private void RefreshSavedRooms()
    {
        if (SavedRoomsList is null) return;
        CreateSavedRoomButton.IsEnabled = AddSavedInvitationButton.IsEnabled = !roomBusy;
        RoomsEmptyState.Visibility = savedRooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateSavedRoomButton.Visibility = AddSavedInvitationButton.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomsContent.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomDetails.Visibility = SavedRoomsList.SelectedItem is SavedRoom ? Visibility.Visible : Visibility.Collapsed;
        ConnectSavedRoomButton.IsEnabled = !roomBusy && SavedRoomsList.SelectedItem is SavedRoom;
        ForgetSavedRoomButton.IsEnabled = CopySavedInviteButton.IsEnabled = CopySavedCodeButton.IsEnabled = SavedRoomsList.SelectedItem is SavedRoom;
        RefreshRoomSettings(); RefreshRoomHero();
    }
    private readonly Dictionary<string, string> savedGuestLists = [];
    private string SavedRoomSetting(SavedRoom saved, string field) => "room:" + saved.Server + ":" + saved.Code.ToUpperInvariant() + ":" + field;
    private void RefreshRoomSettings()
    {
        if (SavedRoomSharedControls is null) return;
        var saved = SavedRoomsList.SelectedItem as SavedRoom;
        var connected = saved is not null && room?.IsConnected == true && room.ServerAddress == saved.Server && room.Identity?.Room == saved.Code;
        var host = saved is not null && (connected ? room!.Identity!.Host : saved.HostKey is not null);
        SavedRoomHostControls.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        // Guests have no permissions tile, so the room actions move to the top of the column.
        Grid.SetRow(SavedRoomActions, host ? 2 : 0);
        MarkLiveRoomPill();
        RenameSavedRoomButton.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        RenameSavedRoomButton.IsEnabled = host;
        RenameSavedRoomButton.ToolTip = host ? "Change the room name for everyone" : "Only the host can change the room name";
        SavedRoomHeading.Text = saved?.Name ?? "Choose a room";
        SavedRoomStatus.Text = saved is null ? "Create a room or save an invitation to get started." : connected ? (host ? "Open · You are the host" : "Connected · Guest") : (host ? "Your room · Closed" : "Saved invitation · Offline");
        SavedRoomStatusDot.Fill = (System.Windows.Media.Brush)FindResource(connected ? "Success" : "Muted");
        SavedRoomCode.Text = saved is null ? "" : string.Join(" ", saved.Code.Chunk(6).Select(part => new string(part)));
        SavedRoomInviteHint.Text = saved is null ? "" : (Uri.TryCreate(saved.Server, UriKind.Absolute, out var server) ? server.Host : saved.Server)
            + (host ? " · People ask to join and you admit them" : " · The host admits new people");
        ShowSavedRoomSession(saved, host);
        ConnectSavedRoomButton.Content = connected ? "Open player" : "Connect";
        DisconnectSavedRoomButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectSavedRoomButton.IsEnabled = connected && !roomBusy;
        SavedRoomSharedControls.IsEnabled = host;
        updatingLibraryPermission = true;
        SavedRoomLibraryBrowsing.IsEnabled = host;
        SavedRoomLibraryBrowsing.IsChecked = connected ? room!.Snapshot?.LibraryBrowsing == true : saved is not null && library.Setting(SavedRoomSetting(saved, "libraryBrowsing")) == "true";
        updatingLibraryPermission = false;
        SavedRoomSharedControls.IsChecked = connected ? room!.Snapshot?.SharedControls == true : saved is not null && library.Setting(SavedRoomSetting(saved, "sharedControls")) == "true";
        AdmittedGuest[] remembered = [];
        if (saved is not null && host)
        {
            if (connected)
            {
                remembered = room!.Snapshot?.AdmittedGuests ?? [];
                // Snapshots arrive for every guest command; write the guest list only when it changes.
                var key = SavedRoomSetting(saved, "guests"); var json = Wire.Serialize(remembered);
                if (savedGuestLists.GetValueOrDefault(key) != json) { library.Setting(key, json); savedGuestLists[key] = json; }
            }
            else try { remembered = Wire.Read<AdmittedGuest[]>(library.Setting(SavedRoomSetting(saved, "guests")) ?? "[]"); } catch (System.Text.Json.JsonException) { }
        }
        List<RoomGuestRow> rows;
        if (host)
        {
            var active = connected ? room!.Snapshot?.People.Where(p => !p.IsHost).ToArray() ?? [] : [];
            rows = active.Select(p => new RoomGuestRow(p.Id, p.Name, p.Approved ? (room!.VoiceState(p.Id)?.Joined == true ? "In the room · In voice" : "In the room") : "Asking to join", !p.Approved, connected, pendingAdmissions.ContainsKey(p.Id), true, true)).ToList();
            rows.AddRange(remembered.Where(p => !active.Any(a => a.GuestId == p.Id)).Select(p => new RoomGuestRow(p.Id, p.Name, "Admitted · Not here", false, connected, false, true, false)));
        }
        // Guests see who is in the room while connected.
        else rows = connected ? (room!.Snapshot?.People ?? []).Where(p => p.IsHost || p.Approved)
            .Select(p => new RoomGuestRow(p.Id, p.Id == room.Identity!.Peer ? p.Name + " (you)" : p.Name, (p.IsHost ? "Host" : "In the room") + (room.VoiceState(p.Id)?.Joined == true ? " · In voice" : ""), false, false, false, false, true)).ToList() : [];
        if (SavedRoomGuests.ItemsSource is not IEnumerable<RoomGuestRow> shown || !shown.SequenceEqual(rows)) SavedRoomGuests.ItemsSource = rows;
        SavedRoomGuestHeading.Text = host ? "GUESTS" : "PEOPLE";
        SavedRoomGuestCount.Text = host ? remembered.Length == 0 ? "" : remembered.Length + " admitted" : rows.Count == 0 ? "" : rows.Count + " here";
        SavedRoomGuestHint.Text = !host ? connected ? "" : "Connect to see who's in the room."
            : connected ? rows.Count == 0 ? "No guests yet. Share the invitation; new people ask to join here." : "Admitted guests can reconnect. Remove revokes access."
            : rows.Count == 0 ? "No guests yet. Open the room and share the invitation." : "Open the room to admit or remove guests.";
        SavedRoomGuestHintPanel.Visibility = SavedRoomGuestHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var empty = rows.Count == 0;
        SavedRoomGuestHintPanel.VerticalAlignment = empty ? VerticalAlignment.Center : VerticalAlignment.Top;
        SavedRoomGuestHintPanel.Margin = new(empty ? 16 : 0, 0, empty ? 16 : 0, empty ? 24 : 12);
        Grid.SetRowSpan(SavedRoomGuestHintPanel, empty ? 2 : 1);
        SavedRoomGuestEmptyIcon.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomGuestHint.TextAlignment = empty ? TextAlignment.Center : TextAlignment.Left;
    }
    private sealed record RoomGuestRow(string Id, string Name, string Status, bool Waiting, bool CanRemove, bool Pending, bool Manage, bool Here)
    {
        public Visibility AdmitVisibility => Waiting ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ManageVisibility => Manage ? Visibility.Visible : Visibility.Collapsed;
        public string Initial => Initials(Name, 1);
        public double Presence => Here ? 1 : 0.45;
        // Each person keeps a colour of their own, derived from the name.
        public System.Windows.Media.Brush Tint { get { var brush = new System.Windows.Media.SolidColorBrush(Hsl(TitleHue(Name), .65, .74)); brush.Freeze(); return brush; } }
        public bool CanAdmit => CanRemove && !Pending;
        public string AdmitLabel => Pending ? "Admitting…" : "Admit";
    }
    private void SavedRoomControlsChanged(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved || !SavedRoomSharedControls.IsEnabled) return;
        var enabled = SavedRoomSharedControls.IsChecked == true;
        library.Setting(SavedRoomSetting(saved, "sharedControls"), enabled ? "true" : "false");
        if (room?.IsConnected == true && room.Identity?.Host == true && room.ServerAddress == saved.Server && room.Identity.Room == saved.Code)
            room.Send(new("controls", Number: enabled ? 1 : 0));
        SetStatus("Room playback controls saved.");
    }
    private void SaveRooms() => library.Setting("rooms", Wire.Serialize(savedRooms));
    private void SavedRoomSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSavedRooms();
    private void RememberCurrentRoom()
    {
        if (room?.Identity is not { } identity || room.ServerAddress is null) return;
        var existing = savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room);
        if (existing is null)
        {
            existing = new(Guid.NewGuid().ToString("N"), room.Snapshot?.Name ?? pendingRoomName ?? "Room " + identity.Room[..6], room.ServerAddress, identity.Room, identity.HostKey);
            savedRooms.Add(existing);
        }
        else savedRooms[savedRooms.IndexOf(existing)] = existing with { HostKey = identity.HostKey ?? existing.HostKey, Name = room.Snapshot?.Name ?? existing.Name };
        SaveRooms(); SavedRoomsList.SelectedItem = savedRooms.First(x => x.Id == existing.Id);
    }
    private void SyncCurrentRoomName(RoomSnapshot snapshot)
    {
        if (room?.Identity is not { } identity || string.IsNullOrWhiteSpace(snapshot.Name)) return;
        RoomHeading.Text = snapshot.Name;
        var saved = savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room);
        if (saved is not null && saved.Name != snapshot.Name)
        {
            var selected = (SavedRoomsList.SelectedItem as SavedRoom)?.Id == saved.Id;
            var updated = saved with { Name = snapshot.Name };
            savedRooms[savedRooms.IndexOf(saved)] = updated; SaveRooms();
            if (selected) SavedRoomsList.SelectedItem = updated;
        }
    }
    private static string? ValidateRoomName(string name) => name.Length > 100 || name.Any(char.IsControl)
        ? "Use at most 100 characters without control characters." : null;
    private async void CreatePersistentRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy) return;
        var name = Dialogs.Prompt(this, "Create room", "Room name", validate: ValidateRoomName, action: "Create room",
            help: "Invite friends after creating the room. Choose a video from your library when you're ready."); if (name is null) return;
        pendingRoomName = name; roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(SavedServerAddress, SavedDisplayName, persistent: true, roomName: name); }
            catch { await Disconnect(); throw; }
            RememberCurrentRoom(); RoomHeading.Text = name; RoomSubtitle.Text = "Share an invitation, then choose a video from the library.";
            ShowPage("Rooms");
        }
        finally { pendingRoomName = null; roomBusy = false; RefreshSavedRooms(); }
    });
    private void SaveRoomInvitation(object sender, RoutedEventArgs e)
    {
        var invitation = Dialogs.Prompt(this, "Add room by invitation", "Invitation link or room code", validate: value =>
        { try { RoomAddress.Parse(value, SavedServerAddress); return null; } catch (ArgumentException ex) { return ex.Message; } });
        if (invitation is null) return;
        var address = RoomAddress.Parse(invitation, SavedServerAddress);
        var existing = savedRooms.FirstOrDefault(x => x.Server == address.Server && x.Code == address.Code);
        if (existing is not null) { SavedRoomsList.SelectedItem = existing; SetStatus("This room is already saved."); return; }
        var saved = new SavedRoom(Guid.NewGuid().ToString("N"), "Room " + address.Code[..6], address.Server, address.Code);
        savedRooms.Add(saved); SaveRooms(); SavedRoomsList.SelectedItem = saved;
    }
    private async void ConnectSavedRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy || SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        if (room?.IsConnected == true && room.ServerAddress == saved.Server && room.Identity?.Room == saved.Code) { ShowPage("Room"); return; }
        if (await ConnectToSavedRoom(saved)) ShowPage("Rooms");
    });
    private async Task<bool> ConnectToSavedRoom(SavedRoom saved)
    {
        if (roomBusy) return false;
        roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(saved.Server, SavedDisplayName, saved.Code, saved.HostKey, roomName: saved.HostKey is null ? null : saved.Name); }
            catch { await Disconnect(); throw; }
            RoomHeading.Text = client.Snapshot?.Name ?? saved.Name;
            RoomSubtitle.Text = saved.HostKey is null ? "Waiting for host approval" : "Choose a video from the library.";
            RememberCurrentRoom();
            PublishRoomSession(client, saved); RestoreRoomSession(client, saved);
            return true;
        }
        finally { roomBusy = false; RefreshSavedRooms(); }
    }
    private async void RenameSavedRoom(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved || !RenameSavedRoomButton.IsEnabled) return Task.CompletedTask;
        var name = Dialogs.Prompt(this, "Rename room", "Room name", saved.Name, ValidateRoomName, action: "Save name"); if (name is null) return Task.CompletedTask;
        RenameHostRoom(saved, name);
        return Task.CompletedTask;
    });
    private void RenameHostRoom(SavedRoom saved, string name)
    {
        var connected = room?.IsConnected == true && room.ServerAddress == saved.Server && room.Identity?.Room == saved.Code;
        if (!(connected ? room!.Identity!.Host : saved.HostKey is not null)) return;
        if (string.IsNullOrWhiteSpace(name) || ValidateRoomName(name.Trim()) is not null) return;
        name = name.Trim();
        if (connected) room!.Send(new("room-name", Text: name));
        library.Setting(SavedRoomSetting(saved, "name"), name);
        var updated = saved with { Name = name }; savedRooms[savedRooms.IndexOf(saved)] = updated;
        SaveRooms(); SavedRoomsList.SelectedItem = updated;
    }
    private void ForgetSavedRoom(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        savedRooms.Remove(saved); SaveRooms();
        SavedRoomsList.SelectedItem = savedRooms.FirstOrDefault(); RefreshSavedRooms();
    }
    private void CopySavedCode(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        try { Clipboard.SetText(saved.Code); SetStatus("Room code copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
    // The pill of the connected room shows a live dot.
    private void MarkLiveRoomPill()
    {
        var live = ConnectedSavedRoom;
        foreach (var saved in savedRooms)
            if (SavedRoomsList.ItemContainerGenerator.ContainerFromItem(saved) is ListBoxItem pill) pill.Tag = saved == live ? "live" : null;
    }
    // The selected room's last session: its picture when one is known, otherwise a card in the title's colour.
    private string? savedRoomSessionKey;
    private void ShowSavedRoomSession(SavedRoom? saved, bool host)
    {
        var session = saved is null ? null : LoadRoomSession(saved);
        SavedRoomSessionTitle.Text = session?.Title ?? "Nothing watched here yet";
        SavedRoomSessionTitle.Foreground = (System.Windows.Media.Brush)FindResource(session is null ? "Muted" : "Ink");
        SavedRoomSessionDetail.Text = session is null ? host ? "Pick something from your library once the room is open." : "It appears here after the host plays something."
            : (session.DurationMs > 0 && session.PositionMs >= session.DurationMs - 60000 ? "Finished" : "Stopped at " + FormatTime(session.PositionMs)) + " · " + Ago(session.UpdatedAt);
        SavedRoomSessionThumb.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
        if (saved is null || session is null) { savedRoomSessionKey = null; SavedRoomSessionImage.Source = null; return; }
        string? file = null;
        if (host) file = session.LibraryId is { } id && items.FirstOrDefault(x => x.Id == id) is { } item ? HeroArtworkPath(item) : null;
        else if (library.Setting(SavedRoomSetting(saved, "artwork")) == session.Title) file = HeroArtworkFile(saved);
        var key = session.Title + "|" + file + "|" + (file is not null && File.Exists(file) ? File.GetLastWriteTimeUtc(file).Ticks : 0);
        if (key == savedRoomSessionKey) return;
        savedRoomSessionKey = key;
        var hue = TitleHue(session.Title);
        var fill = new System.Windows.Media.LinearGradientBrush(Hsl(hue, .5, .16), Hsl(hue, .55, .34), 45); fill.Freeze();
        SavedRoomSessionThumb.Background = fill;
        SavedRoomSessionInitials.Text = Initials(session.Title, 2);
        SavedRoomSessionImage.Source = file is not null && File.Exists(file) ? LoadThumbnail(file) : null;
    }
    private static System.Windows.Media.Imaging.BitmapSource? LoadThumbnail(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var image = new System.Windows.Media.Imaging.BitmapImage(); image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return null; }
    }
    private void CopySavedInvitation(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        try { Clipboard.SetText(saved.Server + "#" + saved.Code); SetStatus("Invitation copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
}
