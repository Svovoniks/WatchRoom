using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    // Set while the queue menu's switch is on Queue but no queue has been chosen yet.
    private bool queueModeRequested;
    private static readonly SolidColorBrush statusIdle = QueueRowConverter.Frozen(0x6B, 0x6D, 0x7A), statusLive = QueueRowConverter.Frozen(0x3F, 0xB9, 0x7A), statusWaiting = QueueRowConverter.Frozen(0xE0, 0xA8, 0x4A);

    private void InitializePlayerChrome()
    {
        QueueRowConverter.Resolve = QueueRowValue;
        // LibVLC moves the video overlay into its own window, outside this window's name scope,
        // so overlay controls are linked by source rather than ElementName.
        RoomHeader.SetBinding(VisibilityProperty, new Binding(nameof(Visibility)) { Source = PlaybackControls });
        // A narrow header shows only the status dot, so the status reads from its tooltip.
        RoomStatusChip.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = ConnectionStatus });
        // The timeline gives the episode pill the output pill's column while it shows.
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement)).AddValueChanged(EpisodeEndButton,
            (_, _) => Grid.SetColumnSpan(Timeline, EpisodeEndButton.Visibility == Visibility.Visible ? 2 : 3));
        PlaybackControls.IsVisibleChanged += (_, _) => PlaceUpNextCard();
        PlayerBottomChrome.SizeChanged += (_, _) => PlaceUpNextCard();
        PlayerOverlay.SizeChanged += (_, _) => RefreshPlayerCorners();
        InitializeSubtitleLift();
        PlayerPills.Overflow = OverflowPill;
        PlayerPills.FoldedChanged += (_, _) => RefreshOverflowMenu();
        // The participants menu sits in LibVLC's overlay window, outside the player page that maps the app palette to dark.
        ((FrameworkElement)ParticipantsMenu.Child).Resources.MergedDictionaries.Add(RoomPage.Resources);
        VolumeFlyout.PlacementTarget = MuteButton;
        MuteButton.GotKeyboardFocus += (_, _) => { if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) ShowVolumeFlyout(); };
        foreach (var (pill, menu) in new[] { (AudioPill, AudioMenu), (SubtitlePill, SubtitleMenu), (QueuePill, PlayerQueueMenu), (ParticipantsPill, ParticipantsMenu), (OverflowPill, OverflowMenu) })
        {
            menu.PlacementTarget = pill;
            // A folded pill's menu opens from the overflow pill, so a click there only closes it.
            menu.Dismissing += (_, _) => OpenPillUnderPointer(menu.PlacementTarget as System.Windows.Controls.Primitives.ToggleButton ?? pill);
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

    // An open menu holds the mouse, so the press that dismisses it never reaches another pill. When that
    // press lands on a different pill, open its menu once the button is released over it, as a normal
    // click would; opening on the press would let the release dismiss the new menu. The release may be
    // delivered to the closing menu or to no WPF element at all, so watch the button itself.
    private DispatcherTimer? pillReleaseWatch;
    private void OpenPillUnderPointer(System.Windows.Controls.Primitives.ToggleButton closing)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        var pill = new[] { AudioPill, SubtitlePill, QueuePill, ParticipantsPill, OverflowPill }.FirstOrDefault(candidate =>
            candidate != closing && candidate.IsVisible && candidate.IsEnabled && new Rect(candidate.RenderSize).Contains(Mouse.GetPosition(candidate)));
        if (pill is null) return;
        pillReleaseWatch?.Stop();
        var button = SystemParameters.SwapButtons ? 0x02 : 0x01; // The primary button, as a physical key.
        var deadline = Environment.TickCount64 + 3000;
        var watch = pillReleaseWatch = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        watch.Tick += (_, _) =>
        {
            if ((GetAsyncKeyState(button) & 0x8000) != 0 && Environment.TickCount64 < deadline) return;
            watch.Stop();
            if (Environment.TickCount64 >= deadline || !pill.IsVisible || !GetCursorPos(out var cursor)) return;
            if (new Rect(pill.RenderSize).Contains(pill.PointFromScreen(new Point(cursor.X, cursor.Y)))) pill.IsChecked = true;
        };
        watch.Start();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
    private struct ScreenPoint { public int X, Y; }

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
        // Bring back the last queue used; otherwise show the picker for the user to open.
        var last = savedQueues.FirstOrDefault(saved => saved.Id == library.Setting("lastQueue"));
        if (last is not null && ResolveQueue(last).Any()) { SwitchToQueue(last); return; }
        RefreshQueuePickers(); UpdateSessionControls();
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

    // The empty player's prompt stays between the two bars rather than under them.
    private void FitPlayerEmptyContent() => PlayerEmptyContent.Margin = new Thickness(16,
        Math.Max(16, RoomHeader.ActualHeight - RoomHeader.Padding.Bottom + 8), 16,
        Math.Max(16, PlayerBottomChrome.ActualHeight - PlaybackControls.Padding.Top + 8));

    // The up-next card sits centred just above the dock and drops to the dock's place when the dock hides.
    // It comes before the controls in their stack, so it reaches down over their fade with a negative margin.
    private void PlaceUpNextCard()
    {
        var padding = PlaybackControls.Padding;
        var margin = new Thickness(0, 0, 0, PlaybackControls.IsVisible ? 10 - padding.Top : padding.Bottom);
        if (UpNextCard.Margin != margin) UpNextCard.Margin = margin;
    }

    // Windows rounds the window but not LibVLC's overlay window above the video, so the overlay rounds itself to match,
    // except where Windows draws square corners: maximized and fullscreen.
    private const double WindowCornerRadius = 8;
    private void RefreshPlayerCorners()
    {
        var round = !fullscreen && (pictureInPicture || WindowState == WindowState.Normal);
        var size = PlayerOverlay.RenderSize;
        if (!round || size.Width <= 0 || size.Height <= 0) { PlayerOverlay.Clip = null; return; }
        var clip = new RectangleGeometry(new Rect(size), WindowCornerRadius, WindowCornerRadius); clip.Freeze();
        PlayerOverlay.Clip = clip;
    }

    // Short players tighten both bars and drop the status line under the title, so the bars don't meet over the video.
    private int playerHeightStep = -1;
    private void VideoFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;
        var step = height >= 420 ? 2 : height >= 300 ? 1 : 0;
        if (step == playerHeightStep) return;
        playerHeightStep = step;
        PlaybackControls.Padding = step == 2 ? new Thickness(16, 40, 16, 16) : new Thickness(10, 12, 10, 8);
        // The top padding centres the header's pills on the window buttons; only the fade below them shrinks.
        RoomHeader.Padding = step == 2 ? new Thickness(9, 9, 14, 24) : new Thickness(9, 9, 14, 10);
        RoomHeaderDetails.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        // The window buttons stay flush with the window's top-right corner.
        PlayerCaptionButtons.Margin = new Thickness(8, -RoomHeader.Padding.Top, -RoomHeader.Padding.Right, 0);
    }

    // Narrow headers keep only the icons of the room buttons and the status dot, and shorten Leave; the narrowest keep only the
    // close button of the window buttons, as double-clicking the bar still maximizes.
    private bool roomHeaderCompact;
    private void RoomHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitPlayerEmptyContent();
        var tiny = e.NewSize.Width < 560;
        PlayerMinimizeButton.Visibility = PlayerMaximizeButton.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
        var compact = e.NewSize.Width < 720;
        foreach (var button in new[] { PlayerMinimizeButton, PlayerMaximizeButton, PlayerCloseButton }) button.Width = compact ? 40 : 46;
        // Voice gives up its label first, as it sits among more room buttons.
        var voiceLabel = e.NewSize.Width >= 1000;
        VoiceButtonText.Visibility = voiceLabel ? Visibility.Visible : Visibility.Collapsed;
        VoiceButtonIcon.Margin = new Thickness(0, 0, voiceLabel ? 7 : 0, 0);
        if (compact == roomHeaderCompact) return;
        roomHeaderCompact = compact;
        ConnectionStatus.Visibility = SharedControlsText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RoomStatusDot.Margin = new Thickness(0, 0, compact ? 0 : 7, 0);
        SharedControlsIcon.Margin = new Thickness(0, 0, compact ? 0 : 6, 0);
        RefreshLeaveButton();
    }
    // A title squeezed to a letter or two says nothing, so it steps aside; its column keeps the space either way.
    private void RoomTitleSizeChanged(object sender, SizeChangedEventArgs e) =>
        ((UIElement)sender).Visibility = e.NewSize.Width < 72 ? Visibility.Hidden : Visibility.Visible;
    private void RefreshLeaveButton()
    {
        var label = room is null ? "Close player" : "Leave room";
        LeaveButton.Content = roomHeaderCompact ? room is null ? "Close" : "Leave" : label;
        System.Windows.Automation.AutomationProperties.SetName(LeaveButton, label);
    }

    // Narrow bars drop the pills' details first, then shorten their labels, then fold the stop and
    // picture-in-picture buttons into the … pill and hide the time. Pills that still don't fit fold too.
    private int controlsDensity = -1;
    private void PlaybackControlsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        FitPlayerEmptyContent();
        UpNextCard.Width = Math.Clamp(width - 16, 240, 340);
        // Resizing fires this continuously; only re-lay out the bar when it crosses a density step.
        var density = width >= 900 ? 6 : width >= 640 ? 5 : width >= 580 ? 4 : width >= 540 ? 3 : width >= 520 ? 2 : width >= 440 ? 1 : 0;
        if (density == controlsDensity) return;
        controlsDensity = density;
        foreach (var detail in new[] { AudioPillDetail, SubtitlePillDetail, QueuePillDetail, ParticipantsPillDetail })
        { detail.MaxWidth = width >= 900 ? 96 : 0; detail.Margin = new Thickness(width >= 900 ? 6 : 0, 0, 0, 0); }
        foreach (var text in new[] { AudioPillText, SubtitlePillText, QueuePillText, ParticipantsPillText }) text.MaxWidth = width >= 640 ? 130 : 72;
        LoopButton.Visibility = width >= 580 ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = width >= 540 ? Visibility.Visible : Visibility.Collapsed;
        PictureInPictureButton.Visibility = width >= 520 ? Visibility.Visible : Visibility.Collapsed;
        PlaybackTime.Visibility = PlaybackTimePill.Visibility = width >= 440 ? Visibility.Visible : Visibility.Collapsed;
        RefreshOverflowMenu();
    }

    private (System.Windows.Controls.Primitives.ToggleButton Pill, PlayerMenuPopup Menu, TextBlock Icon, TextBlock Text, TextBlock Detail)[] PillMenus =>
        [(AudioPill, AudioMenu, AudioPillIcon, AudioPillText, AudioPillDetail), (SubtitlePill, SubtitleMenu, SubtitlePillIcon, SubtitlePillText, SubtitlePillDetail),
         (QueuePill, PlayerQueueMenu, QueuePillIcon, QueuePillText, QueuePillDetail), (ParticipantsPill, ParticipantsMenu, ParticipantsPillIcon, ParticipantsPillText, ParticipantsPillDetail)];

    // The … menu lists the folded pills, each opening its own menu from the … pill, then any bar buttons hidden for width.
    private void RefreshOverflowMenu()
    {
        OverflowItems.Children.Clear();
        foreach (var (pill, menu, icon, text, detail) in PillMenus)
        {
            var folded = PlayerPills.Folded.Contains(pill);
            menu.PlacementTarget = folded ? OverflowPill : pill;
            if (!folded) continue;
            var row = OverflowRow(icon, text, detail, pill, true);
            row.Click += (_, _) => { OverflowMenu.IsOpen = false; Dispatcher.BeginInvoke(() => menu.IsOpen = true, DispatcherPriority.Input); };
            OverflowItems.Children.Add(row);
        }
        var actions = new List<Button>();
        foreach (var (button, label, action) in new (System.Windows.Controls.Primitives.ButtonBase, string, RoutedEventHandler)[] { (StopButton, "Stop", StopPlayback), (LoopButton, "Loop", ToggleLoop), (PictureInPictureButton, "Picture in picture", EnterPictureInPicture) })
        {
            if (button.Visibility == Visibility.Visible) continue;
            var row = OverflowRow(button.Content, label, null, button, false);
            row.Click += (sender, e) => { OverflowMenu.IsOpen = false; action(sender, e); };
            actions.Add(row);
        }
        if (actions.Count > 0 && OverflowItems.Children.Count > 0) OverflowItems.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("PlayerLine"), Margin = new Thickness(6, 4, 6, 4) });
        foreach (var row in actions) OverflowItems.Children.Add(row);
        PlayerPills.ShowsOverflow = actions.Count > 0;
        if (OverflowItems.Children.Count == 0) OverflowMenu.CloseNow();
    }
    // A row in the … menu: icon, label and muted detail, mirroring the pill or button it stands in for.
    private Button OverflowRow(object icon, object text, TextBlock? detail, UIElement source, bool submenu)
    {
        static TextBlock Label(object value, TextBlock block)
        {
            if (value is TextBlock mirrored) block.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = mirrored });
            else block.Text = value as string;
            return block;
        }
        var muted = (Brush)FindResource("PlayerMuted");
        var icons = (FontFamily)FindResource("IconFont");
        var grid = new Grid { ColumnDefinitions = { new() { Width = new GridLength(26) }, new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto }, new() { Width = GridLength.Auto } } };
        grid.Children.Add(Label(icon, new TextBlock { FontFamily = icons, FontSize = 13, Foreground = muted, VerticalAlignment = VerticalAlignment.Center }));
        var title = Label(text, new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        if (detail is not null)
        {
            var extra = Label(detail, new TextBlock { Foreground = muted, FontSize = 11.5, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(extra, 2); grid.Children.Add(extra);
        }
        if (submenu)
        {
            var chevron = new TextBlock { Text = "", FontFamily = icons, FontSize = 9, Foreground = muted, Margin = new Thickness(12, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 3); grid.Children.Add(chevron);
        }
        var row = new Button { Content = grid, Style = (Style)FindResource("PlayerMenuButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        row.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = source });
        System.Windows.Automation.AutomationProperties.SetName(row, text as string ?? System.Windows.Automation.AutomationProperties.GetName(source));
        return row;
    }

    // The volume slider stands above the speaker while the pointer is over either, and briefly after the volume changes.
    private DispatcherTimer? volumeFlyoutTimer;
    private void ShowVolumeFlyout()
    {
        if (!MuteButton.IsVisible) return;
        VolumeFlyout.IsOpen = true;
        ScheduleVolumeFlyoutHide(1200);
    }
    private void ScheduleVolumeFlyoutHide(int milliseconds)
    {
        if (volumeFlyoutTimer is null)
        {
            var timer = volumeFlyoutTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
            timer.Tick += (_, _) =>
            {
                // Over the speaker or the slider, or dragging the slider, keeps it up; check again shortly.
                if (VolumeGroup.IsMouseOver || Volume.IsMouseCaptureWithin) { timer.Interval = TimeSpan.FromMilliseconds(350); return; }
                timer.Stop();
                VolumeFlyout.IsOpen = false;
            };
        }
        volumeFlyoutTimer.Stop();
        volumeFlyoutTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        volumeFlyoutTimer.Start();
    }
    private void HideVolumeFlyout() { volumeFlyoutTimer?.Stop(); VolumeFlyout.IsOpen = false; }
    private void VolumeGroupEntered(object sender, MouseEventArgs e) => ShowVolumeFlyout();
    private void VolumeGroupLeft(object sender, MouseEventArgs e) { if (VolumeFlyout.IsOpen) ScheduleVolumeFlyoutHide(350); }
    private void VolumeFlyoutOpened(object? sender, EventArgs e)
    {
        // Like the player menus, the flyout draws over the native video.
        if (VolumeFlyout.Child is { } child) App.UseSoftwareSurface(child);
    }
    // The speaker shows more waves as the volume rises; the level reads beside it.
    private void RefreshVolumeIndicator()
    {
        var muted = player?.Mute == true;
        var level = (int)Volume.Value;
        MuteButton.Content = muted ? "" : level == 0 ? "" : level < 34 ? "" : level < 67 ? "" : "";
        MuteButton.ToolTip = muted ? "Unmute (M)" : "Mute (M)";
        System.Windows.Automation.AutomationProperties.SetName(MuteButton, muted ? "Unmute" : "Mute");
        VolumeText.Text = muted ? "Muted" : $"{level}%";
    }
}
