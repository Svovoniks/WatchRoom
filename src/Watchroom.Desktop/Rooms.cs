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
        SavedRoomsList.ItemsSource = savedRooms; RefreshSavedRooms();
    }
    private void RefreshSavedRooms()
    {
        if (SavedRoomsList is null) return;
        ReturnToRoomButton.IsEnabled = ready || room is not null;
        ConnectSavedRoomButton.IsEnabled = !roomBusy && SavedRoomsList.SelectedItem is SavedRoom;
        RenameSavedRoomButton.IsEnabled = ForgetSavedRoomButton.IsEnabled = CopySavedInviteButton.IsEnabled = SavedRoomsList.SelectedItem is SavedRoom;
    }
    private void SaveRooms() => library.Setting("rooms", Wire.Serialize(savedRooms));
    private void SavedRoomSelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshSavedRooms();
    private void RememberCurrentRoom()
    {
        if (room?.Identity is not { } identity || room.ServerAddress is null) return;
        var existing = savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room);
        if (existing is null)
        {
            existing = new(Guid.NewGuid().ToString("N"), pendingRoomName ?? "Room " + identity.Room[..6], room.ServerAddress, identity.Room, identity.HostKey);
            savedRooms.Add(existing);
        }
        else if (identity.HostKey is not null) savedRooms[savedRooms.IndexOf(existing)] = existing with { HostKey = identity.HostKey };
        SaveRooms(); SavedRoomsList.SelectedItem = savedRooms.First(x => x.Id == existing.Id);
    }
    private async void CreatePersistentRoom(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (roomBusy) return;
        var name = Dialogs.Prompt(this, "Create room", "Room name"); if (name is null) return;
        pendingRoomName = name; roomBusy = true; RefreshSavedRooms();
        try
        {
            await Disconnect(); var client = NewRoom();
            try { await client.ConnectAsync(ServerBox.Text, DisplayNameBox.Text, persistent: true); }
            catch { await Disconnect(); throw; }
            RememberCurrentRoom(); RoomHeading.Text = name; RoomSubtitle.Text = "Share an invitation, then choose a video from the library.";
            roomPanelVisible = true; ShowPage("Room");
        }
        finally { pendingRoomName = null; roomBusy = false; RefreshSavedRooms(); }
    });
    private void SaveRoomInvitation(object sender, RoutedEventArgs e)
    {
        var invitation = Dialogs.Prompt(this, "Save room", "Invitation link or room code", validate: value =>
        { try { RoomAddress.Parse(value, ServerBox.Text); return null; } catch (ArgumentException ex) { return ex.Message; } });
        if (invitation is null) return;
        var address = RoomAddress.Parse(invitation, ServerBox.Text);
        var existing = savedRooms.FirstOrDefault(x => x.Server == address.Server && x.Code == address.Code);
        if (existing is not null) { SavedRoomsList.SelectedItem = existing; SetStatus("This room is already saved."); return; }
        var name = Dialogs.Prompt(this, "Save room", "Room name", "Room " + address.Code[..6]); if (name is null) return;
        var saved = new SavedRoom(Guid.NewGuid().ToString("N"), name, address.Server, address.Code);
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
            try { await client.ConnectAsync(saved.Server, DisplayNameBox.Text, saved.Code, saved.HostKey); }
            catch { await Disconnect(); throw; }
            RoomHeading.Text = saved.Name;
            RoomSubtitle.Text = saved.HostKey is null ? "Waiting for host approval" : "Choose a video from the library.";
            RememberCurrentRoom(); roomPanelVisible = true; ShowPage("Room");
        }
        finally { roomBusy = false; RefreshSavedRooms(); }
    });
    private void RenameSavedRoom(object sender, RoutedEventArgs e)
    {
        if (SavedRoomsList.SelectedItem is not SavedRoom saved) return;
        var name = Dialogs.Prompt(this, "Rename room", "Room name", saved.Name); if (name is null) return;
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
