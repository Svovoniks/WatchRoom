using System.IO;
using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    // The last session of each saved room, kept per participant. While connected, snapshots from the host overwrite it.
    // Cached by setting key: snapshots and the hero read it often.
    private readonly Dictionary<string, RoomSession?> savedRoomSessions = [];
    private long lastRoomSessionSave, lastRoomHeroRefresh;
    private LibraryCardView? roomHeroArtwork;

    private RoomSession? LoadRoomSession(SavedRoom saved)
    {
        var key = SavedRoomSetting(saved, "session");
        if (savedRoomSessions.TryGetValue(key, out var cached)) return cached;
        RoomSession? session = null;
        try { session = library.Setting(key) is { } json ? Wire.Read<RoomSession>(json) : null; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { }
        return savedRoomSessions[key] = session is not null && RoomSessions.Valid(session) ? session : null;
    }
    private SavedRoom? ConnectedSavedRoom => room?.IsConnected == true && room.Identity is { } identity
        ? savedRooms.FirstOrDefault(x => x.Server == room.ServerAddress && x.Code == identity.Room) : null;

    // Called for every snapshot, every few seconds of playback, and before disconnecting.
    private void RecordRoomSession(bool force = false)
    {
        if (room?.Snapshot is not { } snapshot || room.Identity is not { } identity || ConnectedSavedRoom is not { } saved) return;
        var mono = Environment.TickCount64;
        if (!force && mono - lastRoomSessionSave < 5000 && snapshot.Media is not null) return;
        var stored = LoadRoomSession(saved);
        var duration = snapshot.Media is { } media && loadedMedia == media.Id && player is { Length: > 0 } ? player.Length : 0;
        var session = RoomSessions.Observe(stored, snapshot, identity.Peer, identity.Host, room.ServerNowMs, Wire.Now, duration,
            identity.Host && playingItem?.Path is { Length: > 0 } ? playingItem.Id : null);
        if (session is null || ReferenceEquals(session, stored) || !RoomSessions.Valid(session)) return;
        lastRoomSessionSave = mono;
        // Skip the write when only the time stamp moved, such as while paused.
        if (stored is not null && session with { UpdatedAt = 0, People = null } == stored with { UpdatedAt = 0, People = null } && (session.People ?? []).SequenceEqual(stored.People ?? [])) return;
        savedRoomSessions[SavedRoomSetting(saved, "session")] = session;
        library.Setting(SavedRoomSetting(saved, "session"), Wire.Serialize(session));
    }

    // The host shares its record so guests who connect while nothing is playing still see the room's last session.
    private void PublishRoomSession(RoomClient client, SavedRoom saved)
    {
        if (client.Identity?.Host != true || LoadRoomSession(saved) is not { } session) return;
        try { client.Send(new("last-session", Data: Wire.Serialize(session with { LibraryId = null }))); }
        catch (IOException) { }
    }

    // Reopens the host's last video paused where the room stopped, so returning to the player resumes the session.
    private void RestoreRoomSession(RoomClient client, SavedRoom saved)
    {
        if (client.Identity?.Host != true || client.HostedMedia is not null || LoadRoomSession(saved) is not { LibraryId: { } id } session) return;
        if (items.FirstOrDefault(item => item.Id == id) is not { } item || !File.Exists(item.Path)) return;
        try { client.PublishHostedFile(RoomClient.PrepareHostedFile(item.Path, PlaybackTitle(item)), false, session.PositionMs); playingItem = item; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { SetStatus("Could not reopen the last video: " + ex.Message); }
    }

    private void RoomSessionTick()
    {
        if (room?.IsConnected == true) RecordRoomSession();
        if (room is { IsConnected: true, Identity.Host: true } host && ConnectedSavedRoom is { } saved)
        {
            // Guests may fetch only this title's artwork from the host.
            var session = LoadRoomSession(saved);
            var item = HeroArtworkItem(host.Snapshot?.Media is not null, session);
            host.SetSessionArtwork(host.Snapshot?.Media?.Title ?? session?.Title, item is null ? null : HeroArtworkPath(item));
        }
        var mono = Environment.TickCount64;
        if (RoomsPage.Visibility == Visibility.Visible && mono - lastRoomHeroRefresh >= 1000) RefreshRoomHero();
    }

    private void RefreshRoomHero()
    {
        if (RoomHero is null) return;
        lastRoomHeroRefresh = Environment.TickCount64;
        var connected = ConnectedSavedRoom;
        // The connected room, else the room watched most recently, else the selected or first saved room.
        var heroRoom = connected ?? savedRooms.Where(saved => LoadRoomSession(saved) is not null)
            .OrderByDescending(saved => LoadRoomSession(saved)!.UpdatedAt).FirstOrDefault()
            ?? SavedRoomsList.SelectedItem as SavedRoom ?? savedRooms.FirstOrDefault();
        var session = heroRoom is null ? null : LoadRoomSession(heroRoom);
        var snapshot = connected is null ? null : room!.Snapshot;
        var live = snapshot?.Media;
        // The hero takes over for saved rooms; the header button stays for local playback.
        ReturnToRoomButton.Visibility = (room is null ? ready : connected is null) ? Visibility.Visible : Visibility.Collapsed;
        if (heroRoom is null)
        {
            RoomHero.Visibility = Visibility.Collapsed; RoomHero.DataContext = roomHeroArtwork = null; ShowRoomHeroShimmer(false); return;
        }
        RoomHero.Visibility = Visibility.Visible;
        var current = live is not null && session?.Title == live.Title ? session : null;
        var title = live?.Title ?? session?.Title;
        var hostIdentity = connected is not null ? room!.Identity!.Host : heroRoom.HostKey is not null;
        RoomHeroLiveText.Text = connected is not null ? "LIVE" : hostIdentity ? "CLOSED" : "OFFLINE";
        RoomHeroLive.Background = (System.Windows.Media.Brush)FindResource(connected is not null ? "Success" : "Line");
        RoomHeroLiveText.Foreground = (System.Windows.Media.Brush)FindResource(connected is not null ? "Canvas" : "Muted");
        RoomHeroKicker.Text = connected is not null
            ? heroRoom.Name + (live is null ? session is null ? " · Nothing playing yet" : " · Last played here " + Ago(session.UpdatedAt) : snapshot!.Playback?.WantsPlayback == true ? " · Playing now" : " · Paused")
            : session is null ? heroRoom.Name : "Last watched in " + heroRoom.Name + " · " + Ago(session.UpdatedAt);
        RoomHeroTitle.Text = title ?? (connected is null ? "Nothing watched here yet" : hostIdentity ? "Choose a video from your library" : "Waiting for the host to choose a video");
        RoomHeroTitle.Foreground = (System.Windows.Media.Brush)FindResource(title is null && !(connected is not null && hostIdentity) ? "Muted" : "Ink");
        var position = live is not null && snapshot!.Playback is { } playback && playback.MediaId == live.Id && playback.Stop?.Reason != "loading"
            ? SyncMath.TargetPosition(playback, room!.ServerNowMs) : (live is null || current is not null) ? session?.PositionMs ?? 0 : 0;
        var duration = live is not null && loadedMedia == live.Id && player is { Length: > 0 } ? player.Length : (live is null || current is not null) ? session?.DurationMs ?? 0 : 0;
        var hasTime = title is not null && (position > 0 || duration > 0);
        RoomHeroProgress.Visibility = RoomHeroTime.Visibility = hasTime ? Visibility.Visible : Visibility.Collapsed;
        RoomHeroProgress.Value = duration > 0 ? Math.Clamp(position * 100d / duration, 0, 100) : 0;
        RoomHeroProgress.Visibility = duration > 0 && hasTime ? Visibility.Visible : Visibility.Collapsed;
        RoomHeroTime.Text = duration > 0 ? FormatTime(position) + " / " + FormatTime(duration) : FormatTime(position) + " watched";
        RoomHeroTime.Margin = new(duration > 0 ? 12 : 0, 0, 0, 0);
        if (snapshot is not null)
        {
            var presence = RoomSessions.Presence(snapshot, room!.Identity!.Peer, SavedDisplayName, session?.People);
            RoomHeroPresence.Text = presence.Text;
            RoomHeroPresenceDot.Fill = (System.Windows.Media.Brush)FindResource(presence.Everyone ? "Success" : "Accent");
        }
        else if (session is not null)
        {
            var people = (session.People ?? []).Where(name => name != SavedDisplayName).ToArray();
            RoomHeroPresence.Text = people.Length > 0 ? "Last time with " + RoomSessions.Names(people) : session.Host is { } host && host != SavedDisplayName ? "Hosted by " + host : "You watched alone";
            RoomHeroPresenceDot.Fill = (System.Windows.Media.Brush)FindResource("Muted");
        }
        else
        {
            RoomHeroPresence.Text = hostIdentity ? "Open the room, then pick something from your library" : "You're a guest · ask the host to open the room";
            RoomHeroPresenceDot.Fill = (System.Windows.Media.Brush)FindResource("Muted");
        }
        // Offline: resume the last session or connect. Connected: return to the player, or for the host with nothing chosen, browse the library.
        var browse = connected is not null && title is null && hostIdentity;
        RoomHeroResume.Content = connected is not null ? browse ? "Browse library" : "Return to player" : session is not null ? "Resume" : hostIdentity ? "Open room" : "Connect";
        RoomHeroResume.ContentTemplate = browse ? null : (DataTemplate)FindResource("PlayContent");
        RoomHeroResume.Visibility = connected is not null && title is null && !hostIdentity ? Visibility.Collapsed : Visibility.Visible;
        RoomHeroResume.IsEnabled = connected is not null || !roomBusy;
        RoomHeroResume.Tag = heroRoom.Id;
        System.Windows.Automation.AutomationProperties.SetName(RoomHeroResume, RoomHeroResume.Content + " in " + heroRoom.Name);
        var secondary = connected is not null ? title is null ? "Open player" : null : session is null && hostIdentity ? "Copy invite" : null;
        RoomHeroSecondary.Content = secondary;
        RoomHeroSecondary.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        RoomHeroDisconnect.Content = hostIdentity ? "Close room" : "Disconnect";
        System.Windows.Automation.AutomationProperties.SetName(RoomHeroDisconnect, (hostIdentity ? "Close room " : "Disconnect from ") + heroRoom.Name);
        RoomHeroDisconnect.Visibility = connected is not null ? Visibility.Visible : Visibility.Collapsed;
        RoomHeroDisconnect.IsEnabled = !roomBusy;
        ShowRoomHeroPlaceholder(heroRoom, title);
        // The host reads artwork from its library; guests show the copy they saved from the host.
        if (!hostIdentity) { ShowGuestHeroArtwork(heroRoom, title, connected is not null); return; }
        ShowRoomHeroShimmer(false);
        roomHeroImageKey = null;
        var item = title is null ? null : HeroArtworkItem(live is not null, session);
        if (item is null) { if (RoomHero.DataContext is not null) RoomHero.DataContext = roomHeroArtwork = null; return; }
        if (roomHeroArtwork?.Card.Media.Id == item.Id && RoomHero.DataContext == roomHeroArtwork) return;
        roomHeroArtwork = new(item.Series is null ? new LibraryCard(item.DisplayTitle, item.Caption, item.Poster, item, "movie")
            : new LibraryCard(item.Series + " · " + item.EpisodeDisplayTitle, item.Caption, LibraryCatalog.EpisodeArtwork(item), item, "episode", item.Season));
        RoomHero.DataContext = roomHeroArtwork; _ = roomHeroArtwork.LoadPosterAsync();
    }

    // Shown under the picture: the room's initial before anything was chosen, otherwise a card with the title's initials in a colour of its own.
    private string? roomHeroPlaceholderKey;
    private void ShowRoomHeroPlaceholder(SavedRoom saved, string? title)
    {
        var key = title is null ? "room:" + saved.Name : "title:" + title;
        if (key == roomHeroPlaceholderKey) return;
        roomHeroPlaceholderKey = key;
        RoomHeroBadge.Visibility = title is null ? Visibility.Visible : Visibility.Collapsed;
        RoomHeroTitleCard.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        if (title is null) { RoomHeroBadgeText.Text = Initials(saved.Name, 1); return; }
        RoomHeroTitleCardText.Text = Initials(title, 2);
        var hue = TitleHue(title);
        var fill = new System.Windows.Media.LinearGradientBrush(new System.Windows.Media.GradientStopCollection
        {
            new(Hsl(hue, .5, .12), 0), new(Hsl(hue, .55, .32), .6), new(Hsl((hue + 20) % 360, .5, .18), 1),
        }, new Point(0, 0), new Point(1, 1));
        fill.Freeze(); RoomHeroTitleCardFill.Fill = fill;
    }
    // Stable across runs, so a show keeps its colour from session to session.
    internal static int TitleHue(string title) => (int)(Convert.ToUInt32(LibraryIdentity.Hash(title)[..8], 16) % 360);
    // The first letters of the leading words, skipping short joining words when there are others.
    internal static string Initials(string text, int count)
    {
        var name = text.Split(" · ")[0];
        var words = System.Text.RegularExpressions.Regex.Matches(name, @"[\p{L}\p{N}]+").Select(match => match.Value).ToArray();
        var significant = words.Where(word => word.Length > 1 && word.ToLowerInvariant() is not ("the" or "an" or "of" or "and")).ToArray();
        var source = significant.Length > 0 ? significant : words;
        return source.Length == 0 ? "?" : string.Concat(source.Take(count).Select(word => char.ToUpperInvariant(word[0])));
    }
    private static System.Windows.Media.Color Hsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var (r, g, b) = hue < 60 ? (chroma, x, 0d) : hue < 120 ? (x, chroma, 0d) : hue < 180 ? (0d, chroma, x) : hue < 240 ? (0d, x, chroma) : hue < 300 ? (x, 0d, chroma) : (chroma, 0d, x);
        var m = lightness - chroma / 2;
        return System.Windows.Media.Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
    private void ShowRoomHeroShimmer(bool show)
    {
        if ((RoomHeroShimmer.Visibility == Visibility.Visible) == show) return;
        RoomHeroShimmer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // The sweep only runs while it is visible.
        RoomHeroShimmerShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, show && SystemParameters.ClientAreaAnimation
            ? new System.Windows.Media.Animation.DoubleAnimation(1, -1, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever } : null);
    }
    private MediaItem? HeroArtworkItem(bool live, RoomSession? session)
    {
        var id = (live ? playingItem?.Id : null) ?? session?.LibraryId;
        return id is null ? null : items.FirstOrDefault(x => x.Id == id);
    }
    private static string? HeroArtworkPath(MediaItem item) => item.Series is null ? item.Poster : LibraryCatalog.EpisodeArtwork(item);

    // Guests save the host's artwork per room, with the title it belongs to, and ask the host when it is missing or stale.
    private sealed record HeroImage(System.Windows.Media.Imaging.BitmapSource Poster);
    private string? roomHeroImageKey;
    private readonly HashSet<string> requestedHeroArtwork = [];
    private (long Number, string Title, string Room, bool Full, long Started, SortedDictionary<int, string> Chunks)? pendingHeroArtwork;
    private long heroArtworkSequence;
    private string HeroArtworkFile(SavedRoom saved) => Path.Combine(App.DataDirectory, "room-artwork", LibraryIdentity.Hash(saved.Server + "#" + saved.Code.ToUpperInvariant()) + ".jpg");

    private void ShowGuestHeroArtwork(SavedRoom saved, string? title, bool connected)
    {
        roomHeroArtwork = null;
        var file = HeroArtworkFile(saved);
        var stored = title is not null && library.Setting(SavedRoomSetting(saved, "artwork")) == title && File.Exists(file);
        var key = stored ? file + "|" + title + "|" + File.GetLastWriteTimeUtc(file).Ticks : null;
        if (key != roomHeroImageKey)
        {
            roomHeroImageKey = key;
            RoomHero.DataContext = key is null ? null : LoadHeroImage(file) is { } image ? new HeroImage(image) : null;
        }
        // Show the 600 px copy first, then replace it with full HD when the connection has room.
        if (!stored && connected && title is not null) RequestHeroArtwork(saved, title, false);
        else if (stored && connected && library.Setting(SavedRoomSetting(saved, "artworkFull")) != title && CanUpgradeArtwork()) RequestHeroArtwork(saved, title!, true);
        // Shimmer while the first copy is on its way, for up to 15 seconds; then the title card stays.
        if (stored || !connected || title is null) { heroArtworkWait = null; ShowRoomHeroShimmer(false); return; }
        if (heroArtworkWait?.Title != title) heroArtworkWait = (title, Environment.TickCount64);
        var waiting = pendingHeroArtwork is { Full: false } pending && pending.Title == title || !requestedHeroArtwork.Contains(title);
        ShowRoomHeroShimmer(waiting && Environment.TickCount64 - heroArtworkWait.Value.Since < 15000);
    }
    private (string Title, long Since)? heroArtworkWait;
    private static System.Windows.Media.Imaging.BitmapSource? LoadHeroImage(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var image = new System.Windows.Media.Imaging.BitmapImage(); image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return null; }
    }
    private void RequestHeroArtwork(SavedRoom saved, string title, bool full)
    {
        // Ask once per title, size and connection; the host answers with an empty reply when it has nothing.
        var request = (full ? "full:" : "") + title;
        // A reply that never completes frees the slot after 30 seconds and counts as a slow link.
        if (pendingHeroArtwork is { } stale && Environment.TickCount64 - stale.Started > 30000) { pendingHeroArtwork = null; artworkBandwidth.Slowed(); }
        if (room is not { Identity.Host: false } client || pendingHeroArtwork is not null || !requestedHeroArtwork.Add(request)) return;
        var number = -1_000_000_000 - ++heroArtworkSequence;
        try { client.Send(new("session-artwork", Number: number, Text: title, Data: full ? "full" : null)); pendingHeroArtwork = (number, title, saved.Id, full, Environment.TickCount64, []); }
        // Before the host's direct channel opens, retry with the next refresh.
        catch (IOException) { requestedHeroArtwork.Remove(request); }
    }
    private void ResetHeroArtworkRequests() { requestedHeroArtwork.Clear(); pendingHeroArtwork = null; }
    private bool ReceiveHeroArtworkError(WireMessage message)
    {
        // Hosts on older versions reject the request; treat that as no artwork instead of showing an error.
        if (pendingHeroArtwork?.Number != message.Number) return false;
        pendingHeroArtwork = null; return true;
    }
    private void ReceiveHeroArtwork(WireMessage message)
    {
        if (pendingHeroArtwork is not { } pending || pending.Number != message.Number) return;
        var chunk = Wire.Read<LibraryArtworkChunk>(message.Data!);
        if (chunk.Key != pending.Title || !ArtworkTransfer.ValidChunk(chunk)) return;
        pending.Chunks[chunk.Index] = chunk.Data;
        if (chunk.Paced) AcknowledgeArtwork(message.Number);
        if (pending.Chunks.Count < chunk.Count) return;
        pendingHeroArtwork = null;
        var data = string.Concat(pending.Chunks.Values);
        if (data.Length > 0) artworkBandwidth.Record(data.Length, Environment.TickCount64 - pending.Started);
        // An empty full HD reply means the host is busy with video; ask again after a pause.
        else if (pending.Full) { fullArtworkDeclinedUntil = Environment.TickCount64 + 30000; requestedHeroArtwork.Remove("full:" + pending.Title); }
        if (data.Length == 0 || savedRooms.FirstOrDefault(x => x.Id == pending.Room) is not { } saved) return;
        try
        {
            var file = HeroArtworkFile(saved);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Convert.FromBase64String(data));
            library.Setting(SavedRoomSetting(saved, "artwork"), pending.Title);
            library.Setting(SavedRoomSetting(saved, "artworkFull"), pending.Full ? pending.Title : "");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { return; }
        RefreshRoomHero();
    }

    private static string Ago(long unixMs)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return age.TotalMinutes < 2 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago"
            : age.TotalDays < 2 ? "yesterday" : age.TotalDays < 30 ? $"{(int)age.TotalDays} days ago" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("d MMM yyyy");
    }

    private async void ResumeRoomHero(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (savedRooms.FirstOrDefault(x => x.Id == RoomHeroResume.Tag as string) is not { } saved) return;
        if (ConnectedSavedRoom?.Id == saved.Id)
        {
            // The host with nothing chosen browses the library instead.
            ShowPage(room?.Identity?.Host == true && room.Snapshot?.Media is null && LoadRoomSession(saved) is null ? "Library" : "Room");
            return;
        }
        SavedRoomsList.SelectedItem = saved;
        // With nothing watched yet there is no session to return to; connecting keeps the Rooms page.
        var resume = LoadRoomSession(saved) is not null;
        if (await ConnectToSavedRoom(saved) && resume) ShowPage("Room");
    });
    private void RoomHeroSecondaryClick(object sender, RoutedEventArgs e)
    {
        if (savedRooms.FirstOrDefault(x => x.Id == RoomHeroResume.Tag as string) is not { } saved) return;
        // Connected with nothing playing: open the player. Offline host: copy the invitation.
        if (ConnectedSavedRoom?.Id == saved.Id) { ShowPage("Room"); return; }
        try { Clipboard.SetText(saved.Server + "#" + saved.Code); SetStatus("Invitation copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
}
