using System.IO;
using System.Windows;
using Watchroom.Core;
namespace Watchroom.Desktop;
public partial class MainWindow
{
    private RoomLibrary? roomLibrary;
    private RoomLibraryWindow? libraryWindow;
    private readonly Dictionary<string, MediaItem> sharedItems = new();
    private readonly Dictionary<string, string> sharedIds = new();
    private string[] sharedKinds = [];
    private string HostPeerId() => room?.Snapshot?.People.FirstOrDefault(p => p.IsHost)?.Id ?? room?.Identity?.Peer ?? "";
    private void EnsureRoomLibrary()
    {
        if (roomLibrary is not null || room?.Identity is not { } identity) return;
        var client = room;
        var session = new RoomLibrary(identity.Host, identity.Peer); roomLibrary = session;
        session.Send += (id, message) =>
        {
            if (room != client) return;
            try { client.SendLibrary(id, message); } catch (IOException ex) { SetStatus(ex.Message); }
        };
        session.Changed += () =>
        {
            if (session.Queue.Entries.Length > 0) QueueStatus.Text = "Room up next: " + string.Join(" → ", session.Queue.Entries.Select(e => e.Title));
            UpdateSessionControls();
        };
        session.Activity += entry =>
        {
            chat.Add(entry.Actor + " " + entry.Text); if (chat.Count > 150) chat.RemoveAt(0);
        };
        session.Result += result => SetStatus(result.Text);
        session.StartRequested += async request => await StartLibraryVideo(client, session, request);
        session.SetPeople(client.Snapshot?.People ?? []);
    }
    private void OpenRoomLibrary(object sender, RoutedEventArgs e)
    {
        EnsureRoomLibrary();
        if (roomLibrary is null || room?.Identity is null) { SetStatus("Create or join a room first."); return; }
        libraryWindow ??= new RoomLibraryWindow(roomLibrary, room.Identity.Host, HostPeerId,
            () => room?.Snapshot?.People ?? [], PublishRoomLibrary) { Owner = this };
        if (room.Identity.Host) libraryWindow.ShowAccess();
        libraryWindow.Show(); libraryWindow.Activate();
    }
    private void PublishRoomLibrary(string[] kinds)
    {
        if (roomLibrary is null || room?.Identity?.Host != true) return;
        sharedKinds = kinds; sharedItems.Clear();
        var catalog = new List<SharedLibraryItem>();
        foreach (var item in items.Where(i => kinds.Contains(i.Kind) && !i.IsExtra && !i.IsVirtual).Take(RoomLibrary.MaxCatalog))
        {
            if (!sharedIds.TryGetValue(item.Id, out var id)) sharedIds[item.Id] = id = Guid.NewGuid().ToString("N");
            sharedItems[id] = item;
            catalog.Add(new(id, item.Title, item.Kind, item.Year, item.Series, item.Season, item.Episode,
                item.Overview is { Length: > 2000 } summary ? summary[..2000] : item.Overview, item.Available, MakeThumbnail(item.Poster)));
        }
        roomLibrary.Publish(catalog);
    }
    private static string? MakeThumbnail(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
            bitmap.DecodePixelWidth = 100; bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit();
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 60 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output);
            return output.Length <= 12000 ? Convert.ToBase64String(output.ToArray()) : null;
        }
        catch (Exception) { return null; }
    }
    private async Task StartLibraryVideo(RoomClient client, RoomLibrary session, LibraryStart request)
    {
        try
        {
            if (!sharedItems.TryGetValue(request.Item.Id, out var item)) throw new FileNotFoundException("Title is no longer shared.");
            var prepared = await Task.Run(() => RoomClient.PrepareHostedFile(item.Path, request.Item.DisplayTitle));
            if (room != client || roomLibrary != session || !session.CanCommit(request))
            { session.CompleteStart(request, false); return; }
            // No await between final authorization and publication: revocation cannot race the commit.
            client.PublishHostedFile(prepared, true);
            selected = playingItem = item; session.CompleteStart(request, true);
        }
        catch (Exception ex) { session.CompleteStart(request, false, ex.Message); }
    }
}
