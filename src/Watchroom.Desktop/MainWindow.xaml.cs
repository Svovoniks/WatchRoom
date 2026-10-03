using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow : Window
{
    private readonly LibraryStore library;
    private readonly ObservableCollection<LibraryFolder> folders = [];
    private readonly ObservableCollection<string> chat = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Queue<MediaItem> queue = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer scanDelay = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim loading = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private List<MediaItem> items = [];
    private MediaItem? selected;
    private LibVLC? vlc;
    private LibVLCSharp.Shared.MediaPlayer? player;
    private RoomClient? room;
    private RoomLibrary? roomLibrary;
    private RoomLibraryWindow? libraryWindow;
    private readonly Dictionary<string, MediaItem> sharedItems = new();
    private readonly Dictionary<string, string> sharedIds = new();
    private string[] sharedKinds = [];
    private MediaBridge? bridge;
    private PlaybackState? target;
    private string? loadedMedia;
    private bool scanning, seeking, tracksUpdating, ready, closing, fullscreen, light;
    private long tick, lastBufferNotice, appliedRevision = -1;
    private int loadGeneration;
    private WindowState previousState;

    public MainWindow()
    {
        InitializeComponent();
        library = new LibraryStore(App.DataDirectory);
        foreach (var f in Wire.Read<List<LibraryFolder>>(library.Setting("folders") ?? "[]")) folders.Add(f);
        FolderList.ItemsSource = folders; ChatList.ItemsSource = chat;
        DisplayNameBox.Text = library.Setting("name") ?? Environment.UserName;
        ServerBox.Text = library.Setting("server") ?? "http://localhost:5080";
        FfmpegBox.Text = library.Setting("ffmpeg") ?? "";
        AutoArtwork.IsChecked = library.Setting("artwork") == "true";
        RefreshLibrary(); WatchFolders();
        timer.Tick += (_, _) => PlaybackTick(); timer.Start();
        scanDelay.Tick += async (_, _) => { scanDelay.Stop(); if (!scanning) await Guard(Scan); };
        Loaded += async (_, _) =>
        {
            await Guard(async () =>
            {
                await Task.Run(() => LibVLCSharp.Shared.Core.Initialize());
                vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--quiet");
                player = new(vlc) { Volume = 80, EnableHardwareDecoding = true };
                Video.MediaPlayer = player;
                player.Playing += (_, _) => Dispatcher.BeginInvoke(() => { ready = true; RefreshTracks(); try { room?.Send(new("ready")); } catch { } if (target?.Playing == false) player?.SetPause(true); });
                player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => SetStatus("Playback failed. Check that the file is available and the connection is active."));
                player.Buffering += (_, e) =>
                {
                    if (e.Cache >= 100) { try { room?.Send(new("ready")); } catch { } }
                    if (e.Cache < 10 && ready && room is not null && target?.Playing == true && Wire.Now - lastBufferNotice > 5000)
                    { lastBufferNotice = Wire.Now; try { room.Send(new("buffering")); } catch { } }
                };
                player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => { PlayButton.Content = "Replay"; if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player.Length)); });
            });
            if (folders.Count == 0) ShowPage("Folders");
        };
    }
    private void SetStatus(string text) => StatusText.Text = text;
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Operation cancelled or timed out."); }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Watchroom", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowPage(string name)
    {
        LibraryPage.Visibility = name == "Library" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = name == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        RoomPage.Visibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void RefreshLibrary()
    {
        items = library.All(); FilterLibrary();
        SetStatus($"{items.Count(x => x.Available)} videos available · {folders.Count} folders");
        if (room?.Identity?.Host == true && roomLibrary is not null) PublishRoomLibrary(sharedKinds);
    }
    private void FilterLibrary()
    {
        if (PosterGrid is null || Category is null) return;
        var filter = Category.SelectedIndex;
        var query = SearchBox.Text.Trim();
        IEnumerable<MediaItem> visible = items.Where(x => x.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        visible = filter switch { 1 => visible.Where(x => x.Kind == "Movie"), 2 => visible.Where(x => x.Kind == "Show"), 3 => visible.Where(x => x.Kind == "Anime"), 4 => visible.Where(x => x.Poster is null), _ => visible };
        PosterGrid.ItemsSource = visible.GroupBy(x => x.Series is null ? x.Id : x.Kind + ":" + x.Series).Select(g => g.First()).ToList();
        EmptyLibrary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => FilterLibrary();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => FilterLibrary();
    private void SelectMovie(object sender, RoutedEventArgs e) => Select((MediaItem)((Button)sender).Tag);
    private void Select(MediaItem item)
    {
        selected = item; Details.Visibility = Visibility.Visible; DetailTitle.Text = item.DisplayTitle;
        DetailSummary.Text = (item.Available ? "" : "File unavailable — reconnect its drive. ") + (item.Overview ?? item.Caption);
        Episodes.Visibility = item.Series is null ? Visibility.Collapsed : Visibility.Visible;
        Episodes.ItemsSource = item.Series is null ? null : items.Where(x => x.Series == item.Series && x.Kind == item.Kind).ToList();
        if (item.Series is not null) Episodes.SelectedItem = item;
    }
    private void EpisodeChanged(object sender, SelectionChangedEventArgs e) { if (Episodes.SelectedItem is MediaItem item) { selected = item; DetailSummary.Text = item.Caption + (item.Available ? "" : " · File unavailable"); } }
    private void AddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a movie, show or anime folder", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames.Where(path => !folders.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))))
            folders.Add(new(path, ((ComboBoxItem)FolderKind.SelectedItem).Content.ToString()!));
        SaveFolders();
    }
    private void RemoveFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is LibraryFolder folder) { folders.Remove(folder); SaveFolders(); SetStatus("Folder removed. Rescan to update availability. Files are unchanged."); }
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
    private async void ScanClick(object sender, RoutedEventArgs e) => await Guard(Scan);
    private async Task Scan()
    {
        if (scanning) return; scanning = true;
        try
        {
            var progress = new Progress<string>(text => { ScanStatus.Text = text; SetStatus(text); });
            await library.ScanAsync(folders.ToArray(), progress, lifetime.Token); RefreshLibrary();
            ScanStatus.Text = $"Scan complete · {items.Count} videos indexed.";
            if (AutoArtwork.IsChecked == true && MetadataToken.Password.Length > 0)
            {
                using var metadata = new MetadataClient(MetadataToken.Password);
                foreach (var item in items.Where(x => !x.Matched && x.Available).GroupBy(x => x.Series ?? x.Title).Select(g => g.First()).ToArray())
                {
                    SetStatus("Finding artwork · " + item.DisplayTitle);
                    var matches = await metadata.SearchAsync(item.DisplayTitle, item.Series is not null || item.Kind == "Show", lifetime.Token);
                    var exact = matches.Where(x => string.Equals(x.Title, item.DisplayTitle, StringComparison.OrdinalIgnoreCase) && (item.Year is null || x.Year == item.Year.ToString())).ToArray();
                    if (exact.Length == 1) await ApplyMatch(metadata, item, exact[0]);
                    await Task.Delay(200, lifetime.Token);
                }
                RefreshLibrary();
            }
        }
        finally { scanning = false; }
    }
    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        library.Setting("name", DisplayNameBox.Text.Trim()); library.Setting("server", ServerBox.Text.Trim().TrimEnd('/'));
        library.Setting("artwork", AutoArtwork.IsChecked == true ? "true" : "false"); library.Setting("ffmpeg", FfmpegBox.Text.Trim()); SetStatus("Settings saved. TMDB token is kept for this session only.");
    }
    private async void MatchClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null) return;
        if (MetadataToken.Password.Length == 0) { ShowPage("Settings"); SetStatus("Enter your TMDB API read access token, then return to Find artwork."); return; }
        var item = selected; var query = Dialogs.Prompt(this, "Find artwork", "Search by movie or series title", item.DisplayTitle); if (query is null) return;
        using var metadata = new MetadataClient(MetadataToken.Password);
        var results = await metadata.SearchAsync(query, item.Series is not null || item.Kind == "Show", lifetime.Token);
        if (results.Count == 0) { SetStatus("No matches. Try a different title or use a local poster."); return; }
        var match = Dialogs.Choose(this, results); if (match is null) return;
        await ApplyMatch(metadata, item, match); RefreshLibrary(); Select(items.First(x => x.Id == item.Id));
    });
    private async Task ApplyMatch(MetadataClient metadata, MediaItem item, MetadataMatch match)
    {
        var poster = await metadata.CachePosterAsync(match, Path.Combine(App.DataDirectory, "posters"), lifetime.Token);
        foreach (var entry in items.Where(x => x.Id == item.Id || (item.Series is not null && x.Series == item.Series && x.Kind == item.Kind)).ToArray())
            library.Save(entry with { Title = match.Title, Series = entry.Series is null ? null : match.Title, Poster = poster ?? entry.Poster, Overview = match.Overview, Year = int.TryParse(match.Year, out var year) ? year : entry.Year, Matched = true });
    }
    private void LocalPoster(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var picker = new OpenFileDialog { Filter = "Poster image|*.jpg;*.jpeg;*.png;*.webp" };
        if (picker.ShowDialog(this) != true) return;
        var dir = Path.Combine(App.DataDirectory, "posters"); Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, selected.Id + Path.GetExtension(picker.FileName)); File.Copy(picker.FileName, dest, true);
        foreach (var entry in items.Where(x => x.Id == selected.Id || (selected.Series is not null && x.Series == selected.Series))) library.Save(entry with { Poster = dest, Matched = true });
        RefreshLibrary();
    }
    private async void PlayLocal(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null || player is null) return;
        await Disconnect(); target = null; loadedMedia = null;
        if (!File.Exists(selected.Path)) throw new FileNotFoundException("Movie file is unavailable. Reconnect its drive and rescan.");
        using var media = new Media(vlc!, selected.Path, FromType.FromPath);
        ready = false; player.Play(media); RoomHeading.Text = selected.Title; RoomSubtitle.Text = "Local playback"; ShowPage("Room");
    });
    private async void HostClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null) return;
        if (!File.Exists(selected.Path)) throw new FileNotFoundException("Movie file is unavailable.");
        await Disconnect(); var client = NewRoom();
        client.SetHostedFile(selected.Path, selected.Title);
        await client.ConnectAsync(ServerBox.Text, DisplayNameBox.Text);
        ShowPage("Room"); SetStatus("Creating private room…");
    });
    private async void JoinClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var invitation = Dialogs.Prompt(this, "Join a watch room", "Paste the invitation link or room code"); if (string.IsNullOrWhiteSpace(invitation)) return;
        var server = ServerBox.Text.Trim(); var code = invitation;
        if (invitation.Contains('#')) { var split = invitation.LastIndexOf('#'); server = invitation[..split]; code = invitation[(split + 1)..]; }
        await Disconnect(); var client = NewRoom(); await client.ConnectAsync(server, DisplayNameBox.Text, code);
        RoomSubtitle.Text = "Waiting for host approval"; ShowPage("Room");
    });
    private RoomClient NewRoom()
    {
        var client = new RoomClient(); room = client;
        client.Message += msg => Dispatcher.BeginInvoke(async () => { if (room == client) await Guard(() => OnRoomMessage(msg)); });
        client.LibraryPeerReady += id => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return;
            EnsureRoomLibrary();
            try { roomLibrary?.PeerReady(id); } catch (IOException ex) { SetStatus(ex.Message); }
        });
        client.LibraryMessage += (id, message) => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return;
            EnsureRoomLibrary();
            try { roomLibrary?.Receive(id, message); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException) { SetStatus("Invalid guest library request: " + ex.Message); }
        });
        client.Status += text => Dispatcher.BeginInvoke(() =>
        {
            if (room != client) return; ConnectionStatus.Text = text;
            if (text.StartsWith("Disconnected", StringComparison.Ordinal)) { player?.SetPause(true); target = null; SetStatus(text); }
        });
        return client;
    }
    private async Task OnRoomMessage(WireMessage message)
    {
        if (room is null) return;
        switch (message.Type)
        {
            case "welcome":
                EnsureRoomLibrary();
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
                if (roomLibrary is null) QueueStatus.Text = (snapshot.Queue?.Length > 0) ? "Up next: " + string.Join(" → ", snapshot.Queue) : "Queue is empty";
                PeopleList.ItemsSource = snapshot.People.Select(p => new { p.Id, p.Name, Status = !p.Approved ? "Waiting for approval" : (p.IsHost ? "Host · " : "") + (p.Ready ? "Ready" : "Buffering"), Actions = room.Identity?.Host == true && !p.IsHost ? Visibility.Visible : Visibility.Collapsed }).ToList();
                SharedControls.IsEnabled = room.Identity?.Host == true; SharedControls.IsChecked = snapshot.SharedControls;
                if (snapshot.Playback is not null) AcceptState(snapshot.Playback);
                if (snapshot.Media is not null && snapshot.Media.Id != loadedMedia) await LoadRoomMedia(snapshot.Media);
                break;
            case "playback": AcceptState(Wire.Read<PlaybackState>(message.Data!)); break;
            case "chat": case "notice":
                chat.Add(message.Text ?? ""); if (chat.Count > 150) chat.RemoveAt(0); ChatList.ScrollIntoView(chat.Last()); break;
            case "error": SetStatus(message.Text ?? "Room error"); break;
        }
    }
    private void AcceptState(PlaybackState state)
    {
        if (target is null || state.Revision > target.Revision) { target = state; appliedRevision = -1; }
    }
    private async Task LoadRoomMedia(SharedMedia media)
    {
        var client = room; if (client is null) return;
        var generation = ++loadGeneration; loadedMedia = media.Id;
        await loading.WaitAsync(lifetime.Token);
        try
        {
            if (client != room || generation != loadGeneration) return;
            ready = false; player?.Stop();
            if (target?.Playing == true) client.Send(new("buffering"));
            if (bridge is not null) await bridge.DisposeAsync(); bridge = null;
            var source = await client.GetSourceAsync(media, lifetime.Token);
            if (client != room || generation != loadGeneration) return;
            bridge = new(); var uri = await bridge.StartAsync(source);
            using var vlcMedia = new Media(vlc!, uri); vlcMedia.AddOption(":network-caching=1500");
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
                        await output.WriteAsync(bytes, lifetime.Token); offset += bytes.Length;
                    }
                vlcMedia.AddSlave(MediaSlaveType.Subtitle, 2, new Uri(subtitlePath).AbsoluteUri);
            }
            if (client != room || generation != loadGeneration) return;
            player!.Play(vlcMedia); RoomHeading.Text = media.Title; RoomSubtitle.Text = "Private room · " + (client.Identity?.Host == true ? "You are hosting" : "Streaming from host");
            ShowPage("Room");
        }
        catch { if (generation == loadGeneration) loadedMedia = null; throw; }
        finally { loading.Release(); }
    }
    private void PlaybackTick()
    {
        roomLibrary?.ExpireViews(Wire.Now);
        if (player is null) return;
        if (!seeking)
        {
            Timeline.Maximum = Math.Max(1, player.Length); Timeline.Value = Math.Max(0, player.Time);
            PlaybackTime.Text = FormatTime(player.Time) + " / " + FormatTime(player.Length);
        }
        PlayButton.Content = player.IsPlaying ? "Pause" : "Play";
        if (++tick % 8 == 0 && ready) RefreshTracks();
        if (room is null || target is null || !ready || seeking) return;
        long now = Wire.Now + room.ServerOffsetMs;
        if (now < target.AtUnixMs) { player.SetPause(true); return; }
        long desired = SyncMath.TargetPosition(target, now); long drift = desired - Math.Max(0, player.Time);
        if (appliedRevision != target.Revision || Math.Abs(drift) > 1200)
        {
            if (target.Playing && player.State is VLCState.Ended or VLCState.Stopped) player.Play();
            if (Math.Abs(drift) > 200) player.Time = desired;
            player.SetPause(!target.Playing); player.SetRate(1); appliedRevision = target.Revision;
        }
        else if (target.Playing) player.SetRate(SyncMath.Correction(drift));
    }
    private static string FormatTime(long milliseconds) => TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(milliseconds >= 3600000 ? @"h\:mm\:ss" : @"mm\:ss");
    private void SendPlayback(bool playing, long position)
    {
        if (room?.Snapshot?.Media is not { } media) return;
        if (room.Identity?.Host != true && room.Snapshot.SharedControls != true) { SetStatus("The host controls playback."); return; }
        room.Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, media.Id, playing, position, 0))));
    }
    private void TogglePlayback(object sender, RoutedEventArgs e)
    {
        if (player is null) return;
        if (room is not null) SendPlayback(!player.IsPlaying, player.Length > 0 && player.Time >= player.Length - 250 ? 0 : Math.Max(0, player.Time));
        else if (player.IsPlaying) player.SetPause(true); else player.Play();
    }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => seeking = true;
    private void SeekFinished(object sender, MouseButtonEventArgs e)
    {
        if (player is not null) { if (room is not null) SendPlayback(target?.Playing == true, (long)Timeline.Value); else player.Time = (long)Timeline.Value; }
        seeking = false;
    }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player is not null) player.Volume = (int)e.NewValue; }
    private void RefreshTracks()
    {
        if (player is null) return; tracksUpdating = true;
        try
        {
            var audio = player.AudioTrackDescription; var subs = player.SpuDescription;
            if (AudioTracks.Items.Count != audio.Length) AudioTracks.ItemsSource = audio;
            if (SubtitleTracks.Items.Count != subs.Length) SubtitleTracks.ItemsSource = subs;
            AudioTracks.SelectedItem = audio.FirstOrDefault(x => x.Id == player.AudioTrack);
            SubtitleTracks.SelectedItem = subs.FirstOrDefault(x => x.Id == player.Spu);
        }
        finally { tracksUpdating = false; }
    }
    private void AudioChanged(object sender, SelectionChangedEventArgs e) { if (!tracksUpdating && AudioTracks.SelectedItem is TrackDescription t) player?.SetAudioTrack(t.Id); }
    private void SubtitlesChanged(object sender, SelectionChangedEventArgs e) { if (!tracksUpdating && SubtitleTracks.SelectedItem is TrackDescription t) player?.SetSpu(t.Id); }
    private void LoadSubtitles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub" };
        if (picker.ShowDialog(this) == true) { player?.AddSlave(MediaSlaveType.Subtitle, new Uri(picker.FileName).AbsoluteUri, true); SetStatus("Subtitles loaded for your player."); }
    }
    private void CopyInvite(object sender, RoutedEventArgs e)
    {
        if (room?.Identity is null) { SetStatus("Create a room from the library first."); return; }
        Clipboard.SetText(ServerBox.Text.TrimEnd('/') + "#" + room.Identity.Room); SetStatus("Invitation copied. Guests need the Windows app and host approval.");
    }
    private void Admit(object sender, RoutedEventArgs e) => room?.Send(new("admit", Target: (string)((Button)sender).Tag));
    private void RemovePeer(object sender, RoutedEventArgs e)
    {
        var id = (string)((Button)sender).Tag;
        if (room?.Snapshot?.People.Any(p => p.Id == id && p.Approved) == true) roomLibrary?.SetAccess(id, new());
        room?.Send(new("remove", Target: id));
    }
    private void ControlsChanged(object sender, RoutedEventArgs e) => room?.Send(new("controls", Number: SharedControls.IsChecked == true ? 1 : 0));
    private void SendChat(object sender, RoutedEventArgs e)
    {
        if (room is null || string.IsNullOrWhiteSpace(ChatInput.Text)) return;
        room.Send(new("chat", Text: ChatInput.Text.Trim())); ChatInput.Clear();
    }
    private void ChatKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SendChat(sender, e); e.Handled = true; } }
    private void QueueClick(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        if (room is not null)
        {
            if (room.Identity?.Host == false) { OpenRoomLibrary(sender, e); SetStatus("Choose a title from the host library."); return; }
            var id = sharedItems.FirstOrDefault(pair => pair.Value.Id == selected.Id).Key;
            if (id is null) { OpenRoomLibrary(sender, e); SetStatus("Share this collection before adding its titles to the room queue."); return; }
            roomLibrary?.Request(HostPeerId(), "add", id); return;
        }
        if (queue.Count >= 50) { SetStatus("The queue can hold up to 50 titles."); return; }
        queue.Enqueue(selected); PublishQueue(); SetStatus("Added to your queue");
    }
    private void PublishQueue()
    {
        if (roomLibrary is not null) return;
        var titles = queue.Select(x => x.Title).ToArray(); QueueStatus.Text = "Up next: " + string.Join(" → ", titles);
        if (room?.Identity?.Host == true) room.Send(new("queue", Data: Wire.Serialize(titles)));
    }
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
            QueueStatus.Text = session.Queue.Entries.Length > 0 ? "Up next: " + string.Join(" → ", session.Queue.Entries.Select(e => e.Title)) : "Room queue empty · Open Host library / Guest views";
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
        libraryWindow.Show(); libraryWindow.Activate();
    }
    private void PublishRoomLibrary(string[] kinds)
    {
        if (roomLibrary is null || room?.Identity?.Host != true) return;
        sharedKinds = kinds; sharedItems.Clear();
        var catalog = new List<SharedLibraryItem>();
        foreach (var item in items.Where(i => kinds.Contains(i.Kind)).Take(RoomLibrary.MaxCatalog))
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
            selected = item; session.CompleteStart(request, true);
        }
        catch (Exception ex) { session.CompleteStart(request, false, ex.Message); }
    }
    private async void NextQueued(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (room is not null && roomLibrary is not null) { roomLibrary.Request(HostPeerId(), "next"); return; }
        if (room is not null && room.Identity?.Host != true) { SetStatus("Open the host library to request the next video."); return; }
        if (!queue.TryDequeue(out var next)) { SetStatus("Queue is empty. Add titles from the library."); return; }
        selected = next;
        if (room is null) PlayLocal(sender, e);
        else { room.SetHostedFile(next.Path, next.Title); room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); }
        PublishQueue(); await Task.CompletedTask;
    });
    private async void LeaveRoom(object sender, RoutedEventArgs e) => await Guard(async () => { await Disconnect(); player?.Stop(); RoomHeading.Text = "Your watch room"; RoomSubtitle.Text = "Room closed"; ShowPage("Library"); });
    private async Task Disconnect()
    {
        libraryWindow?.Shutdown(); libraryWindow = null; roomLibrary = null; sharedItems.Clear(); sharedIds.Clear(); sharedKinds = [];
        ++loadGeneration; var old = room; room = null; target = null; loadedMedia = null; ready = false;
        player?.Stop(); if (old is not null) await old.DisposeAsync();
        await loading.WaitAsync();
        try { if (bridge is not null) await bridge.DisposeAsync(); bridge = null; } finally { loading.Release(); }
        PeopleList.ItemsSource = null; chat.Clear(); SetThreadExecutionState(0x80000000); ConnectionStatus.Text = "Local playback";
    }
    private async void PrepareCopy(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null || room?.Identity?.Host == false) return;
        var exe = FfmpegBox.Text.Trim();
        if (!File.Exists(exe)) { ShowPage("Settings"); SetStatus("Choose an FFmpeg executable in Settings to prepare a smaller copy."); return; }
        var item = selected; var dir = Path.Combine(App.DataDirectory, "prepared"); Directory.CreateDirectory(dir);
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
        if (room?.Identity?.Host == true) { room.SetHostedFile(output, item.Title + " · 720p"); room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); }
        else { using var media = new Media(vlc!, output, FromType.FromPath); player?.Play(media); }
        SetStatus("720p copy ready.");
    });
    private void Fullscreen(object sender, RoutedEventArgs e)
    {
        fullscreen = !fullscreen;
        if (fullscreen) { previousState = WindowState; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; }
        else { WindowStyle = WindowStyle.SingleBorderWindow; WindowState = previousState; }
        Rail.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible; RailWidth.Width = new GridLength(fullscreen ? 0 : 190);
        RoomSide.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible; RoomSideWidth.Width = new GridLength(fullscreen ? 0 : 250);
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or PasswordBox || RoomPage.Visibility != Visibility.Visible) return;
        if (e.Key == Key.F || (e.Key == Key.Escape && fullscreen)) { Fullscreen(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(sender, e); e.Handled = true; }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        light = !light;
        var colors = light ? new[] { "#F4F5FA", "#FFFFFF", "#EAECF4", "#202433", "#586175", "#D6DAE7", "#6950C9", "#FFFFFF" } : new[] { "#101218", "#191C26", "#232736", "#F2F1F8", "#A5ABBE", "#303547", "#BEABFF", "#241A40" };
        var keys = new[] { "Canvas", "Surface", "Elevated", "Ink", "Muted", "Line", "Accent", "AccentInk" };
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return; e.Cancel = true; closing = true; lifetime.Cancel(); timer.Stop(); scanDelay.Stop();
        await Task.Yield(); // Let WPF finish the first Closing event before calling Close again.
        foreach (var watcher in watchers) watcher.Dispose();
        await Disconnect(); Video.MediaPlayer = null; player?.Dispose(); vlc?.Dispose(); Close();
    }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
