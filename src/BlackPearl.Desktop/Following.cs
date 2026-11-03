using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Auto-download of new episodes (EpisodeFollowing in Core does the work): Auto-download new episodes on a show's page
// follows it, and Settings > Downloads turns the hourly check on or off, chooses between asking first and a countdown,
// and lists the episodes waiting for the user, the followed shows and what happened recently.
public partial class MainWindow
{
    private readonly DispatcherTimer downloadsRefresh = new() { Interval = TimeSpan.FromSeconds(15) };
    // The open app checks too, a few minutes after it starts and then hourly, in case Windows wouldn't run the task.
    private readonly DispatcherTimer followTimer = new() { Interval = TimeSpan.FromMinutes(3) };
    private bool followChecking;
    private string? downloadsKey, followRegistrationProblem, followNotificationProblem;

    private EpisodeFollowing FollowEngine() => BackgroundFollow.Engine(library, acquisitionPlugins, savedMetadataToken);
    private AcquisitionPlugin[] FollowPlugins(MediaItem member) => GuestLibrary ? [] : acquisitionPlugins.Where(x => x.Interactive && x.Supports(ContentAcquisition.ContentType(member.Kind))).ToArray();

    private void InitializeFollowing()
    {
        downloadsRefresh.Tick += (_, _) => { if (preferenceSection == "Downloads" && currentPage == "Settings") RenderDownloads(); else downloadsRefresh.Stop(); };
        followTimer.Tick += async (_, _) => { followTimer.Interval = TimeSpan.FromHours(1); await Guard(() => RunFollowCheck(false)); };
        if (!FollowSystem.Off) followTimer.Start();
        var settings = FollowStore.Settings(library);
        if (!settings.Enabled) return;
        // An installation that moved, or a task someone deleted, gets the hourly check back.
        _ = Task.Run(() =>
        {
            var problem = FollowSystem.Repair();
            if (problem is not null) { PlaybackDiagnostics.Record("follow-repair-failed", new { problem }); Dispatcher.BeginInvoke(() => followRegistrationProblem = problem); }
        });
        // Episodes found while the app was closed and still waiting for the user's OK.
        var waiting = FollowStore.Shows(library).Sum(x => x.Episodes.Count(e => e.Status == FollowStatus.Pending));
        if (waiting > 0)
            Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (closing || currentPage == "Settings" && preferenceSection == "Downloads") return;
                InAppDialog.Toast(this, $"{Count(waiting, "new episode")} {(waiting == 1 ? "is" : "are")} ready to download. Open Settings > Downloads to choose.", TimeSpan.FromSeconds(8));
            });
    }
    private static string Count(int count, string noun) => count + " " + noun + (count == 1 ? "" : "s");

    // A notification's Review button, or its body: the app comes to the front at Settings > Downloads.
    public void ShowDownloads()
    {
        if (closing) return;
        if (!IsLoaded) { RoutedEventHandler? once = null; once = (_, _) => { Loaded -= once; Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ShowDownloads); }; Loaded += once; return; }
        BringToFront();
        ShowPage("Downloads");
    }
    private void ShowDownloadsSection()
    {
        followNotificationProblem = FollowStore.Settings(library).Enabled ? FollowSystem.NotificationProblem() : null;
        RenderDownloads(force: true);
        downloadsRefresh.Start();
    }

    // The show page's button: follows the show, or offers to change or stop following it.
    private void ShowFollowButton()
    {
        if (FollowShowButton is null) return;
        var member = FollowMember();
        var followed = member is null ? null : FollowStore.Show(library, LibraryIdentity.ShowKey(member));
        FollowShowButton.Visibility = member is not null && (followed is not null || FollowPlugins(member).Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        FollowShowText.Text = followed is null ? "Auto-download new episodes" : "Auto-downloading new episodes";
        FollowShowIcon.Text = followed is null ? "" : "";
        FollowShowButton.ToolTip = followed is null ? "Find this show's new episodes as they air and download them after you say so, even while BlackPearl is closed"
            : $"New episodes of {followed.Title} are downloaded after you say so. Click to change or stop.";
        AutomationProperties.SetName(FollowShowButton, FollowShowText.Text);
    }
    private MediaItem? FollowMember()
    {
        if (GuestLibrary || browseSeries is null) return null;
        var members = items.Where(x => x.Series is not null && !x.IsMusic && LibraryIdentity.InShow(x, browseSeries, browseKind)).ToArray();
        return members.FirstOrDefault(x => !x.IsVirtual && !x.IsExtra && !x.IsBonus) ?? members.FirstOrDefault();
    }
    private async void FollowShowClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (FollowMember() is not { } member) return;
        var key = LibraryIdentity.ShowKey(member);
        if (FollowStore.Show(library, key) is not { } followed) { await FollowSetup(member, null); return; }
        var menu = new ContextMenu { PlacementTarget = FollowShowButton, Placement = PlacementMode.Bottom };
        MenuItem Entry(string header, Func<Task> run) { var entry = new MenuItem { Header = header }; entry.Click += async (_, _) => await Guard(run); menu.Items.Add(entry); return entry; }
        Entry("Change download source…", () => FollowSetup(member, followed));
        Entry("Open Downloads settings", () => { ShowPage("Downloads"); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        Entry("Stop auto-downloading", () => { StopFollowing(followed); return Task.CompletedTask; });
        FollowShowButton.ContextMenu = menu; menu.IsOpen = true;
    });

    // Follows a show, or changes where a followed show's episodes come from. The picker finds the show's releases with an
    // interactive plugin so the user can check the source; nothing downloads from it.
    private async Task FollowSetup(MediaItem member, FollowedShow? existing)
    {
        var plugins = FollowPlugins(member);
        var kind = ContentAcquisition.ContentTypeName(ContentAcquisition.ContentType(member.Kind)).ToLowerInvariant();
        if (plugins.Length == 0)
        {
            Dialogs.Alert(this, "Auto-download needs a plugin", $"Auto-download needs an acquisition plugin for {kind} that lists releases, such as the Sparrow plugins. Install one in Settings > Plugins.");
            return;
        }
        var key = LibraryIdentity.ShowKey(member);
        var show = items.Where(x => x.Series is not null && LibraryIdentity.ShowKey(x) == key).ToList();
        // The latest season that has started: an announced season has no releases to check the source against yet.
        var started = DateOnly.FromDateTime(DateTime.UtcNow);
        var season = show.Where(x => x.Season > 0 && (!x.IsVirtual || DateOnly.TryParse(x.AirDate, CultureInfo.InvariantCulture, out var aired) && aired <= started)).Select(x => x.Season!.Value)
            .DefaultIfEmpty(show.Where(x => x.Season > 0).Select(x => x.Season!.Value).DefaultIfEmpty(1).Min()).Max();
        var request = ContentAcquisition.ForEpisodes(member, season, [], items, folders);
        if (request is null) { Dialogs.Alert(this, "No folder to download into", $"Add a library folder for {kind} in Settings > Folders first, so downloads have somewhere to go."); return; }
        var initial = plugins.FirstOrDefault(x => x.Id == existing?.PluginId) ?? plugins.FirstOrDefault(x => x.Id == library.Setting(ContentAcquisition.ProviderKey(request)));
        var dialog = new AcquisitionPicker(this, plugins, request, initial, plugin => AcquisitionPreferences(plugin, request), existing: ContentAcquisition.ExistingNames(request, items), setup: true);
        if (dialog.ShowDialog() != true || dialog.Selection is not { } choice || choice.Request.Options is not { } options) return;
        library.Setting(AcquisitionPreferenceKey(choice.Plugin, request), Wire.Serialize(options)); library.Setting(ContentAcquisition.ProviderKey(request), choice.Plugin.Id);
        if (existing is not null)
        {
            // Episodes still being looked for are looked for again at once with the new source.
            FollowStore.Update(library, key, x => x with { PluginId = choice.Plugin.Id, Options = options, Problem = null, Episodes = x.Episodes.Select(e =>
                e.Status is FollowStatus.Waiting or FollowStatus.NotFound ? e with { Status = FollowStatus.Waiting, NextCheckAt = 0, Attempts = 0, SeenAt = Wire.Now, Message = null } : e).ToArray() });
            InAppDialog.Toast(this, $"{existing.Title} now downloads with {choice.Plugin.Name}.", TimeSpan.FromSeconds(6));
            ShowFollowButton(); await RunFollowCheck(false);
            return;
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var aired = show.Count(x => x.IsVirtual && x.Season == season && x.Episode is not null && DateOnly.TryParse(x.AirDate, CultureInfo.InvariantCulture, out var day) && day <= today);
        var seasonName = SeasonName(season, show);
        int? backlog = Dialogs.Confirm(this, "Get the episodes that are already out?",
            (aired > 0 ? $"{Count(aired, "episode")} of {seasonName} aired already and {(aired == 1 ? "isn't" : "aren't")} in your library. " : "") +
            $"BlackPearl can also look for the episodes of {seasonName} that aired before today. Either way it asks before downloading anything.",
            "Get them too", "Only new episodes") ? season : null;
        FollowStore.Follow(library, member, choice.Plugin.Id, options, backlog, today);
        var settings = FollowStore.Settings(library);
        if (!settings.Enabled) await SetFollowEnabled(true);
        InAppDialog.Toast(this, $"Auto-download is on for {member.Series}. BlackPearl checks every hour, even when it's closed, and " +
            (settings.Countdown ? $"tells you {settings.Minutes} minutes before it downloads." : "asks before it downloads."), TimeSpan.FromSeconds(8));
        ShowFollowButton(); await RunFollowCheck(false);
    }
    private void StopFollowing(FollowedShow show)
    {
        if (!Dialogs.Confirm(this, "Stop auto-downloading?", $"BlackPearl stops looking for new episodes of {show.Title}. Files already downloaded stay where they are.", "Stop auto-downloading")) return;
        foreach (var token in show.Episodes.Select(x => x.Token).Distinct()) FollowSystem.Remove(token);
        FollowStore.Remove(library, show.Key);
        SyncDesktopIcon(); ShowFollowButton(); RenderDownloads(force: true);
        SetStatus($"Stopped auto-downloading {show.Title}.");
    }
    private async Task SetFollowEnabled(bool enabled)
    {
        FollowStore.SaveSettings(library, FollowStore.Settings(library) with { Enabled = enabled });
        if (enabled)
        {
            followRegistrationProblem = await Task.Run(FollowSystem.Register);
            followNotificationProblem = FollowSystem.NotificationProblem();
        }
        else { followRegistrationProblem = null; await Task.Run(FollowSystem.Unregister); }
        SyncDesktopIcon(); RenderDownloads(force: true);
    }
    // The desktop icon follows what waits for the user; it's written off the UI thread.
    private void SyncDesktopIcon() { var engine = FollowEngine(); _ = Task.Run(() => DesktopIcon.Sync(engine)); }

    // Checks the followed shows now, as the hourly check would, and starts a countdown in this process when there is one.
    private async Task RunFollowCheck(bool manual)
    {
        if (followChecking || closing || !FollowStore.Settings(library).Enabled) return;
        followChecking = true; RenderDownloads(force: true);
        try
        {
            var engine = FollowEngine();
            var result = await Task.Run(() => engine.CheckAsync(lifetime.Token));
            if (closing) return;
            FollowSystem.Show(result.Ready); FollowSystem.Show(result.Attention); SyncDesktopIcon();
            if (result.Ready is { } ready) InAppDialog.Toast(this, ready.Heading + "\n" + string.Join("\n", ready.Lines) + "\n" + ready.Footer, TimeSpan.FromSeconds(10));
            else if (manual) SetStatus(result.Summary + ".");
            if (result.Ready is { StartAt: > 0 } countdown) _ = Countdown(engine, countdown);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { followChecking = false; if (!closing) RenderDownloads(force: true); }
    }
    private async Task Countdown(EpisodeFollowing engine, FollowNotice notice)
    {
        try { await Task.Run(() => BackgroundFollow.Countdown(engine, notice, lifetime.Token)); }
        catch (OperationCanceledException) { return; }
        if (closing) return;
        scanDelay.Stop(); scanDelay.Start(); RenderDownloads(force: true);
    }
    private async Task ApproveEpisodes(FollowEpisodeRef[] episodes, string[] tokens)
    {
        if (episodes.Length == 0) return;
        var engine = FollowEngine();
        SetStatus($"Starting {Count(episodes.Length, "download")}…");
        var outcomes = await Task.Run(() => engine.ApproveAsync(episodes, lifetime.Token));
        if (closing) return;
        foreach (var token in tokens) FollowSystem.Remove(token);
        if (EpisodeFollowing.OutcomeNotice(outcomes) is { } notice)
        {
            var text = notice.Heading + "\n" + string.Join("\n", notice.Lines);
            SetStatus(notice.Heading); InAppDialog.Toast(this, text, TimeSpan.FromSeconds(outcomes.All(x => x.Success) ? 6 : 10));
        }
        // A plugin that finished downloading leaves files for the next scan.
        if (outcomes.Any(x => x.Success)) { scanDelay.Stop(); scanDelay.Start(); }
        SyncDesktopIcon(); RenderDownloads(force: true);
    }
    private void SkipEpisodes(FollowEpisodeRef[] episodes, string[] tokens)
    {
        FollowEngine().Skip(episodes);
        foreach (var token in tokens) FollowSystem.Remove(token);
        SyncDesktopIcon(); RenderDownloads(force: true);
    }

    // Settings > Downloads. Rebuilt only when what it shows changed, so the refresh that picks up the background check's
    // work doesn't move focus.
    private void RenderDownloads(bool force = false)
    {
        if (DownloadsContent is null || preferenceSection != "Downloads") return;
        var settings = FollowStore.Settings(library); var shows = FollowStore.Shows(library); var checkedAt = FollowStore.LastCheck(library);
        var key = Wire.Serialize(new { settings, shows, checkedAt, followChecking, followRegistrationProblem, followNotificationProblem, plugins = acquisitionPlugins.Select(x => x.Id) });
        if (!force && key == downloadsKey) return;
        downloadsKey = key;
        DownloadsContent.Content = DownloadsPanel(settings, shows, checkedAt);
    }
    private FrameworkElement DownloadsPanel(FollowSettings settings, List<FollowedShow> shows, long checkedAt)
    {
        var page = new StackPanel();
        TextBlock Line(string text, bool muted = true, double size = 13)
        {
            var line = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (muted) line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return line;
        }
        TextBlock Warning(string text) { var line = Line(text, muted: false); line.SetResourceReference(TextBlock.ForegroundProperty, "Accent"); line.Margin = new Thickness(0, 10, 0, 0); return line; }
        StackPanel Card(string title, string name)
        {
            var panel = new StackPanel();
            var heading = new TextBlock { Text = title }; heading.SetResourceReference(StyleProperty, "CardTitle"); panel.Children.Add(heading);
            var border = new Border { Child = panel, Margin = new Thickness(0, 0, 0, 16) }; border.SetResourceReference(StyleProperty, "Card");
            AutomationProperties.SetName(border, name); page.Children.Add(border);
            return panel;
        }
        Button Action(Panel row, string text, Func<Task> run, bool primary = false, string? tip = null)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
            if (primary) button.SetResourceReference(StyleProperty, "Primary");
            button.Click += async (_, _) => await Guard(run);
            row.Children.Add(button); return button;
        }
        string When(long at) => DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime() is var local && local.Date == DateTime.Today
            ? local.ToString("t", CultureInfo.CurrentCulture) : local.ToString("MMM d, t", CultureInfo.CurrentCulture);
        DockPanel Row(string title, string detail, Action<StackPanel>? actions = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            if (actions is not null) { var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; actions(buttons); DockPanel.SetDock(buttons, Dock.Right); row.Children.Add(buttons); }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var name = Line(title, muted: false); name.FontWeight = FontWeights.SemiBold; texts.Children.Add(name);
            if (detail.Length > 0) texts.Children.Add(Line(detail, size: 12.5));
            row.Children.Add(texts); AutomationProperties.SetName(row, title);
            return row;
        }

        // The switch, how downloads start, and whether Windows will show the notifications.
        var main = Card("Auto-download new episodes", "Auto-download");
        var enabled = new CheckBox { IsChecked = settings.Enabled, Content = "Check the shows I follow for new episodes every hour, even when BlackPearl is closed" };
        enabled.Click += async (_, _) => await Guard(() => SetFollowEnabled(enabled.IsChecked == true));
        main.Children.Add(enabled);
        var about = Line("Follow a show with Auto-download new episodes on its page. BlackPearl looks for new episodes once they air, using the show's acquisition plugin, and downloads only the releases it showed you.");
        about.Margin = new Thickness(0, 6, 0, 0); main.Children.Add(about);
        var label = new TextBlock { Text = "Before downloading" }; label.SetResourceReference(StyleProperty, "FieldLabel"); label.Margin = new Thickness(0, 16, 0, 6); main.Children.Add(label);
        // A segmented choice, as elsewhere in Settings, with the countdown's length beside it when that's chosen.
        RadioButton Segment(string text, bool on) { var choice = new RadioButton { Content = text, IsChecked = on, GroupName = "FollowMode", IsEnabled = settings.Enabled }; choice.SetResourceReference(StyleProperty, "Segment"); return choice; }
        var ask = Segment("Ask me first", !settings.Countdown); var countdown = Segment("Download after a countdown", settings.Countdown);
        var segments = new WrapPanel(); segments.Children.Add(ask); segments.Children.Add(countdown);
        AutomationProperties.SetName(segments, "Before downloading");
        var frame = new Border { Child = segments, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
        frame.SetResourceReference(Border.BackgroundProperty, "Canvas"); frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        var minutes = new ComboBox { ItemsSource = FollowSettings.CountdownChoices.Select(x => $"{x} minutes").ToArray(), SelectedIndex = Array.IndexOf(FollowSettings.CountdownChoices, settings.Minutes),
            IsEnabled = settings.Enabled, Margin = new Thickness(12, 0, 0, 0), MinWidth = 130, VerticalAlignment = VerticalAlignment.Center,
            Visibility = settings.Countdown ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetName(minutes, "Countdown before downloading");
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        modeRow.Children.Add(frame); modeRow.Children.Add(minutes); main.Children.Add(modeRow);
        main.Children.Add(Line(settings.Countdown ? "The notification counts down; Skip or Review there stops the download." : "Notifications wait for you: nothing downloads until you choose Download."));
        var icon = new CheckBox { IsChecked = settings.DesktopIcon, IsEnabled = settings.Enabled, Margin = new Thickness(0, 14, 0, 0),
            Content = "Put an icon on the desktop when new episodes are ready" };
        icon.Click += (_, _) =>
        {
            FollowStore.SaveSettings(library, FollowStore.Settings(library) with { DesktopIcon = icon.IsChecked == true });
            SyncDesktopIcon(); RenderDownloads(force: true);
        };
        main.Children.Add(icon);
        var iconHint = Line("Opening the icon downloads the episodes it lists. It goes away once they're downloaded or skipped.");
        iconHint.Margin = new Thickness(28, 2, 0, 0); main.Children.Add(iconHint);
        void SaveMode() => FollowStore.SaveSettings(library, FollowStore.Settings(library) with { Countdown = countdown.IsChecked == true,
            CountdownMinutes = FollowSettings.CountdownChoices[Math.Max(0, minutes.SelectedIndex)] });
        ask.Checked += (_, _) => { SaveMode(); RenderDownloads(force: true); };
        countdown.Checked += (_, _) => { SaveMode(); RenderDownloads(force: true); };
        minutes.SelectionChanged += (_, _) => SaveMode();
        if (settings.Enabled && followRegistrationProblem is { } registration) main.Children.Add(Warning(registration));
        if (settings.Enabled && followNotificationProblem is { } notifications)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var open = new Button { Content = "Notification settings", VerticalAlignment = VerticalAlignment.Center };
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { }
            };
            DockPanel.SetDock(open, Dock.Right); row.Children.Add(open);
            var text = Warning(notifications + " Turn them on to hear about new episodes; they also wait here."); text.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(text);
            main.Children.Add(row);
        }
        var status = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var checkNow = new Button { Content = followChecking ? "Checking…" : "Check now", IsEnabled = settings.Enabled && !followChecking && shows.Count > 0, VerticalAlignment = VerticalAlignment.Center };
        checkNow.Click += async (_, _) => await Guard(() => RunFollowCheck(true));
        DockPanel.SetDock(checkNow, Dock.Right); status.Children.Add(checkNow);
        status.Children.Add(Line(!settings.Enabled ? "Off. Followed shows aren't checked." : checkedAt == 0 ? "Not checked yet." : $"Last checked {When(checkedAt)}. Next check within the hour."));
        main.Children.Add(status);

        // Episodes whose releases wait for the user.
        var pending = shows.SelectMany(s => s.Episodes.Where(e => e.Status == FollowStatus.Pending).Select(e => (Show: s, Episode: e))).ToArray();
        if (pending.Length > 0)
        {
            var ready = Card("Ready to download", "Ready to download");
            FollowEpisodeRef Ref((FollowedShow Show, FollowEpisode Episode) x) => new(x.Show.Key, x.Episode.Season, x.Episode.Episode);
            string[] Tokens(IEnumerable<(FollowedShow Show, FollowEpisode Episode)> x) => x.Select(y => y.Episode.Token).OfType<string>().Distinct().ToArray();
            if (pending.Length > 1)
            {
                var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                Action(all, $"Download all {pending.Length}", () => ApproveEpisodes(pending.Select(Ref).ToArray(), Tokens(pending)), primary: true);
                Action(all, "Skip all", () => { SkipEpisodes(pending.Select(Ref).ToArray(), Tokens(pending)); return Task.CompletedTask; });
                ready.Children.Add(all);
            }
            foreach (var item in pending)
            {
                var release = item.Episode.Releases?.FirstOrDefault()?.Title ?? "";
                var detail = release + (item.Episode.StartAt > 0 ? $" · downloads at {When(item.Episode.StartAt)}" : "");
                ready.Children.Add(Row($"{item.Show.Title} {item.Episode.Code}", detail, buttons =>
                {
                    Action(buttons, "Download", () => ApproveEpisodes([Ref(item)], Tokens([item])), primary: true, tip: "Download " + release);
                    Action(buttons, "Skip", () => { SkipEpisodes([Ref(item)], Tokens([item])); return Task.CompletedTask; }, tip: "Don't download this episode");
                }));
            }
        }

        // The followed shows.
        var followed = Card("Followed shows", "Followed shows");
        if (shows.Count == 0) followed.Children.Add(Line("None yet. Open a show and choose Auto-download new episodes."));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var show in shows)
        {
            var plugin = EpisodeFollowing.PluginFor(acquisitionPlugins, show);
            var looking = show.Episodes.Where(e => e.Status == FollowStatus.Waiting).ToArray();
            var next = (show.Listing ?? []).Where(x => DateOnly.TryParse(x.AirDate, CultureInfo.InvariantCulture, out var day) && day > today)
                .OrderBy(x => x.AirDate, StringComparer.Ordinal).FirstOrDefault();
            var parts = new List<string> { plugin is null ? "Plugin not installed" : "With " + plugin.Name };
            if (looking.Length > 0) parts.Add($"looking for {string.Join(", ", looking.Take(3).Select(x => x.Code))}" + (looking.Length > 3 ? $" and {looking.Length - 3} more" : ""));
            if (next is not null && DateOnly.TryParse(next.AirDate, CultureInfo.InvariantCulture, out var airs))
                parts.Add($"next: S{next.Season:00}E{next.Episode:00} on {airs.ToString("MMM d", CultureInfo.CurrentCulture)}");
            else if (looking.Length == 0 && show.Listing is not null) parts.Add("no new episodes announced");
            var row = Row(show.Title, string.Join(" · ", parts), buttons =>
            {
                Action(buttons, "Change source…", async () =>
                {
                    if (items.Where(x => x.Series is not null && LibraryIdentity.ShowKey(x) == show.Key).OrderBy(x => x.IsVirtual).FirstOrDefault() is { } member) await FollowSetup(member, show);
                    else Dialogs.Alert(this, show.Title, "This show isn't in your library any more.");
                });
                Action(buttons, "Stop", () => { StopFollowing(show); return Task.CompletedTask; }, tip: "Stop auto-downloading " + show.Title);
            });
            followed.Children.Add(row);
            if (show.Problem is { } problem) { var warning = Warning(problem); warning.Margin = new Thickness(0, -2, 0, 6); followed.Children.Add(warning); }
        }

        // What happened lately.
        var recent = shows.SelectMany(s => s.Episodes.Where(e => e.Status is not (FollowStatus.Waiting or FollowStatus.Pending) && e.UpdatedAt > Wire.Now - (long)TimeSpan.FromDays(30).TotalMilliseconds)
            .Select(e => (Show: s, Episode: e))).OrderByDescending(x => x.Episode.UpdatedAt).Take(12).ToArray();
        if (recent.Length > 0)
        {
            var history = Card("Recent", "Recent downloads");
            foreach (var (show, episode) in recent)
            {
                var detail = episode.Status switch
                {
                    FollowStatus.Downloading => "Starting the download…",
                    FollowStatus.Sent => $"Sent to the plugin {When(episode.SentAt)}; it joins your library when the download finishes",
                    FollowStatus.Arrived => "In your library",
                    FollowStatus.Skipped => "Skipped",
                    FollowStatus.Failed => "Couldn't start: " + episode.Message,
                    FollowStatus.NotFound => episode.Message ?? "No release found",
                    FollowStatus.Stalled => episode.Message ?? "Hasn't arrived yet",
                    _ => episode.Status
                };
                var retry = episode.Status is FollowStatus.Failed or FollowStatus.NotFound or FollowStatus.Stalled or FollowStatus.Skipped;
                history.Children.Add(Row($"{show.Title} {episode.Code}", detail, retry ? buttons => Action(buttons, "Look again", async () =>
                {
                    FollowEngine().Retry(new(show.Key, episode.Season, episode.Episode));
                    RenderDownloads(force: true); await RunFollowCheck(false);
                }, tip: "Look for this episode's releases again") : null));
            }
        }
        return page;
    }
}
