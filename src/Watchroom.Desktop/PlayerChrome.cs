using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Supplies the queue menu's row details (position, playing mark, runtime, faded when played) from the window's playback state.
public sealed class QueueRowConverter : IValueConverter
{
    internal static Func<object?, string, object?>? Resolve;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Resolve?.Invoke(value, (string)parameter) ?? Fallback((string)parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    internal static readonly SolidColorBrush RowInk = Frozen(0xF4, 0xF4, 0xF7), RowDone = Frozen(0x7D, 0x7F, 0x8C);
    internal static SolidColorBrush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private static object Fallback(string part) => part switch { "foreground" => RowInk, "playing" => Visibility.Collapsed, _ => "" };
}

public partial class MainWindow
{
    public sealed record ChatBubble(string Name, string Text, double Opacity);
    private readonly ObservableCollection<ChatBubble> chatBubbles = [];
    private readonly List<(string Name, string Text, long Expires)> recentChat = [];
    private DispatcherTimer? chatBubbleTimer;
    private int unreadChat;
    // Set while the queue menu's switch is on Queue but no queue has been chosen yet.
    private bool queueModeRequested;
    private static readonly SolidColorBrush statusIdle = QueueRowConverter.Frozen(0x6B, 0x6D, 0x7A), statusLive = QueueRowConverter.Frozen(0x3F, 0xB9, 0x7A), statusWaiting = QueueRowConverter.Frozen(0xE0, 0xA8, 0x4A);

    private void InitializePlayerChrome()
    {
        QueueRowConverter.Resolve = QueueRowValue;
        ChatBubbles.ItemsSource = chatBubbles;
        // LibVLC moves the video overlay into its own window, outside this window's name scope,
        // so overlay controls are linked by source rather than ElementName.
        RoomHeader.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = PlaybackControls });
        foreach (var (pill, menu) in new[] { (AudioPill, AudioMenu), (SubtitlePill, SubtitleMenu), (QueuePill, PlayerQueueMenu) })
        {
            menu.PlacementTarget = pill;
            pill.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(System.Windows.Controls.Primitives.Popup.IsOpen)) { Source = menu, Mode = BindingMode.TwoWay });
        }
        foreach (var (text, detail, list, fallback) in new[] { (AudioPillText, AudioPillDetail, AudioTracks, "Audio"), (SubtitlePillText, SubtitlePillDetail, SubtitleTracks, "Subtitles") })
        {
            text.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Label") { Source = list, FallbackValue = fallback, TargetNullValue = fallback });
            detail.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Detail") { Source = list, FallbackValue = "", TargetNullValue = "" });
        }
        // Closing the menu without choosing a queue returns the switch to Episodes / Off.
        PlayerQueueMenu.Closed += (_, _) => { if (queueModeRequested && !HasPlaybackQueue) { queueModeRequested = false; UpdateSessionControls(); } };
    }

    // Row positions, built once per list so a long season's rows don't each scan the whole list as they're realized.
    private (object? Source, Dictionary<object, int> Positions) queueRowPositions = (null, new(ReferenceEqualityComparer.Instance));
    private int QueueRowPosition(object row)
    {
        var source = QueueList.ItemsSource;
        if (!ReferenceEquals(source, queueRowPositions.Source))
        {
            var positions = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            var index = 0;
            foreach (var item in source as System.Collections.IEnumerable ?? Array.Empty<object>()) positions.TryAdd(item, index++);
            queueRowPositions = (source, positions);
        }
        return queueRowPositions.Positions.TryGetValue(row, out var position) ? position : -1;
    }
    private object? QueueRowValue(object? row, string part)
    {
        var episode = row as EpisodeEntry;
        var media = episode?.Media ?? row as MediaItem;
        if (media is null) return null;
        // Episode rows mirror listedEpisodes and queue rows mirror the queue, so a row's list position is its index.
        var index = QueueRowPosition(row!);
        var playing = playingItem?.Id == media.Id && (episode is not null || queuePosition < 0 || index == queuePosition);
        return part switch
        {
            // Played rows dim their title colour rather than their opacity, which would give every row its own render layer.
            "foreground" => (episode is not null ? !playing && WasWatched(media.Id) : queuePosition >= 0 && index >= 0 && index < queuePosition)
                ? QueueRowConverter.RowDone : QueueRowConverter.RowInk,
            "playing" => playing ? Visibility.Visible : Visibility.Collapsed,
            "number" => playing ? "" : episode is not null ? media.Episode?.ToString() ?? "" : index >= 0 ? (index + 1).ToString() : "",
            "runtime" => media.RuntimeMinutes is > 0 and var minutes ? minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00}" : $"{minutes} min" : "",
            _ => null
        };
    }

    // The queue menu's switch: Episodes (or Off) turns the queue off; Queue picks one. Neither interrupts the playing video.
    private void RefreshQueueMode(bool canManage)
    {
        var queueMode = HasPlaybackQueue || queueModeRequested;
        QueueModeOn.IsChecked = queueMode; QueueModeOff.IsChecked = !queueMode;
        QueueModeOn.IsEnabled = QueueModeOff.IsEnabled = canManage;
        var series = playingItem is { Series: not null, Season: not null, Episode: not null, IsExtra: false };
        QueueModeOffText.Text = series ? "Episodes" : "Off";
        QueueModeOffIcon.Text = series ? "" : "";
        QueuePickerRow.Visibility = queueMode && canManage ? Visibility.Visible : Visibility.Collapsed;
        PlayPlayerQueueButton.Visibility = QueueList.HasItems && !ShowingEpisodeList ? Visibility.Visible : Visibility.Collapsed;
        var reorderable = ShowingEpisodeList ? Visibility.Collapsed : Visibility.Visible;
        QueueEarlier.Visibility = QueueLater.Visibility = RemoveQueueButton.Visibility = reorderable;
        AddEpisodeToQueueButton.Visibility = ShowingEpisodeList && canManage ? Visibility.Visible : Visibility.Collapsed;
        AddEpisodeToQueueButton.IsEnabled = QueueList.SelectedItem is EpisodeEntry;
        AddCurrentToQueueButton.Visibility = canManage && activeQueueId is not null && playingItem is { } current && queue.All(item => item.Id != current.Id)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!canManage) return;
        if (queueModeRequested && !HasPlaybackQueue) QueueStatus.Text = "Choose a queue. The current video keeps playing.";
        else if (!queueMode && !ShowingEpisodeList) QueueStatus.Text = "No queue. Switch to Queue to line up what plays next.";
        else if (queue.Count > 0 && queuePosition < 0 && playingItem is not null)
            QueueStatus.Text = "This video keeps playing. Next starts the queue from its first video.";
    }
    private void QueueModeOffClicked(object sender, RoutedEventArgs e)
    {
        if (CanEditQueue())
        {
            queueModeRequested = false;
            if (HasPlaybackQueue) SelectNoQueue();
        }
        UpdateSessionControls();
    }
    private void QueueModeOnClicked(object sender, RoutedEventArgs e)
    {
        if (!CanEditQueue() || HasPlaybackQueue) { UpdateSessionControls(); return; }
        queueModeRequested = true;
        // Bring back the last queue used; otherwise let the user pick one.
        var last = savedQueues.FirstOrDefault(saved => saved.Id == library.Setting("lastQueue"));
        if (last is not null && ResolveQueue(last).Any()) { SwitchToQueue(last); return; }
        RefreshQueuePickers(); UpdateSessionControls();
        Dispatcher.BeginInvoke(() => PlayerQueuePicker.IsDropDownOpen = PlayerQueueMenu.IsOpen, DispatcherPriority.Input);
    }
    private void AddEpisodeToQueue(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedItem is EpisodeEntry entry) ShowAddToQueueMenu((FrameworkElement)sender, [entry.Media]);
    }
    private void AddCurrentToQueue(object sender, RoutedEventArgs e)
    {
        if (savedQueues.FirstOrDefault(saved => saved.Id == activeQueueId) is { } active && playingItem is { } current) AddToSavedQueue(active, [current]);
    }

    private void RefreshQueuePill()
    {
        if (room?.Identity?.Host == false) return; // Guests show the host's queue from the room snapshot.
        if (ShowingEpisodeList)
        {
            var index = Array.FindIndex(listedEpisodes, item => item.Id == playingItem?.Id);
            SetQueuePill("Episodes", index >= 0 ? $"{index + 1} / {listedEpisodes.Length}" : $"{listedEpisodes.Length}", "");
        }
        else if (queue.Count > 0)
            SetQueuePill(savedQueues.FirstOrDefault(saved => saved.Id == activeQueueId)?.Name ?? "Queue",
                queuePosition >= 0 && queuePosition < queue.Count ? $"{queuePosition + 1} / {queue.Count}" : $"{queue.Count} videos");
        else if (roomLibrary?.Queue.Entries.Length is > 0 and var entries) SetQueuePill("Queue", $"{entries} videos");
        else SetQueuePill("Queue", queueModeRequested ? "" : "Off");
    }
    private void SetQueuePill(string text, string detail, string icon = "")
    {
        QueuePillText.Text = text; QueuePillDetail.Text = detail; QueuePillIcon.Text = icon;
    }
    private string[]? hintedAudioLanguages;
    // Runs every playback tick; rebuild the hint only when the preference changes.
    private void RefreshAudioPreferenceHint()
    {
        if (ReferenceEquals(hintedAudioLanguages, audioLanguages)) return;
        hintedAudioLanguages = audioLanguages;
        AudioPreferenceHint.Text = audioLanguages.Length == 0 ? "" : "Default picked from Settings › Preferred audio: " + string.Join(", ", audioLanguages);
        AudioPreferenceHint.Visibility = audioLanguages.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshSharedControlsChip()
    {
        var shared = SharedControls.IsChecked == true;
        SharedControlsText.Text = shared ? "Everyone can control" : "Host controls";
        SharedControlsIcon.Text = shared ? "" : "";
        SharedControls.ToolTip = room?.Identity?.Host == true
            ? shared ? "Guests can play, pause and seek. Click to keep control to yourself." : "Only you control playback. Click to let admitted guests play, pause and seek."
            : shared ? "Everyone in the room can play, pause and seek." : "The host controls playback.";
    }
    private void RefreshRoomStatusDot() =>
        RoomStatusDot.Fill = room is null ? statusIdle : room.IsConnected && !room.IsPeerRecovering ? statusLive : statusWaiting;

    // Chat appears over the video for a few seconds while the room panel isn't showing it.
    private void ChatArrived(string text)
    {
        var panelOpen = roomPanelVisible && !fullscreen;
        if (panelOpen && RoomTabs.SelectedIndex == 1) return;
        unreadChat++; RefreshUnreadBadge();
        if (panelOpen) return;
        var split = text.IndexOf(": ", StringComparison.Ordinal);
        var (name, body) = split is > 0 and < 48 ? (text[..split] + " ", text[(split + 2)..]) : ("", text);
        recentChat.Add((name, body, Environment.TickCount64 + 6000));
        if (recentChat.Count > 3) recentChat.RemoveAt(0);
        RenderChatBubbles();
        chatBubbleTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => ExpireChatBubbles(), Dispatcher);
        chatBubbleTimer.Start();
    }
    private void ExpireChatBubbles()
    {
        var now = Environment.TickCount64;
        if (recentChat.RemoveAll(bubble => bubble.Expires <= now) > 0) RenderChatBubbles();
        if (recentChat.Count == 0) chatBubbleTimer?.Stop();
    }
    private void RenderChatBubbles()
    {
        chatBubbles.Clear();
        // Older messages fade so the newest one reads first.
        for (var i = 0; i < recentChat.Count; i++)
            chatBubbles.Add(new(recentChat[i].Name, recentChat[i].Text, (recentChat.Count - 1 - i) switch { 0 => 1, 1 => 0.8, _ => 0.55 }));
    }
    private void ClearChatBubbles()
    {
        recentChat.Clear(); chatBubbles.Clear(); chatBubbleTimer?.Stop();
        unreadChat = 0; RefreshUnreadBadge();
    }
    private void RefreshUnreadBadge()
    {
        RoomUnreadText.Text = unreadChat > 99 ? "99+" : unreadChat.ToString();
        RoomUnreadBadge.Visibility = unreadChat > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Narrow bars drop the pills' details first, then the volume slider, then the time.
    private int controlsDensity = -1;
    private void PlaybackControlsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        // Resizing fires this continuously; only re-lay out the bar when it crosses a density step.
        var density = width >= 1000 ? 3 : width >= 780 ? 2 : width >= 640 ? 1 : 0;
        if (density == controlsDensity) return;
        controlsDensity = density;
        foreach (var detail in new[] { AudioPillDetail, SubtitlePillDetail, QueuePillDetail })
        { detail.MaxWidth = width >= 1000 ? 96 : 0; detail.Margin = new Thickness(width >= 1000 ? 6 : 0, 0, 0, 0); }
        foreach (var text in new[] { AudioPillText, SubtitlePillText, QueuePillText }) text.MaxWidth = width >= 640 ? 130 : 72;
        Volume.Visibility = VolumeText.Visibility = width >= 780 ? Visibility.Visible : Visibility.Collapsed;
        PlaybackTime.Visibility = width >= 640 ? Visibility.Visible : Visibility.Collapsed;
    }
}
