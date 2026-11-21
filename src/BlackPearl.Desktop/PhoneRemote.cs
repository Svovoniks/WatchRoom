using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlackPearl.Core;
using LibVLCSharp.Shared;

namespace BlackPearl.Desktop;

// Settings > Profile > Phone remote, and the PC's side of BlackPearl Mobile (PHONE-REMOTE.md). Paired phones send the same
// commands as the player's keys and buttons, so a room's control rules apply to them the same way, and they get the
// player's state as it changes.
public partial class MainWindow
{
    private PhoneRemoteHost? phoneRemote;
    private string? phoneRemoteServer;
    private DispatcherTimer? phoneRemoteTimer;
    private string? phoneRemoteSentKey;
    private long phoneRemoteSentAt, phoneRemoteSentPosition;
    private string? phoneRemoteKey;
    private Window? pairingWindow;

    private bool PhoneRemoteEnabled => PhoneRemoteSettings.Enabled(library);
    private bool PhonesCanStart => PhoneRemoteSettings.PhonesCanStart(library);
    private static string PcName => PhoneRemoteSettings.PcName;

    // Whether closing BlackPearl starts the phone host (PhoneHost.cs), worked out before closing stops the link. Not when
    // it closes to install an update, which needs every copy of the app gone, or when Windows is signing out.
    // A sign-out the user cancels leaves the flag behind, so it counts for a minute.
    private bool closingForUpdate;
    private long sessionEndingAt = long.MinValue / 2;
    private bool SessionEnding => Environment.TickCount64 - sessionEndingAt < 60000;
    private bool KeepsPhonesAfterClosing => PhoneRemoteEnabled && PhoneRemoteSettings.WhileClosed(library) && !closingForUpdate && !SessionEnding &&
        OpenFileHandoff.Claimed && PhoneRemoteSettings.HostKey(library) is not null && PhoneRemoteSettings.HasPhones(library);

    // Keeps the sign-in entry in step with the settings, off the UI thread.
    private void SyncPhoneHostStartup() { var store = library; _ = Task.Run(() => PhoneHostStartup.Sync(store)); }

    // Starts, restarts or stops the link to match the settings. Called at startup and when they change.
    private void SyncPhoneRemote()
    {
        var server = SavedServerAddress;
        if (!PhoneRemoteEnabled || closing)
        {
            StopPhoneRemote();
            return;
        }
        if (phoneRemote is not null && phoneRemoteServer == server) return;
        StopPhoneRemote();
        var hostKey = library.Setting("phoneRemoteHostKey");
        if (hostKey is not { Length: 64 }) { hostKey = PersistentRooms.NewKey(); library.Setting("phoneRemoteHostKey", hostKey); }
        try { RoomAddress.ValidateServer(server); }
        catch (ArgumentException) { RenderPhoneRemote(); return; }
        phoneRemoteServer = server;
        phoneRemote = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = server, HostKey = hostKey, PcName = PcName, Store = new LibraryPhoneStore(library), CanStart = PhonesCanStart, Features = PhoneFeatures,
            AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        });
        var remote = phoneRemote;
        remote.Changed += () => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) RenderPhoneRemote(); });
        remote.Paired += _ => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) { pairingWindow?.Close(); SetStatus("Phone paired. It can now control BlackPearl."); } });
        remote.Command += (session, command) => Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) RunPhoneCommand(session, command); });
        // Asked from a native thread when a phone connects. The state is read on the UI thread, which then sends it to
        // every phone, rather than holding the native thread until it can.
        remote.CurrentState = () => { Dispatcher.BeginInvoke(() => { if (phoneRemote == remote) PublishPhoneRemoteState(force: true); }); return null; };
        AttachPhonePlayback(remote);
        remote.Start();
        phoneRemoteTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PublishPhoneRemoteState(), Dispatcher);
        phoneRemoteTimer.Start();
        RenderPhoneRemote();
    }

    private void StopPhoneRemote()
    {
        phoneRemoteTimer?.Stop();
        pairingWindow?.Close();
        if (phoneRemote is { } remote) { phoneRemote = null; remote.Dispose(); }
        ClosePhoneStreams();
        phoneRemoteServer = null; phoneRemoteSentKey = null;
        RenderPhoneRemote();
    }

    // Whether the video playing is one of the library's, rather than a file opened on its own. Remembered per video and
    // library list: the state is read four times a second while a phone is connected, and the library can load after a
    // file opened at startup.
    private (string Id, List<MediaItem> Items, bool Known) phoneStateItem;
    private bool InLibrary(MediaItem item)
    {
        if (phoneStateItem.Id != item.Id || !ReferenceEquals(phoneStateItem.Items, items))
            phoneStateItem = (item.Id, items, !string.IsNullOrEmpty(item.Path) && items.Any(x => x.Id == item.Id));
        return phoneStateItem.Known;
    }

    private RemoteState PhoneRemoteState()
    {
        RemoteRoom? inRoom = room?.Snapshot is { } snapshot ? new(snapshot.Name ?? "Room", snapshot.People.Length) : null;
        if (!HasMediaPlayback || player is null) return new RemoteState(false, Room: inRoom, Volume: (int)Volume.Value);
        var item = playingItem;
        var title = room?.Snapshot?.Media?.Title ?? item?.ShownTitle ?? "BlackPearl";
        string? detail = null;
        if (audioPlayback) detail = string.Join(" · ", new[] { AudioArtist.Text, AudioAlbum.Text }.Where(x => !string.IsNullOrWhiteSpace(x)));
        else if (item is { Series: not null })
            detail = string.Join(" · ", new[] { item.Season is { } season ? $"Season {season}" : null, item.EpisodeDisplayTitle }.OfType<string>());
        else if (item?.Year is { } year) detail = year.ToString(CultureInfo.InvariantCulture);
        var position = Math.Max(0, PlaybackPosition());
        return new RemoteState(true, title, string.IsNullOrWhiteSpace(detail) ? null : detail, WantsMediaPlayback, position, Math.Max(0, player.Length),
            (int)Math.Round(Volume.Value), player.Mute, loopMode switch { LoopMode.All => "all", LoopMode.One => "one", _ => "off" }, fullscreen,
            PreviousButton.IsEnabled, NextButton.IsEnabled, CanControlMediaPlayback, inRoom,
            // Only a video this PC plays on its own can move to the phone; a room's belongs to the room.
            room is null && item is { IsMusic: false } && InLibrary(item) ? item.Id : null,
            room is null ? PhoneRemoteQueue(item) : null);
    }

    // The saved queue playing here and the video's place in it. The player's queue is the saved one's videos that the
    // library has, in order, so one pass finds the place without a lookup on every state.
    private RemoteQueue? PhoneRemoteQueue(MediaItem? item)
    {
        if (!PlayingFromQueue(item) || activeQueueId is null || savedQueues.FirstOrDefault(q => q.Id == activeQueueId) is not { } saved) return null;
        for (int index = 0, at = 0; index < saved.MediaIds.Length && at < queue.Count; index++)
        {
            if (saved.MediaIds[index] != queue[at].Id) continue;
            if (at == queuePosition) return new(saved.Id, saved.Name, index);
            at++;
        }
        return null;
    }

    // Sends the state when something other than the advancing position changed, after a seek, and every five seconds
    // while playing so phones that advance the position themselves don't drift.
    private void PublishPhoneRemoteState(bool force = false)
    {
        if (phoneRemote is not { } remote || remote.Connected.Count == 0) { phoneRemoteSentKey = null; return; }
        var state = PhoneRemoteState();
        var key = Wire.Serialize(state with { PositionMs = 0 });
        var now = Environment.TickCount64;
        var expected = phoneRemoteSentPosition + (state.Playing == true ? now - phoneRemoteSentAt : 0);
        var jumped = state.PositionMs is { } position && Math.Abs(position - expected) > 1500;
        if (!force && key == phoneRemoteSentKey && !jumped && (state.Playing != true || now - phoneRemoteSentAt < 5000)) return;
        phoneRemoteSentKey = key; phoneRemoteSentAt = now; phoneRemoteSentPosition = state.PositionMs ?? 0;
        remote.Publish(state);
    }

    private void RunPhoneCommand(PhoneRemoteSession session, RemoteCommand command)
    {
        if (phoneRemote is not { } remote) return;
        var action = command.Action;
        if (action is "volume" or "mute")
        {
            if (action == "volume" && command.Number is { } level) Volume.Value = Math.Clamp(Math.Round(level), 0, 100);
            else if (action == "mute") MutePlayback(this, new RoutedEventArgs());
            return;
        }
        if (action is not ("toggle" or "play" or "pause" or "seek-by" or "seek-to" or "previous" or "next" or "stop" or "loop" or "fullscreen")) return;
        if (!HasMediaPlayback)
        {
            // Play continues the up-next countdown even with the player's video ended.
            if (action is "toggle" or "play" && upNextChoice is not null) RequestPlayback(true);
            else remote.SendError(session, "nothing-playing", "Nothing is playing on the PC.");
            return;
        }
        if (!CanControlMediaPlayback && action is not "fullscreen")
        {
            remote.SendError(session, "not-allowed", StreamerName + " is playing the video and controls playback in this room.");
            return;
        }
        switch (action)
        {
            case "toggle": RequestPlayback(!WantsMediaPlayback); break;
            case "play": if (!WantsMediaPlayback) RequestPlayback(true); break;
            case "pause": if (WantsMediaPlayback) RequestPlayback(false); break;
            case "seek-by" when command.Number is { } offset: MovePlayback((long)Math.Clamp(offset, -86400000, 86400000)); break;
            case "seek-to" when command.Number is { } to && player!.Length > 0: SeekPlayback(Math.Clamp((long)to, 0, player.Length)); break;
            case "previous": if (PreviousButton.IsEnabled) PreviousQueued(this, new RoutedEventArgs()); break;
            case "next": if (NextButton.IsEnabled) NextQueued(this, new RoutedEventArgs()); break;
            case "stop": StopPlayback(this, new RoutedEventArgs()); break;
            case "loop": ToggleLoop(this, new RoutedEventArgs()); break;
            case "fullscreen":
                if (RoomPage.Visibility != Visibility.Visible) remote.SendError(session, "not-shown", "Open the player on the PC to go fullscreen.");
                else SetFullscreen(!fullscreen);
                break;
        }
        // The player applies most of these a moment later; the next tick sends the state once it has.
        UpdateSystemMediaControls();
    }

    // Settings > Profile, below the server. Rebuilt only when what it shows changed, so it doesn't move focus.
    private void RenderPhoneRemote(bool force = false)
    {
        if (PhoneRemoteContent is null || preferenceSection != "Profile") return;
        var remote = phoneRemote;
        var phones = remote?.Phones ?? new LibraryPhoneStore(library).Load();
        var connected = remote?.Connected.Select(s => (s.PhoneId, s.Relayed)).ToArray() ?? [];
        var key = Wire.Serialize(new { enabled = PhoneRemoteEnabled, canStart = PhonesCanStart, status = remote?.Status, ready = remote?.ServerReady, phones, connected, server = SavedServerAddress });
        if (!force && key == phoneRemoteKey) return;
        phoneRemoteKey = key;
        PhoneRemoteContent.Content = PhoneRemotePanel(remote, phones, connected);
    }

    private FrameworkElement PhoneRemotePanel(PhoneRemoteHost? remote, IReadOnlyList<PairedPhone> phones, (string PhoneId, bool Relayed)[] connected)
    {
        var page = new StackPanel();
        TextBlock Line(string text, bool muted = true, double size = 13)
        {
            var line = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (muted) line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return line;
        }
        StackPanel Card(string title)
        {
            var panel = new StackPanel();
            var heading = new TextBlock { Text = title }; heading.SetResourceReference(StyleProperty, "CardTitle"); panel.Children.Add(heading);
            var border = new Stone { Child = panel, Margin = new Thickness(0, 0, 0, 20) }; border.SetResourceReference(StyleProperty, "Card");
            AutomationProperties.SetName(border, title); page.Children.Add(border);
            return panel;
        }
        var enabled = PhoneRemoteEnabled;
        var main = Card("Phone remote");
        main.Children.Add(Line("Control playback on this PC from BlackPearl Mobile on your phone, at home or away, or browse this library and watch it on the phone, then carry on here from the same moment. The phone connects straight to this PC; your room server only introduces them."));
        var toggle = new CheckBox { IsChecked = enabled, Content = "Allow paired phones to control BlackPearl", Margin = new Thickness(0, 14, 0, 0) };
        toggle.Click += (_, _) => { library.Setting("phoneRemote", toggle.IsChecked == true ? "on" : "off"); SyncPhoneRemote(); SyncPhoneHostStartup(); RenderPhoneRemote(force: true); };
        main.Children.Add(toggle);
        var canStart = new CheckBox { IsChecked = PhonesCanStart, IsEnabled = enabled, Content = "Phones can start videos and change queues on their own", Margin = new Thickness(0, 10, 0, 0) };
        canStart.Click += (_, _) =>
        {
            library.Setting("phoneRemoteCanStart", canStart.IsChecked == true ? "on" : "off");
            // Phones learn this when they connect, so the link starts again.
            StopPhoneRemote(); SyncPhoneRemote();
        };
        main.Children.Add(canStart);
        var whileClosed = PhoneRemoteSettings.WhileClosed(library);
        var keep = new CheckBox { IsChecked = whileClosed, IsEnabled = enabled, Content = "Phones can browse and watch while BlackPearl is closed", Margin = new Thickness(0, 10, 0, 0) };
        keep.Click += (_, _) => { library.Setting("phoneRemoteWhileClosed", keep.IsChecked == true ? "on" : "off"); SyncPhoneHostStartup(); RenderPhoneRemote(force: true); };
        main.Children.Add(keep);
        var signIn = new CheckBox { IsChecked = PhoneRemoteSettings.AtSignIn(library), IsEnabled = enabled && whileClosed, Content = "Also when I sign in to Windows, before BlackPearl is opened", Margin = new Thickness(28, 8, 0, 0) };
        signIn.Click += (_, _) => { library.Setting("phoneRemoteAtSignIn", signIn.IsChecked == true ? "on" : "off"); SyncPhoneHostStartup(); };
        main.Children.Add(signIn);
        var keepHint = Line("BlackPearl's icon stays in the notification area meanwhile. Right-click it to turn this off.", size: 12.5);
        keepHint.Margin = new Thickness(28, 6, 0, 0);
        if (!enabled || !whileClosed) keepHint.Opacity = .6;
        main.Children.Add(keepHint);
        var status = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var pair = new Button { Content = "Pair a phone", IsEnabled = enabled && remote is not null, VerticalAlignment = VerticalAlignment.Center };
        pair.SetResourceReference(StyleProperty, "Primary");
        pair.Click += (_, _) => ShowPairing();
        DockPanel.SetDock(pair, Dock.Right); status.Children.Add(pair);
        var statusText = !enabled ? "Off. Phones can't connect." : remote is null ? "Enter a valid BlackPearl server above to use the phone remote."
            : remote.ServerReady ? $"Ready. Phones reach this PC through {new Uri(SavedServerAddress).Host}." : remote.Status;
        var statusLine = Line(statusText); statusLine.Margin = new Thickness(0, 0, 12, 0); statusLine.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(statusLine, AutomationLiveSetting.Polite);
        status.Children.Add(statusLine);
        main.Children.Add(status);

        var list = Card("Paired phones");
        if (phones.Count == 0) list.Children.Add(Line("No phones yet. Choose Pair a phone and scan the code with BlackPearl Mobile."));
        foreach (var phone in phones.OrderByDescending(p => p.LastSeen))
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, "Remove " + phone.Name);
            remove.Click += (_, _) =>
            {
                if (phoneRemote is { } live) live.Forget(phone.Id);
                else { var store = new LibraryPhoneStore(library); store.Save(store.Load().Where(p => p.Id != phone.Id).ToArray()); }
                RenderPhoneRemote(force: true);
            };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var name = Line(phone.Name, muted: false); name.FontWeight = FontWeights.SemiBold; texts.Children.Add(name);
            var link = connected.FirstOrDefault(c => c.PhoneId == phone.Id);
            texts.Children.Add(Line(link.PhoneId is not null ? (link.Relayed ? "Connected through the relay" : "Connected") : "Last used " + When(phone.LastSeen), size: 12.5));
            row.Children.Add(texts); AutomationProperties.SetName(row, phone.Name);
            list.Children.Add(row);
        }
        if (phones.Count > 0) { var hint = Line("Removing a phone cuts it off at once, wherever it is."); hint.Margin = new Thickness(0, 6, 0, 0); list.Children.Add(hint); }
        return page;
    }

    private static string When(long at) => DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime() is var local && local.Date == DateTime.Today
        ? "today, " + local.ToString("t", CultureInfo.CurrentCulture) : local.ToString("MMM d", CultureInfo.CurrentCulture);

    // A window with the pairing QR code, the link as text, and how long it lasts. Pairing closes it.
    private void ShowPairing()
    {
        if (phoneRemote is not { } remote) return;
        if (pairingWindow is not null) { pairingWindow.Activate(); return; }
        var link = remote.BeginPairing();
        var expires = DateTimeOffset.Now + PhoneRemoteProtocol.PairingLifetime;
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 24), Width = 340 };
        var title = new TextBlock { Text = "Pair a phone", FontSize = 20, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        panel.Children.Add(title);
        var hint = new TextBlock { Text = "Open BlackPearl Mobile and scan this code. You can also scan it with the phone's camera once the app is installed.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16), FontSize = 13 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(hint);
        var code = new Image { Source = QrImage(link), Width = 260, Height = 260, SnapsToDevicePixels = true, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(code, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(code, "Pairing code");
        panel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center, Child = code });
        var timeLeft = new TextBlock { Margin = new Thickness(0, 14, 0, 0), FontSize = 12.5, TextAlignment = TextAlignment.Center };
        timeLeft.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(timeLeft, AutomationLiveSetting.Off);
        panel.Children.Add(timeLeft);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        // Both buttons set their margins: the default bottom margin would make one taller than the other in this row.
        var copy = new Button { Content = "Copy link", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        copy.Click += (_, _) => { try { Clipboard.SetText(link); copy.Content = "Copied"; } catch (System.Runtime.InteropServices.COMException) { } };
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(copy); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        var window = new Window { Title = "Pair a phone", Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.SetResourceReference(BackgroundProperty, "Canvas");
        window.SetResourceReference(ForegroundProperty, "Ink");
        close.Click += (_, _) => window.Close();
        var countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            var left = expires - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero || phoneRemote is null || !phoneRemote.PairingOpen) { timeLeft.Text = "This code has expired. Close and pair again for a new one."; code.Opacity = .25; return; }
            timeLeft.Text = $"Works once, for {Math.Ceiling(left.TotalMinutes)} more minute{(left.TotalMinutes > 1 ? "s" : "")}.";
        }, Dispatcher);
        timeLeft.Text = $"Works once, for {PhoneRemoteProtocol.PairingLifetime.TotalMinutes} minutes.";
        window.Closed += (_, _) => { countdown.Stop(); pairingWindow = null; if (phoneRemote == remote && remote.PairingOpen) remote.CancelPairing(); };
        pairingWindow = window;
        window.Show();
    }

    private static BitmapSource QrImage(string text)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var modules = data.ModuleMatrix;
        var size = modules.Count;
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++) pixels[y * size + x] = modules[y][x] ? (byte)0 : (byte)255;
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Gray8, null, pixels, size);
        bitmap.Freeze();
        return bitmap;
    }
}
