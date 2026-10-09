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
        if (savedRooms.Count > 0) SavedRoomsList.SelectedIndex = 0;
        RefreshSavedRooms();
    }
    private void RefreshSavedRooms()
    {
        if (SavedRoomsList is null) return;
        ReturnToRoomButton.IsEnabled = ready || room is not null;
        ReturnToRoomButton.Visibility = ReturnToRoomButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        RoomsEmptyState.Visibility = savedRooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CreateSavedRoomButton.Visibility = AddSavedInvitationButton.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomsContent.Visibility = savedRooms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedRoomDetails.Visibility = SavedRoomsList.SelectedItem is SavedRoom ? Visibility.Visible : Visibility.Collapsed;
        ConnectSavedRoomButton.IsEnabled = !roomBusy && SavedRoomsList.SelectedItem is SavedRoom;
        ForgetSavedRoomButton.IsEnabled = CopySavedInviteButton.IsEnabled = SavedRoomsList.SelectedItem is SavedRoom;
        RefreshRoomSettings();
    }
    private string SavedRoomSetting(SavedRoom saved, string field) => "room:" + saved.Server + ":" + saved.Code.ToUpperInvariant() + ":" + field;
    private void RefreshRoomSettings()
    {
        if (SavedRoomSharedControls is null) return;
        var saved = SavedRoomsList.SelectedItem as SavedRoom;
        var connected = saved is not null && room?.IsConnected == true && room.ServerAddress == saved.Server && room.Identity?.Room == saved.Code;
        var host = saved is not null && (connected ? room!.Identity!.Host : saved.HostKey is not null);
        SavedRoomHostControls.Visibility = SavedRoomGuestManagement.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        RenameSavedRoomButton.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        RenameSavedRoomButton.IsEnabled = host;
        RenameSavedRoomButton.ToolTip = host ? "Change the room name for everyone" : "Only the host can change the room name";
        SavedRoomHeading.Text = saved?.Name ?? "Choose a room";
        SavedRoomStatus.Text = saved is null ? "Create a room or save an invitation to get started." : connected ? (host ? "Connected · You are the host" : "Connected · Guest") : (host ? "Your room · Offline" : "Saved invitation · Offline");
        ConnectSavedRoomButton.Content = connected ? "Open player" : "Connect";
        SavedRoomLibraryButton.IsEnabled = connected;
        SavedRoomLibraryButton.Content = host ? "Room library" : "Browse host library";
        SavedRoomSharedControls.IsEnabled = host;
        updatingLibraryPermission = true;
        SavedRoomLibraryBrowsing.IsEnabled = host;
        SavedRoomLibraryBrowsing.IsChecked = connected ? room!.Snapshot?.LibraryBrowsing == true : saved is not null && library.Setting(SavedRoomSetting(saved, "libraryBrowsing")) == "true";
        updatingLibraryPermission = false;
        SavedRoomSharedControls.IsChecked = connected ? room!.Snapshot?.SharedControls == true : saved is not null && library.Setting(SavedRoomSetting(saved, "sharedControls")) == "true";
        AdmittedGuest[] remembered = [];
        if (saved is not null && host)
        {
            if (connected) { remembered = room!.Snapshot?.AdmittedGuests ?? []; library.Setting(SavedRoomSetting(saved, "guests"), Wire.Serialize(remembered)); }
            else try { remembered = Wire.Read<AdmittedGuest[]>(library.Setting(SavedRoomSetting(saved, "guests")) ?? "[]"); } catch (System.Text.Json.JsonException) { }
        }
        var active = connected && host ? room!.Snapshot?.People.Where(p => !p.IsHost).ToArray() ?? [] : [];
        var rows = active.Select(p => new RoomGuestRow(p.Id, p.Name, p.Approved ? "Admitted · Connected" : "Waiting for approval", !p.Approved, connected && host, pendingAdmissions.ContainsKey(p.Id))).ToList();
        rows.AddRange(remembered.Where(p => !active.Any(a => a.GuestId == p.Id)).Select(p => new RoomGuestRow(p.Id, p.Name, "Admitted · Remembered", false, connected && host, false)));
        SavedRoomGuests.ItemsSource = rows;
        SavedRoomGuestHint.Text = !host ? "Connect as the host to manage guests." : connected ? "Admitted guests can reconnect. Remove revokes access." : "Connect to admit or remove guests.";
    }
    private sealed record RoomGuestRow(string Id, string Name, string Status, bool Waiting, bool CanRemove, bool Pending)
    {
        public Visibility AdmitVisibility => Waiting ? Visibility.Visible : Visibility.Collapsed;
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
        RefreshSavedRooms();
    }
    private static string? ValidateRoomName(string name) => name.Length > 100 || name.Any(char.IsControl)
        ? "Use at most 100 characters without control characters." : null;
    private async void CreatePersistentRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy) return;
        var name = Dialogs.Prompt(this, "Create room", "Room name", validate: ValidateRoomName); if (name is null) return;
        pendingRoomName = name; roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(SavedServerAddress, SavedDisplayName, persistent: true, roomName: name); }
            catch { await Disconnect(); throw; }
            RememberCurrentRoom(); RoomHeading.Text = name; RoomSubtitle.Text = "Share an invitation, then choose a video from the library.";
            roomPanelVisible = true; ShowPage("Rooms");
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
        roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(saved.Server, SavedDisplayName, saved.Code, saved.HostKey, roomName: saved.HostKey is null ? null : saved.Name); }
            catch { await Disconnect(); throw; }
            RoomHeading.Text = client.Snapshot?.Name ?? saved.Name;
            RoomSubtitle.Text = saved.HostKey is null ? "Waiting for host approval" : "Choose a video from the library.";
            RememberCurrentRoom(); roomPanelVisible = true; ShowPage("Rooms");
        }
        finally { roomBusy = false; RefreshSavedRooms(); }
    });
    private async void RenameSavedRoom(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved || !RenameSavedRoomButton.IsEnabled) return Task.CompletedTask;
        var name = Dialogs.Prompt(this, "Rename room", "Room name", saved.Name, ValidateRoomName); if (name is null) return Task.CompletedTask;
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
        savedRooms.Remove(saved); SaveRooms(); RefreshSavedRooms();
    }
    private void CopySavedInvitation(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        try { Clipboard.SetText(saved.Server + "#" + saved.Code); SetStatus("Invitation copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
}
