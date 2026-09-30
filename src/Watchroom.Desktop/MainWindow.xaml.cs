using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private MediaBridge? bridge;
    private PlaybackState? target;
    private string? loadedMedia;
    private bool scanning, seeking, tracksUpdating, ready, closing, fullscreen, light;
    private long tick, lastBufferNotice, appliedRevision = -1;
    private int loadGeneration;
    private long? pendingSeek;
    private long seekExpires, seekRevision;
    private WindowState previousState;
    private Rect previousBounds;
    private ResizeMode previousResizeMode;
    private bool roomPanelVisible;
    private long lastPlayerInteraction;
    private Point lastPlayerMouse;
    private string? browseSeries, browseKind;
    private int? browseSeason;
    private bool artworkBusy;

    public MainWindow()
    {
        InitializeComponent();
        // Keep the initial window inside the usable desktop on smaller displays.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        library = new LibraryStore(App.DataDirectory);
        light = library.Setting("theme") == "light";
        ApplyTheme();
        ShowPage("Library");
        foreach (var f in Wire.Read<List<LibraryFolder>>(library.Setting("folders") ?? "[]")) folders.Add(f);
        FolderList.ItemsSource = folders; ChatList.ItemsSource = chat;
        DisplayNameBox.Text = library.Setting("name") ?? Environment.UserName;
        ServerBox.Text = library.Setting("server") ?? "https://watchroom-rooms.svovoniks.chatgpt.site";
        FfmpegBox.Text = library.Setting("ffmpeg") ?? "";
        AutoArtwork.IsChecked = library.Setting("artwork") != "false";
        RefreshLibrary(); WatchFolders();
        timer.Tick += (_, _) => PlaybackTick(); timer.Start();
        scanDelay.Tick += async (_, _) => { scanDelay.Stop(); if (!scanning) await Guard(Scan); };
        Loaded += async (_, _) =>
        {
            await Guard(async () =>
            {
                await Task.Run(() => LibVLCSharp.Shared.Core.Initialize());
                vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--quiet");
                player = new(vlc) { Volume = 80, EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
                Video.MediaPlayer = player;
                player.Playing += (_, _) => Dispatcher.BeginInvoke(() => { ready = true; RefreshTracks(); try { room?.Send(new("ready")); } catch { } if (target?.Playing == false) player?.SetPause(true); });
                player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => SetStatus("Playback failed. Check that the file is available and the connection is active."));
                player.Buffering += (_, e) =>
                {
                    if (e.Cache >= 100) { try { room?.Send(new("ready")); } catch { } }
                    if (e.Cache < 10 && ready && room is not null && target?.Playing == true && Wire.Now - lastBufferNotice > 5000)
                    { lastBufferNotice = Wire.Now; try { room.Send(new("buffering")); } catch { } }
                };
                player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => { PlayButton.Content = "▶"; if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player.Length)); });
            });
            if (folders.Count == 0) ShowPage("Folders");
            else if (AutoArtwork.IsChecked == true) await FetchArtwork();
        };
    }
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        if (RoomPage.Visibility == Visibility.Visible) { ConnectionStatus.Text = text; ConnectionStatus.ToolTip = text; ShowPlayerControls(); }
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { SetStatus("Operation cancelled or timed out."); }
        catch (Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Watchroom", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void ShowPage(string name)
    {
        if (fullscreen && name != "Room") SetFullscreen(false);
        // VideoView hosts its content in a separate native overlay window. Collapsing
        // only RoomPage does not update that window's visibility or hit testing.
        // Hide the view itself before changing the shell layout, and restore both
        // the view and its overlay content when returning to the player.
        var playerVisibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
        PlayerOverlay.Visibility = playerVisibility;
        Video.Visibility = playerVisibility;
        LibraryPage.Visibility = name == "Library" ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = name == "Details" ? Visibility.Visible : Visibility.Collapsed;
        FoldersPage.Visibility = name == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        RoomPage.Visibility = name == "Room" ? Visibility.Visible : Visibility.Collapsed;
        ApplyPlayerLayout();
        if (name == "Library") SearchBox.Focus();
        foreach (var button in new[] { LibraryNav, RoomNav, FoldersNav, SettingsNav })
            System.Windows.Automation.AutomationProperties.SetItemStatus(button,
                (string)button.Tag == (name == "Details" ? "Library" : name) ? "Selected" : "");
    }
    private void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    private void RefreshLibrary()
    {
        items = library.All(); FilterLibrary();
        if (Details.Visibility == Visibility.Visible && selected is not null && items.FirstOrDefault(x => x.Id == selected.Id) is { } updated) Select(updated);
        SetStatus($"{items.Count(x => x.Available)} videos available · {folders.Count} folders");
    }
    private void FilterLibrary()
    {
        if (PosterGrid is null || Category is null || NoResults is null) return;
        var filter = Category.SelectedIndex;
        var query = SearchBox.Text.Trim();
        IEnumerable<MediaItem> visible = items.Where(x => x.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        visible = filter switch { 1 => visible.Where(x => x.Kind == "Movie"), 2 => visible.Where(x => x.Kind == "Show"), 3 => visible.Where(x => x.Kind == "Anime"), 4 => visible.Where(x => x.Poster is null), _ => visible };
        var cards = LibraryCatalog.Browse(visible, browseSeries, browseKind, browseSeason).ToList();
        PosterGrid.ItemsSource = cards;
        ResultCount.Text = $"{cards.Count} {(cards.Count == 1 ? "item" : "items")}";
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoResults.Visibility = items.Count > 0 && cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryBack.Visibility = browseSeries is null ? Visibility.Collapsed : Visibility.Visible;
        LibraryLocation.Text = browseSeries is null ? (query.Length > 0 ? "Search results" : ((ComboBoxItem)Category.SelectedItem).Content.ToString()) : browseSeries + (browseSeason is null ? " · Seasons" : browseSeason == 0 ? " · Specials" : $" · Season {browseSeason}");
        EmptyLibrary.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => FilterLibrary();
    private void ClearSearch(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void ResetFilters(object sender, RoutedEventArgs e)
    {
        browseSeries = browseKind = null; browseSeason = null;
        SearchBox.Clear(); Category.SelectedIndex = 0; FilterLibrary(); SearchBox.Focus();
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { browseSeries = browseKind = null; browseSeason = null; FilterLibrary(); }
    private void LibraryBackClick(object sender, RoutedEventArgs e) { if (browseSeason is not null) browseSeason = null; else browseSeries = browseKind = null; Details.Visibility = Visibility.Collapsed; FilterLibrary(); }
    private void SelectMovie(object sender, RoutedEventArgs e)
    {
        var card = (LibraryCard)((Button)sender).Tag;
        if (card.Level == "series") { browseSeries = card.Media.Series; browseKind = card.Media.Kind; browseSeason = null; Details.Visibility = Visibility.Collapsed; FilterLibrary(); }
        else if (card.Level == "season") { browseSeason = card.Season; Details.Visibility = Visibility.Collapsed; FilterLibrary(); }
        else Select(card.Media);
    }
    private void Select(MediaItem item)
    {
        selected = item; ShowPage("Details"); DetailTitle.Text = item.Episode is { } ep ? $"Episode {ep}" : item.Title;
        DetailContext.Text = item.Series is null ? "MOVIE" : item.Series;
        DetailMeta.Text = item.Caption + " · " + Path.GetExtension(item.Path).TrimStart('.').ToUpperInvariant();
        var poster = new[] { item.Poster, item.SeasonPoster, item.SeriesPoster }.FirstOrDefault(File.Exists);
        DetailPoster.Source = poster is null ? null : new BitmapImage(new Uri(Path.GetFullPath(poster)));
        DetailFile.Text = Path.GetFileName(item.Path);
        DetailSummary.Text = (item.Available ? "" : "File unavailable — reconnect its drive. ") + (string.IsNullOrWhiteSpace(item.Overview) ? "No overview available yet. Use artwork and matching to add metadata." : item.Overview) + (item.PosterSource is null ? "" : "\n\nArtwork: " + item.PosterSource);
        Episodes.Visibility = Visibility.Collapsed;
    }
    private void DetailBack(object sender, RoutedEventArgs e) => ShowPage("Library");
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
        ScanButton.IsEnabled = RescanButton.IsEnabled = false;
        RescanButton.Content = "Scanning…";
        ScanProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<string>(text => { ScanStatus.Text = text; SetStatus(text); });
            await library.ScanAsync(folders.ToArray(), progress, lifetime.Token); RefreshLibrary();
            ScanStatus.Text = $"Scan complete · {items.Count} videos indexed.";
            if (AutoArtwork.IsChecked == true) await FetchArtwork();
        }
        finally
        {
            scanning = false;
            ScanButton.IsEnabled = RescanButton.IsEnabled = true;
            RescanButton.Content = "↻  Rescan";
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }
    private async void FetchArtworkClick(object sender, RoutedEventArgs e) => await Guard(FetchArtwork);
    private async Task FetchArtwork()
    {
        if (artworkBusy) return; artworkBusy = true;
        try { var report = await AutomaticArtwork.FetchAsync(library, App.DataDirectory, MetadataToken.Password, new Progress<string>(SetStatus), lifetime.Token); RefreshLibrary(); SetStatus(report); }
        finally { artworkBusy = false; }
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
        ready = false; RoomHeading.Text = selected.Title; RoomSubtitle.Text = "Local playback";
        await PrepareVideoSurface(); player.Play(media);
    });
    private async void HostClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null) return;
        if (!File.Exists(selected.Path)) throw new FileNotFoundException("Movie file is unavailable.");
        await Disconnect(); var client = NewRoom();
        client.SetHostedFile(selected.Path, selected.Title);
        SetStatus("Connecting to the room service…");
        try { await client.ConnectAsync(ServerBox.Text.Trim(), DisplayNameBox.Text); }
        catch (System.Net.Http.HttpRequestException ex)
        {
            await Disconnect();
            throw new IOException($"Cannot reach the room service at {ServerBox.Text.Trim()}. Check the server address in Settings. Localhost requires a server running on this computer.", ex);
        }
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
                QueueStatus.Text = (snapshot.Queue?.Length > 0) ? "Up next: " + string.Join(" → ", snapshot.Queue) : "Queue is empty · Suggest a title in chat";
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
            RoomHeading.Text = media.Title; RoomSubtitle.Text = "Private room · " + (client.Identity?.Host == true ? "You are hosting" : "Streaming from host");
            await PrepareVideoSurface();
            if (client != room || generation != loadGeneration) return;
            player!.Play(vlcMedia);
        }
        catch { if (generation == loadGeneration) loadedMedia = null; throw; }
        finally { loading.Release(); }
    }
    private async Task PrepareVideoSurface()
    {
        PlayerEmpty.Visibility = Visibility.Collapsed;
        roomPanelVisible = room is not null;
        ShowPage("Room");
        await Dispatcher.InvokeAsync(() => Video.UpdateLayout(), DispatcherPriority.Render);
        PlayerOverlay.Focus();
    }
    private void PlaybackTick()
    {
        if (fullscreen && PlaybackControls.Visibility == Visibility.Visible &&
            Environment.TickCount64 - lastPlayerInteraction > 2600 && !PlaybackControls.IsMouseOver &&
            !seeking && PlayerOptions.Visibility != Visibility.Visible)
        {
            if (PlaybackControls.IsKeyboardFocusWithin) PlayerOverlay.Focus();
            PlaybackControls.Visibility = FullscreenHint.Visibility = Visibility.Collapsed;
            PlayerOverlay.Cursor = Cursors.None;
        }
        if (player is null) return;
        MuteButton.Content = player.Mute ? "\uE74F" : "\uE767";
        MuteButton.ToolTip = player.Mute ? "Unmute (M)" : "Mute (M)";
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, player.Mute ? "Unmute" : "Mute");
        VolumeText.Text = player.Mute ? "Muted" : $"{(int)Volume.Value}%";
        if (pendingSeek is { } requested && (Wire.Now >= seekExpires || (room is null || target?.Revision > seekRevision) && Math.Abs(player.Time - requested) < 1500)) pendingSeek = null;
        if (!seeking)
        {
            Timeline.Maximum = Math.Max(1, player.Length); Timeline.Value = pendingSeek ?? Math.Max(0, player.Time);
            PlaybackTime.Text = FormatTime(player.Time) + " / " + FormatTime(player.Length);
        }
        PlayButton.Content = player.IsPlaying ? "Ⅱ" : "▶";
        PlayButton.ToolTip = player.IsPlaying ? "Pause (Space)" : "Play (Space)";
        if (++tick % 8 == 0 && ready) RefreshTracks();
        if (room is null || target is null || !ready || seeking || pendingSeek is not null && target.Revision <= seekRevision) return;
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
        else if (player.IsPlaying) player.SetPause(true); else { if (player.State == VLCState.Ended) player.Stop(); player.Play(); }
    }
    private bool CanSeek() => player is not null && player.Length > 0 && (room is null || room.Identity?.Host == true || room.Snapshot?.SharedControls == true);
    private void SeekStarted(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!CanSeek()) { SetStatus("The host controls playback, or the video is not ready to seek."); return; }
        seeking = true; Timeline.CaptureMouse(); UpdateScrub(e.GetPosition(Timeline).X);
    }
    private void UpdateScrub(double x)
    {
        // Account for the thumb inset so both ends remain reachable.
        var fraction = Math.Clamp((x - 8) / Math.Max(1, Timeline.ActualWidth - 16), 0, 1);
        Timeline.Value = fraction * Timeline.Maximum;
        PlaybackTime.Text = FormatTime((long)Timeline.Value) + " / " + FormatTime(player?.Length ?? 0);
    }
    private void SeekMoved(object sender, MouseEventArgs e) { if (seeking && Timeline.IsMouseCaptured) { UpdateScrub(e.GetPosition(Timeline).X); e.Handled = true; } }
    private void CommitSeek()
    {
        if (!seeking) return;
        seeking = false;
        if (CanSeek())
        {
            var position = (long)Timeline.Value;
            pendingSeek = position; seekRevision = target?.Revision ?? -1; seekExpires = Wire.Now + 5000;
            if (room is null) player!.Time = position; else SendPlayback(target?.Playing == true, position);
        }
        if (Timeline.IsMouseCaptured) Timeline.ReleaseMouseCapture();
    }
    private void SeekFinished(object sender, MouseButtonEventArgs e) { if (seeking) { UpdateScrub(e.GetPosition(Timeline).X); CommitSeek(); e.Handled = true; } }
    private void SeekCaptureLost(object sender, MouseEventArgs e) => CommitSeek();
    private void SeekKeyStarted(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        e.Handled = true; if (!CanSeek()) return; seeking = true;
        Timeline.Value = e.Key switch { Key.Home => 0, Key.End => Timeline.Maximum, Key.Left => Math.Max(0, Timeline.Value - 5000), _ => Math.Min(Timeline.Maximum, Timeline.Value + 5000) };
    }
    private void SeekKeyFinished(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) { CommitSeek(); e.Handled = true; } }
    private void MovePlayback(long offset) { if (player is null || player.Length <= 0) return; var position = Math.Clamp(player.Time + offset, 0, player.Length); if (room is null) player.Time = position; else SendPlayback(target?.Playing == true, position); }
    private void SkipBackward(object sender, RoutedEventArgs e) => MovePlayback(-10000);
    private void SkipForward(object sender, RoutedEventArgs e) => MovePlayback(10000);
    private void StopPlayback(object sender, RoutedEventArgs e) { if (player is null) return; if (room is null) { player.Stop(); ready = false; } else SendPlayback(false, 0); }
    private void MutePlayback(object sender, RoutedEventArgs e) { if (player is not null) player.Mute = !player.Mute; }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (player is not null) { player.Volume = (int)e.NewValue; if (e.NewValue > 0) player.Mute = false; } }
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
    private void RemovePeer(object sender, RoutedEventArgs e) => room?.Send(new("remove", Target: (string)((Button)sender).Tag));
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
        if (room?.Identity?.Host == false) { room.Send(new("chat", Text: "Suggestion: " + selected.Title)); SetStatus("Suggested title to the room"); return; }
        if (queue.Count >= 50) { SetStatus("The queue can hold up to 50 titles."); return; }
        queue.Enqueue(selected); PublishQueue(); SetStatus("Added to your queue");
    }
    private void PublishQueue()
    {
        var titles = queue.Select(x => x.Title).ToArray(); QueueStatus.Text = "Up next: " + string.Join(" → ", titles);
        if (room?.Identity?.Host == true) room.Send(new("queue", Data: Wire.Serialize(titles)));
    }
    private async void NextQueued(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (room is not null && room.Identity?.Host != true) { SetStatus("The host controls the queue."); return; }
        if (!queue.TryDequeue(out var next)) { SetStatus("Queue is empty. Add titles from the library."); return; }
        selected = next;
        if (room is null) PlayLocal(sender, e);
        else { room.SetHostedFile(next.Path, next.Title); room.Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); }
        PublishQueue(); await Task.CompletedTask;
    });
    private async void LeaveRoom(object sender, RoutedEventArgs e) => await Guard(async () => { await Disconnect(); player?.Stop(); RoomHeading.Text = "Your watch room"; RoomSubtitle.Text = "Room closed"; ShowPage("Library"); });
    private async Task Disconnect()
    {
        if (fullscreen) SetFullscreen(false);
        PlayerEmpty.Visibility = Visibility.Visible;
        pendingSeek = null; seeking = false; ++loadGeneration; var old = room; room = null; target = null; loadedMedia = null; ready = false;
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
        else { await PrepareVideoSurface(); using var media = new Media(vlc!, output, FromType.FromPath); player?.Play(media); }
        SetStatus("720p copy ready.");
    });
    private void Fullscreen(object sender, RoutedEventArgs e)
    {
        SetFullscreen(!fullscreen);
    }
    private void ApplyPlayerLayout()
    {
        bool inPlayer = RoomPage.Visibility == Visibility.Visible;
        Rail.Visibility = inPlayer ? Visibility.Collapsed : Visibility.Visible;
        RailWidth.Width = new GridLength(inPlayer ? 0 : 208);
        ContentArea.Margin = inPlayer ? new Thickness(0) : new Thickness(30, 24, 30, 18);
        AppStatusBar.Visibility = inPlayer ? Visibility.Collapsed : Visibility.Visible;
        StatusRow.Height = new GridLength(inPlayer ? 0 : 34);
        RoomHeader.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        RoomSide.Visibility = roomPanelVisible && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        RoomSideWidth.Width = new GridLength(roomPanelVisible && !fullscreen ? 280 : 0);
        RoomToggle.ToolTip = roomPanelVisible ? "Hide participants and chat" : "Show participants and chat";
    }
    private void SetFullscreen(bool value)
    {
        if (fullscreen == value || RoomPage.Visibility != Visibility.Visible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (value)
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor)) return;
            previousState = WindowState;
            previousBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            previousResizeMode = ResizeMode;
            fullscreen = true;
            // Use the entire monitor, including the taskbar area. Maximizing uses the work area.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            DockedControlsHost.Children.Remove(PlaybackControls);
            FullscreenControlsHost.Children.Add(PlaybackControls);
            ApplyPlayerLayout();
            SetWindowPos(handle, IntPtr.Zero, monitor.Monitor.Left, monitor.Monitor.Top,
                monitor.Monitor.Right - monitor.Monitor.Left, monitor.Monitor.Bottom - monitor.Monitor.Top, 0x0020 | 0x0040);
        }
        else
        {
            fullscreen = false;
            FullscreenControlsHost.Children.Remove(PlaybackControls);
            DockedControlsHost.Children.Add(PlaybackControls);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = previousResizeMode;
            Left = previousBounds.Left; Top = previousBounds.Top;
            Width = previousBounds.Width; Height = previousBounds.Height;
            WindowState = previousState;
            ApplyPlayerLayout();
        }
        PlayerOptions.Visibility = Visibility.Collapsed;
        FullscreenButton.Content = fullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = fullscreen ? "Exit fullscreen (Esc)" : "Fullscreen (F or double-click video)";
        System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, fullscreen ? "Exit fullscreen" : "Fullscreen");
        ShowPlayerControls();
        PlayerOverlay.Focus();
    }
    private void ToggleRoomPanel(object sender, RoutedEventArgs e) { roomPanelVisible = !roomPanelVisible; ApplyPlayerLayout(); }
    private void TogglePlayerOptions(object sender, RoutedEventArgs e)
    {
        PlayerOptions.Visibility = PlayerOptions.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ShowPlayerControls();
    }
    private void ShowPlayerControls()
    {
        lastPlayerInteraction = Environment.TickCount64;
        PlaybackControls.Visibility = Visibility.Visible;
        FullscreenHint.Visibility = fullscreen ? Visibility.Visible : Visibility.Collapsed;
        PlayerOverlay.Cursor = Cursors.Arrow;
    }
    private void PlayerMouseMoved(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(PlayerOverlay);
        if ((position - lastPlayerMouse).Length < 2) return;
        lastPlayerMouse = position;
        if (fullscreen) ShowPlayerControls();
    }
    private void ControlsMouseMoved(object sender, MouseEventArgs e) { if (fullscreen) ShowPlayerControls(); }
    private void ControlsKeyPressed(object sender, KeyEventArgs e) { if (fullscreen) ShowPlayerControls(); }
    private void VideoClicked(object sender, MouseButtonEventArgs e)
    {
        // Controls and menus inside the native-video overlay keep their own clicks.
        if (e.OriginalSource != PlayerOverlay) return;
        PlayerOverlay.Focus();
        if (e.ClickCount == 2) { SetFullscreen(!fullscreen); e.Handled = true; }
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape && fullscreen) { SetFullscreen(false); e.Handled = true; return; }
        if (fullscreen && e.Key == Key.Tab) ShowPlayerControls();
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !fullscreen)
        {
            ShowPage("Library"); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape && LibraryPage.Visibility == Visibility.Visible && SearchBox.IsKeyboardFocused)
        { SearchBox.Clear(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox || RoomPage.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Space && Keyboard.FocusedElement is Button) return;
        if (e.Key is Key.F or Key.F11) { Fullscreen(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space) { TogglePlayback(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not Slider && e.Key is Key.Left or Key.Right) { MovePlayback(e.Key == Key.Left ? -10000 : 10000); e.Handled = true; }
        else if (e.Key == Key.M) { MutePlayback(sender, e); e.Handled = true; }
        else if (e.Key == Key.S) { StopPlayback(sender, e); e.Handled = true; }
        else if (e.Key == Key.N) { NextQueued(sender, e); e.Handled = true; }
        else if (Keyboard.FocusedElement is not Slider && e.Key is Key.Up or Key.Down) { Volume.Value = Math.Clamp(Volume.Value + (e.Key == Key.Up ? 5 : -5), 0, 100); e.Handled = true; }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        light = !light;
        library.Setting("theme", light ? "light" : "dark");
        ApplyTheme();
    }
    private void ApplyTheme()
    {
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
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
