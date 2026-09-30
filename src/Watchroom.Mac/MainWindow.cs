using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Watchroom.Core;
using VideoView = LibVLCSharp.Avalonia.VideoView;

namespace Watchroom.Mac;

public sealed class MainWindow : Window
{
    private readonly LibraryStore library = new(App.DataDirectory);
    private readonly ObservableCollection<LibraryFolder> folders = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly List<Bitmap> posters = [];
    private readonly Queue<MediaItem> queue = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim loading = new(1);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer scanDelay = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TabControl tabs = new();
    private readonly WrapPanel posterGrid = new() { ItemWidth = 165, ItemHeight = 280 };
    private readonly TextBox search = new() { Watermark = "Search your library", Width = 320 };
    private readonly ComboBox category = new() { ItemsSource = new[] { "All", "Movies", "Shows", "Anime", "Needs artwork" }, SelectedIndex = 0, Width = 170 };
    private readonly TextBlock status = Text("Ready", 14);
    private readonly TextBlock title = Text("Pick a movie", 24);
    private readonly TextBlock summary = Text("Add your movie folders to get started.", 16);
    private readonly ComboBox episodes = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly ListBox folderList = new() { MinHeight = 160 };
    private readonly ComboBox folderKind = new() { ItemsSource = new[] { "Mixed", "Movie", "Show", "Anime" }, SelectedIndex = 0, Width = 160 };
    private readonly TextBox server = new() { Watermark = "HTTPS room server" };
    private readonly TextBox name = new() { Watermark = "Your display name" };
    private readonly TextBox token = new() { PasswordChar = '●', Watermark = "TMDB API read access token (this session only)" };
    private readonly TextBox invitation = new() { Watermark = "Invitation link or room code" };
    private readonly TextBox chatInput = new() { Watermark = "Message your room", MaxLength = 1000 };
    private readonly StackPanel people = new() { Spacing = 6 };
    private readonly StackPanel chat = new() { Spacing = 4 };
    private readonly TextBlock queueText = Text("Queue is empty", 14);
    private readonly TextBlock roomTitle = Text("Watch room", 22);
    private readonly TextBlock roomHint = Text("Choose a movie to host, or join an invitation.", 14);
    private readonly CheckBox shared = new() { Content = "Allow shared controls", IsEnabled = false };
    private readonly VideoView video = new() { MinHeight = 220 };
    private readonly Slider timeline = new() { Minimum = 0, Maximum = 1 };
    private readonly Slider volume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 100 };
    private readonly TextBlock time = Text("00:00 / 00:00", 14);
    private readonly ComboBox audio = new() { Width = 170 };
    private readonly ComboBox subtitles = new() { Width = 170 };
    private readonly Button play = new() { Content = "Play", MinWidth = 70 };
    private List<MediaItem> items = [];
    private MediaItem? selected;
    private LibVLC? vlc;
    private MediaPlayer? player;
    private RoomClient? room;
    private MediaBridge? bridge;
    private PlaybackState? target;
    private string? loadedMedia;
    private long revision = -1, tick, lastBufferNotice;
    private int generation;
    private bool ready, seeking, refreshingTracks, refreshingRoom, scanning, closing, allowClose;

    public MainWindow()
    {
        Title = "Watchroom"; Width = 1220; Height = 820; MinWidth = 850; MinHeight = 620;
        Background = new SolidColorBrush(Color.Parse("#101722"));
        foreach (var folder in Wire.Read<List<LibraryFolder>>(library.Setting("folders") ?? "[]")) folders.Add(folder);
        folderList.ItemsSource = folders;
        server.Text = library.Setting("server") ?? "http://localhost:5080";
        name.Text = library.Setting("name") ?? Environment.UserName;
        BuildUI(); RefreshLibrary(); WatchFolders();
        search.TextChanged += (_, _) => FilterLibrary(); category.SelectionChanged += (_, _) => FilterLibrary();
        episodes.SelectionChanged += (_, _) => { if (episodes.SelectedItem is MediaItem item) { selected = item; summary.Text = item.Caption; } };
        timer.Tick += (_, _) => Tick(); timer.Start();
        scanDelay.Tick += async (_, _) => { scanDelay.Stop(); if (!scanning) await Guard(Scan); };
        Opened += async (_, _) =>
        {
            if (folders.Count == 0) tabs.SelectedIndex = 2;
            await Guard(async () =>
            {
                await Task.Run(NativeMedia.Initialize);
                vlc = new LibVLC("--no-video-title-show", "--quiet");
                player = new MediaPlayer(vlc) { Volume = 80, EnableHardwareDecoding = true };
                video.MediaPlayer = player;
                player.Playing += (_, _) => Dispatcher.UIThread.Post(() => { ready = true; RefreshTracks(); SendReady(); if (target?.Playing == false) player.SetPause(true); });
                player.Buffering += (_, e) => Dispatcher.UIThread.Post(() =>
                {
                    if (e.Cache >= 100 && !ready) { ready = true; SendReady(); }
                    if (e.Cache < 10 && ready && room is not null && target?.Playing == true && Wire.Now - lastBufferNotice > 5000)
                    { lastBufferNotice = Wire.Now; ready = false; Send(new("buffering")); }
                });
                player.EncounteredError += (_, _) => Dispatcher.UIThread.Post(() => { status.Text = "Playback failed. Check the file, VLC installation, and connection."; });
                player.EndReached += (_, _) => Dispatcher.UIThread.Post(async () =>
                {
                    if (room?.Identity?.Host == true) SendPlayback(false, Math.Max(0, player.Length));
                    if ((room is null || room.Identity?.Host == true) && queue.TryDequeue(out var next))
                    { selected = next; await Guard(room is null ? PlayLocal : Host); }
                });
            });
        };
        Closing += async (_, e) =>
        {
            if (allowClose) return; e.Cancel = true; if (closing) return; closing = true;
            await Task.Yield(); timer.Stop(); scanDelay.Stop(); lifetime.Cancel();
            foreach (var watcher in watchers) watcher.Dispose();
            try { await Disconnect(); } catch { }
            video.MediaPlayer = null; player?.Dispose(); vlc?.Dispose();
            foreach (var bitmap in posters) bitmap.Dispose(); allowClose = true; Close();
        };
        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox) return;
            if (e.Key == Key.Space && tabs.SelectedIndex == 1) { TogglePlay(); e.Handled = true; }
            if (e.Key == Key.F && tabs.SelectedIndex == 1) { Fullscreen(); e.Handled = true; }
            if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) WindowState = WindowState.Normal;
        };
    }
    private static TextBlock Text(string text, double size = 16) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Column(params Control[] controls) { var stack = new StackPanel { Spacing = 12 }; foreach (var c in controls) stack.Children.Add(c); return stack; }
    private static StackPanel Row(params Control[] controls) { var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; foreach (var c in controls) stack.Children.Add(c); return stack; }
    private static WrapPanel Flow(params Control[] controls) { var panel = new WrapPanel(); foreach (var c in controls) { c.Margin = new Thickness(0, 0, 10, 6); panel.Children.Add(c); } return panel; }
    private Button Button(string text, Func<Task> action) { var b = new Button { Content = text }; b.Click += async (_, _) => await Guard(action); return b; }
    private Button Button(string text, Action action) => Button(text, () => { action(); return Task.CompletedTask; });
    private static Control Pane(Control content) => new Border { Padding = new Thickness(20), Child = content };
    private void BuildUI()
    {
        var root = new DockPanel();
        var header = new Border { Padding = new Thickness(24, 18), Child = Text("WATCHROOM", 26) }; DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new Border { Padding = new Thickness(18, 10), Child = status }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(tabs); Content = root;
        var libraryPage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,280"), RowDefinitions = new RowDefinitions("Auto,*") };
        var tools = Row(search, category); tools.Margin = new Thickness(0, 0, 0, 18); Grid.SetColumnSpan(tools, 2); libraryPage.Children.Add(tools);
        var gallery = new ScrollViewer { Content = posterGrid }; Grid.SetRow(gallery, 1); libraryPage.Children.Add(gallery);
        var details = Column(title, summary, episodes, Button("Play locally", PlayLocal), Button("Watch together", Host), Button("Add to queue", AddQueue), Button("Find artwork", MatchArtwork), Button("Use local poster", LocalPoster));
        details.Margin = new Thickness(18, 0, 0, 0); Grid.SetColumn(details, 1); Grid.SetRow(details, 1); libraryPage.Children.Add(details);
        var roomPage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300") };
        var playback = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto") };
        playback.Children.Add(Column(roomTitle, roomHint)); Grid.SetRow(video, 1); playback.Children.Add(video);
        Grid.SetRow(timeline, 2); playback.Children.Add(timeline);
        play.Click += (_, _) => TogglePlay(); volume.ValueChanged += (_, _) => { if (player is not null) player.Volume = (int)volume.Value; };
        timeline.AddHandler(PointerPressedEvent, (_, _) => seeking = true, RoutingStrategies.Tunnel, true);
        timeline.AddHandler(PointerReleasedEvent, (_, _) => Seek(), RoutingStrategies.Tunnel, true);
        timeline.KeyUp += (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) { seeking = true; Seek(); } };
        var controls = Flow(play, time, Text("Volume", 14), volume, Button("Full screen", Fullscreen)); Grid.SetRow(controls, 3); playback.Children.Add(controls);
        var tracks = Flow(Text("Audio", 14), audio, Text("Subtitles", 14), subtitles, Button("Load subtitle", LoadSubtitle)); tracks.Margin = new Thickness(0, 10, 0, 0); Grid.SetRow(tracks, 4); playback.Children.Add(tracks);
        audio.SelectionChanged += (_, _) => { if (!refreshingTracks && audio.SelectedItem is TrackOption t) player?.SetAudioTrack(t.Id); };
        subtitles.SelectionChanged += (_, _) => { if (!refreshingTracks && subtitles.SelectedItem is TrackOption t) player?.SetSpu(t.Id); };
        roomPage.Children.Add(playback);
        shared.IsCheckedChanged += (_, _) => { if (!refreshingRoom && room?.Identity?.Host == true) Send(new("controls", Number: shared.IsChecked == true ? 1 : 0)); };
        var sidebar = Column(Button("Copy invitation", CopyInvitation), Button("Leave room", Disconnect), shared, Text("People", 18), people, Text("Up next", 18), queueText, Button("Play next", PlayNext), Text("Chat", 18), new ScrollViewer { Content = chat, Height = 150 }, chatInput, Button("Send", SendChat));
        chatInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) SendChat(); };
        var side = new ScrollViewer { Content = sidebar, Margin = new Thickness(18, 0, 0, 0) }; Grid.SetColumn(side, 1); roomPage.Children.Add(side);
        tabs.ItemsSource = new[] {
            new TabItem { Header = "Library", Content = Pane(libraryPage) },
            new TabItem { Header = "Watch room", Content = Pane(roomPage) },
            new TabItem { Header = "Folders", Content = Pane(Column(Text("Your movie folders",24),Text("Add the folders Watchroom should scan. You can skip this when joining a friend."), Row(folderKind, Button("Add folders", AddFolders)), folderList, Row(Button("Remove selected", RemoveFolder),Button("Scan library", Scan)))) },
            new TabItem { Header = "Settings", Content = Pane(new ScrollViewer { Content = Column(Text("Room connection",24),Text("Display name",14),name,Text("Server address",14),server,Button("Save settings",SaveSettings),Text("Join a friend",22),invitation,Button("Join room",Join),Text("Poster artwork",22),token,Text("Use a TMDB read access token to match posters, or choose local artwork from the library. Token stays in this app session.",14),Text("This product uses the TMDB API but is not endorsed or certified by TMDB.",14),Text("Playback requirements",22),Text("On Mac, install VLC in /Applications. WebRTC requires a matching libdatachannel library; Homebrew can install it with brew install libdatachannel. The private Sites deployment requires access enabled for native clients.",14)) }) }
        };
    }
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { status.Text = "Operation cancelled or timed out."; }
        catch (Exception ex) { status.Text = ex.Message; try { await File.AppendAllTextAsync(Path.Combine(App.DataDirectory, "errors.log"), DateTimeOffset.UtcNow + " " + ex + Environment.NewLine); } catch { } }
    }
    private void RefreshLibrary() { items = library.All(); FilterLibrary(); status.Text = $"{items.Count(x => x.Available)} videos · {folders.Count} folders"; }
    private void FilterLibrary()
    {
        posterGrid.Children.Clear(); foreach (var bitmap in posters) bitmap.Dispose(); posters.Clear();
        var visible = items.Where(x => x.DisplayTitle.Contains(search.Text?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));
        visible = category.SelectedIndex switch { 1 => visible.Where(x => x.Kind == "Movie"), 2 => visible.Where(x => x.Kind == "Show"), 3 => visible.Where(x => x.Kind == "Anime"), 4 => visible.Where(x => x.Poster is null), _ => visible };
        foreach (var item in visible.GroupBy(x => x.Series is null ? x.Id : x.Kind + ":" + x.Series).Select(g => g.First()))
        {
            Control image = new Border { Background = new SolidColorBrush(Color.Parse("#263d52")), Height = 200, Child = new TextBlock { Text = item.DisplayTitle, FontSize = 20, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center } };
            if (item.Poster is not null && File.Exists(item.Poster)) try { var bitmap = new Bitmap(item.Poster); posters.Add(bitmap); image = new Image { Source = bitmap, Height = 200, Stretch = Stretch.UniformToFill }; } catch (Exception ex) when (ex is IOException or ArgumentException) { }
            var button = Button("", () => Select(item)); button.Padding = new Thickness(6); button.Margin = new Thickness(3); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Content = Column(image, Text(item.DisplayTitle, 14), Text(item.Available ? item.Kind : "Unavailable", 12)); posterGrid.Children.Add(button);
        }
        if (posterGrid.Children.Count == 0) posterGrid.Children.Add(Text(items.Count == 0 ? "Add folders and scan to see your movies here." : "No matching titles."));
    }
    private void Select(MediaItem item)
    {
        selected = item; title.Text = item.DisplayTitle; summary.Text = item.Overview ?? item.Caption;
        episodes.IsVisible = item.Series is not null;
        episodes.ItemsSource = item.Series is null ? null : items.Where(x => x.Series == item.Series && x.Kind == item.Kind).ToArray();
        episodes.SelectedItem = item;
    }
    private async Task AddFolders()
    {
        var chosen = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose movie folders", AllowMultiple = true });
        foreach (var folder in chosen) { var path = folder.TryGetLocalPath(); if (path is not null && !folders.Any(x => x.Path.Equals(path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) folders.Add(new(path, folderKind.SelectedItem?.ToString() ?? "Mixed")); }
        SaveFolders();
    }
    private void RemoveFolder() { if (folderList.SelectedItem is LibraryFolder folder) folders.Remove(folder); SaveFolders(); }
    private void SaveFolders() { library.Setting("folders", Wire.Serialize(folders)); WatchFolders(); }
    private void WatchFolders()
    {
        foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
        foreach (var folder in folders.Where(f => Directory.Exists(f.Path))) try
        {
            var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = true, EnableRaisingEvents = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            void Changed(object? s, FileSystemEventArgs e) => Dispatcher.UIThread.Post(() => { scanDelay.Stop(); scanDelay.Start(); });
            watcher.Created += Changed; watcher.Deleted += Changed; watcher.Renamed += (_, e) => Changed(null, e); watchers.Add(watcher);
        } catch (IOException) { }
    }
    private async Task Scan()
    {
        if (scanning) return; scanning = true;
        try { await library.ScanAsync(folders.ToArray(), new Progress<string>(s => status.Text = s), lifetime.Token); RefreshLibrary(); tabs.SelectedIndex = 0; }
        finally { scanning = false; }
    }
    private void SaveSettings() { library.Setting("server", server.Text?.Trim() ?? ""); library.Setting("name", name.Text?.Trim() ?? ""); status.Text = "Settings saved"; }
    private async Task MatchArtwork()
    {
        if (selected is null) return; if (string.IsNullOrWhiteSpace(token.Text)) { tabs.SelectedIndex = 3; status.Text = "Enter your TMDB read access token, or use a local poster."; return; }
        using var metadata = new MetadataClient(token.Text); var item = selected;
        var matches = await metadata.SearchAsync(item.Series ?? item.Title, item.Series is not null, lifetime.Token);
        if (matches.Count == 0) { status.Text = "No metadata matches. Try a local poster."; return; }
        var picker = new Window { Title = "Choose the matching title", Width = 520, Height = 420 };
        var list = new ListBox { ItemsSource = matches, SelectedIndex = 0, Height = 280 };
        MetadataMatch? match = null; var use = new Button { Content = "Use selected match" }; use.Click += (_, _) => { match = list.SelectedItem as MetadataMatch; picker.Close(); };
        picker.Content = Pane(Column(list, use)); await picker.ShowDialog(this); if (match is null) return;
        var poster = await metadata.CachePosterAsync(match, Path.Combine(App.DataDirectory, "posters"), lifetime.Token);
        foreach (var episode in items.Where(x => x.Id == item.Id || item.Series is not null && x.Series == item.Series && x.Kind == item.Kind)) library.Save(episode with { Poster = poster, Overview = match.Overview, Matched = true });
        RefreshLibrary();
    }
    private async Task LocalPoster()
    {
        if (selected is null) return; var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose poster", FileTypeFilter = new[] { new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp" } } } });
        var path = chosen.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        foreach (var item in items.Where(x => x.Id == selected.Id || selected.Series is not null && x.Series == selected.Series && x.Kind == selected.Kind)) library.Save(item with { Poster = path, Matched = true }); RefreshLibrary();
    }
    private void NeedPlayer() { if (player is null) throw new InvalidOperationException("Playback is unavailable. Check the VLC installation, then restart Watchroom."); }
    private async Task PlayLocal()
    {
        if (selected is null) { status.Text = "Select a movie first."; return; } NeedPlayer(); await Disconnect();
        if (!File.Exists(selected.Path)) throw new FileNotFoundException("Video is unavailable. Reconnect its drive and rescan.");
        roomTitle.Text = selected.Title; roomHint.Text = "Local playback"; tabs.SelectedIndex = 1;
        await Dispatcher.UIThread.InvokeAsync(() => video.UpdateLayout(), DispatcherPriority.Render);
        using var media = new Media(vlc!, selected.Path, FromType.FromPath); ready = false; player!.Play(media);
    }
    private async Task Host()
    {
        if (selected is null) { status.Text = "Select a movie first."; return; } NeedPlayer();
        if (room?.Identity?.Host == true)
        {
            room.SetHostedFile(selected.Path, selected.Title);
            Send(new("media", Data: Wire.Serialize(room.HostedMedia!.Media))); PublishQueue(); return;
        }
        using (var check = new PeerTransport([])) { }
        await Disconnect(); var client = NewRoom(); client.SetHostedFile(selected.Path, selected.Title);
        try { await client.ConnectAsync(server.Text ?? "", name.Text ?? "Host"); tabs.SelectedIndex = 1; roomHint.Text = "Creating room…"; }
        catch { await Disconnect(); throw; }
    }
    private async Task Join()
    {
        NeedPlayer(); using (var check = new PeerTransport([])) { }
        var link = invitation.Text?.Trim(); if (string.IsNullOrEmpty(link)) { status.Text = "Paste an invitation first."; return; }
        var endpoint = server.Text ?? ""; var code = link; var separator = link.LastIndexOf('#'); if (separator >= 0) { endpoint = link[..separator]; code = link[(separator + 1)..]; }
        await Disconnect(); var client = NewRoom();
        try { await client.ConnectAsync(endpoint, name.Text ?? "Guest", code); roomHint.Text = "Waiting for host approval"; tabs.SelectedIndex = 1; }
        catch { await Disconnect(); throw; }
    }
    private RoomClient NewRoom()
    {
        var client = new RoomClient(); room = client;
        client.Message += message => Dispatcher.UIThread.Post(async () => { if (client == room) await Guard(() => OnMessage(message)); });
        client.Status += text => Dispatcher.UIThread.Post(() => { if (room != client) return; status.Text = text; if (text.StartsWith("Disconnected", StringComparison.Ordinal)) { player?.SetPause(true); target = null; } }); return client;
    }
    private async Task OnMessage(WireMessage message)
    {
        if (room is null) return;
        switch (message.Type)
        {
            case "welcome": if (room.Identity!.Host && room.HostedMedia is not null) { Send(new("media", Data: Wire.Serialize(room.HostedMedia.Media))); PublishQueue(); } break;
            case "admitted": roomHint.Text = "Admitted · Connecting to host"; break;
            case "snapshot":
                var snapshot = Wire.Read<RoomSnapshot>(message.Data!); people.Children.Clear();
                foreach (var participant in snapshot.People)
                {
                    var person = Column(Text(participant.Name + (participant.IsHost ? " · Host" : ""), 14), Text(!participant.Approved ? "Waiting for approval" : participant.Ready ? "Ready" : "Buffering", 12));
                    if (room.Identity?.Host == true && !participant.IsHost) person.Children.Add(Button(participant.Approved ? "Remove" : "Admit", () => Send(new(participant.Approved ? "remove" : "admit", Target: participant.Id)))); people.Children.Add(person);
                }
                refreshingRoom = true; shared.IsEnabled = room.Identity?.Host == true; shared.IsChecked = snapshot.SharedControls; refreshingRoom = false;
                queueText.Text = snapshot.Queue?.Length > 0 ? string.Join(" → ", snapshot.Queue) : "Queue is empty";
                if (snapshot.Playback is not null) AcceptState(snapshot.Playback);
                if (snapshot.Media is not null && loadedMedia != snapshot.Media.Id) await LoadRoomMedia(snapshot.Media);
                break;
            case "playback": AcceptState(Wire.Read<PlaybackState>(message.Data!)); break;
            case "chat": case "notice": chat.Children.Add(Text(message.Text ?? "",14)); if (chat.Children.Count > 150) chat.Children.RemoveAt(0); break;
            case "error": status.Text = message.Text; break;
        }
    }
    private void AcceptState(PlaybackState state) { if (target is null || state.Revision > target.Revision) { target = state; revision = -1; } }
    private async Task LoadRoomMedia(SharedMedia media)
    {
        var client = room; if (client is null) return; var current = ++generation; loadedMedia = media.Id;
        await loading.WaitAsync(lifetime.Token);
        try
        {
            if (client != room || current != generation) return;
            ready = false; player?.Stop(); if (bridge is not null) await bridge.DisposeAsync(); bridge = null;
            var source = await client.GetSourceAsync(media, lifetime.Token); if (client != room || current != generation) return;
            bridge = new MediaBridge(); var uri = await bridge.StartAsync(source);
            using var movie = new Media(vlc!, uri); movie.AddOption(":network-caching=1500");
            foreach (var subtitle in (media.Subtitles ?? []).Take(12))
            {
                if (subtitle.Length is <= 0 or > 8388608 || !new[] { ".srt", ".ass", ".ssa" }.Contains(subtitle.Extension.ToLowerInvariant())) continue;
                var asset = await client.GetSourceAsync(subtitle, lifetime.Token); var dir = Path.Combine(App.DataDirectory, "subtitles"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + subtitle.Extension.ToLowerInvariant());
                await using (var output = File.Create(path)) for (long offset = 0; offset < subtitle.Length;) { var bytes = await asset.ReadAsync(offset, (int)Math.Min(32768, subtitle.Length - offset), lifetime.Token); await output.WriteAsync(bytes, lifetime.Token); offset += bytes.Length; }
                movie.AddSlave(MediaSlaveType.Subtitle, 2, new Uri(path).AbsoluteUri);
            }
            if (client != room || current != generation) return;
            roomTitle.Text = media.Title; roomHint.Text = client.Identity?.Host == true ? "You are hosting · Copy the invitation for friends" : "Streaming from host"; tabs.SelectedIndex = 1;
            await Dispatcher.UIThread.InvokeAsync(() => video.UpdateLayout(), DispatcherPriority.Render);
            player!.Play(movie);
        }
        catch { if (current == generation) loadedMedia = null; throw; }
        finally { loading.Release(); }
    }
    private void Send(WireMessage message) { try { room?.Send(message); } catch (IOException ex) { status.Text = ex.Message; } }
    private void SendReady() { Send(new("ready")); }
    private void SendPlayback(bool playing, long position)
    {
        if (room?.Snapshot?.Media is not { } media) return;
        if (room.Identity?.Host != true && !room.Snapshot.SharedControls) { status.Text = "The host controls playback."; return; }
        Send(new("playback", Data: Wire.Serialize(new PlaybackState(0, media.Id, playing, position, 0))));
    }
    private void Tick()
    {
        if (player is null) return;
        if (!seeking) { timeline.Maximum = Math.Max(1, player.Length); timeline.Value = Math.Max(0, player.Time); time.Text = FormatTime(player.Time) + " / " + FormatTime(player.Length); }
        play.Content = player.IsPlaying ? "Pause" : "Play"; if (++tick % 8 == 0 && ready) RefreshTracks();
        if (room is null || target is null || !ready || seeking) return;
        var now = Wire.Now + room.ServerOffsetMs; if (now < target.AtUnixMs) { player.SetPause(true); return; }
        var desired = SyncMath.TargetPosition(target, now); var drift = desired - Math.Max(0, player.Time);
        if (revision != target.Revision || Math.Abs(drift) > 1200)
        {
            if (target.Playing && player.State is VLCState.Ended or VLCState.Stopped) player.Play();
            if (Math.Abs(drift) > 200) player.Time = desired; player.SetPause(!target.Playing); player.SetRate(1); revision = target.Revision;
        }
        else if (target.Playing) player.SetRate(SyncMath.Correction(drift));
    }
    private static string FormatTime(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3600000 ? @"h\:mm\:ss" : @"mm\:ss");
    private void TogglePlay() { if (player is null) return; if (room is not null) SendPlayback(!player.IsPlaying, player.Length > 0 && player.Time >= player.Length - 250 ? 0 : Math.Max(0, player.Time)); else if (player.IsPlaying) player.SetPause(true); else player.Play(); }
    private void Seek() { if (player is not null && seeking) { if (room is null) player.Time = (long)timeline.Value; else SendPlayback(target?.Playing == true, (long)timeline.Value); } seeking = false; }
    private void Fullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    private void RefreshTracks()
    {
        if (player is null) return; refreshingTracks = true;
        try { audio.ItemsSource = player.AudioTrackDescription.Select(t => new TrackOption(t.Id,t.Name)).ToArray(); subtitles.ItemsSource = player.SpuDescription.Select(t => new TrackOption(t.Id,t.Name)).ToArray(); audio.SelectedItem = audio.Items.OfType<TrackOption>().FirstOrDefault(t => t.Id == player.AudioTrack); subtitles.SelectedItem = subtitles.Items.OfType<TrackOption>().FirstOrDefault(t => t.Id == player.Spu); }
        finally { refreshingTracks = false; }
    }
    private async Task LoadSubtitle()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Load subtitles", FileTypeFilter = new[] { new FilePickerFileType("Subtitles") { Patterns = new[] { "*.srt", "*.ass", "*.ssa", "*.sub" } } } });
        var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) player?.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
    }
    private async Task CopyInvitation() { if (room?.Identity is null) return; if (Clipboard is not null) await Clipboard.SetTextAsync((server.Text ?? "").TrimEnd('/') + "#" + room.Identity.Room); status.Text = "Invitation copied"; }
    private void SendChat() { var text = chatInput.Text?.Trim(); if (!string.IsNullOrEmpty(text)) { Send(new("chat",Text:text)); chatInput.Text = ""; } }
    private void AddQueue() { if (selected is null) return; if (room is not null && room.Identity?.Host != true) { Send(new("chat",Text:"Suggested movie: " + selected.Title)); return; } if (queue.Count >= 50) { status.Text = "Queue is full"; return; } queue.Enqueue(selected); PublishQueue(); }
    private void PublishQueue() { queueText.Text = queue.Count > 0 ? string.Join(" → ", queue.Select(x => x.Title)) : "Queue is empty"; if (room?.Identity?.Host == true) Send(new("queue",Data:Wire.Serialize(queue.Select(x => x.Title).ToArray()))); }
    private async Task PlayNext() { if (!queue.TryDequeue(out var next)) return; selected = next; if (room?.Identity?.Host == true) await Host(); else if (room is null) await PlayLocal(); PublishQueue(); }
    private async Task Disconnect()
    {
        var previous = room; room = null; ++generation; target = null; loadedMedia = null; ready = false; player?.Stop();
        if (previous is not null) await previous.DisposeAsync();
        await loading.WaitAsync(); try { if (bridge is not null) await bridge.DisposeAsync(); bridge = null; } finally { loading.Release(); }
        people.Children.Clear(); shared.IsEnabled = false; roomHint.Text = "Disconnected";
    }
    private sealed record TrackOption(int Id, string Name) { public override string ToString() => Name; }
}
