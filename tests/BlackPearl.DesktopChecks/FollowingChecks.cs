using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Auto-download in the app: the show page's button, and Settings > Downloads with the switch, how downloads start, the
// episodes waiting for the user, the followed shows and recent activity. Windows registration is off for the checks
// (BLACKPEARL_FOLLOW_SYSTEM=off); --follow-system-probe exercises it.
static class FollowingChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethods(flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(window, args);
        void Until(Func<bool> done)
        {
            for (var until = DateTime.UtcNow.AddSeconds(10); !done() && DateTime.UtcNow < until;)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
        }
        void Await(object? task) { var pending = (Task)task!; Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
        IEnumerable<T> Logical<T>(object root) where T : DependencyObject
        {
            if (root is not DependencyObject node) yield break;
            if (node is T match) yield return match;
            foreach (var child in LogicalTreeHelper.GetChildren(node)) foreach (var nested in Logical<T>(child)) yield return nested;
        }
        var content = (ContentControl)window.FindName("DownloadsContent");
        string[] Texts() => Logical<TextBlock>(content.Content).Select(x => x.Text).ToArray();
        Button ButtonNamed(string text, int index = 0) => Logical<Button>(content.Content).Where(x => x.Content as string == text).ElementAt(index);
        string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");

        // A show with files, and an interactive plugin that gets shows.
        var root = new LibraryFolder(Path.Combine(directory, "Follow Shows"), "Show");
        var folders = (System.Collections.ObjectModel.ObservableCollection<LibraryFolder>)typeof(MainWindow).GetField("folders", flags)!.GetValue(window)!; folders.Insert(0, root);
        var folder = Path.Combine(root.Path, "Follow Check Show"); Directory.CreateDirectory(folder);
        var file = new MediaItem("follow-check-1", Path.Combine(folder, "Follow Check Show S01E01.mkv"), "Follow Check Show", "Show", 2024, "Follow Check Show", 1, 1,
            ShowId: "show-follow-check", SeriesPath: folder, MetadataProvider: "tmdb", MetadataId: 9301);
        store.Save(file);
        var pluginsField = typeof(MainWindow).GetField("acquisitionPlugins", flags)!;
        var previousPlugins = (List<AcquisitionPlugin>)pluginsField.GetValue(window)!;
        var plugin = new AcquisitionPlugin("follow-provider", "Follow provider", "", ["show"], "unused.exe", [], directory, 30, Interactive: true);
        pluginsField.SetValue(window, new List<AcquisitionPlugin>(previousPlugins) { plugin, plugin with { Id = "plain", Name = "Plain", Interactive = false } });
        Await(Call("RefreshLibrary"));

        Call("NavigateLibrary", 2, "show-follow-check", "Show", null);
        var follow = (Button)window.FindName("FollowShowButton"); var followText = (TextBlock)window.FindName("FollowShowText");
        Until(() => follow.Visibility == Visibility.Visible);
        check(follow.Visibility == Visibility.Visible && followText.Text == "Auto-download new episodes", "a show offers auto-download when a plugin can list its releases");
        pluginsField.SetValue(window, new List<AcquisitionPlugin> { plugin with { Id = "plain", Name = "Plain", Interactive = false } });
        Call("ShowAcquireSeason");
        check(follow.Visibility == Visibility.Collapsed, "without a plugin that lists releases, a show doesn't offer auto-download");
        pluginsField.SetValue(window, new List<AcquisitionPlugin>(previousPlugins) { plugin });

        // Following it: the button says so, and Downloads lists the show and the episode waiting for the user.
        FollowStore.SaveSettings(store, new FollowSettings(Enabled: true));
        var token = EpisodeFollowing.NewToken();
        FollowStore.Follow(store, file, "follow-provider", new AcquisitionOptions("Follow Check Show"), null, DateOnly.FromDateTime(DateTime.UtcNow));
        FollowStore.Update(store, "show-follow-check", x => x with
        {
            Listing = [new(1, 2, Day(-1)), new(1, 3, Day(6))], ListedAt = Wire.Now,
            Episodes = [new FollowEpisode(1, 2, Day(-1), FollowStatus.Pending, Wire.Now, Releases: [new("release-2", "Follow.Check.Show.S01E02.1080p-GRP")], Token: token, UpdatedAt: Wire.Now)]
        });
        Call("ShowAcquireSeason");
        check(followText.Text == "Auto-downloading new episodes", "a followed show's button says it auto-downloads");

        window.ShowDownloads();
        Until(() => content.Content is not null);
        var page = (FrameworkElement)window.FindName("DownloadsPreferences");
        check(page.Visibility == Visibility.Visible, "a notification's Review opens Settings > Downloads");
        var texts = Texts();
        check(texts.Contains("Ready to download") && texts.Contains("Follow Check Show S01E02") && texts.Contains("Follow.Check.Show.S01E02.1080p-GRP"),
            "Downloads lists the episode waiting for the user with the release it would download");
        check(texts.Contains("Followed shows") && texts.Any(x => x.StartsWith("With Follow provider") && x.Contains("next: S01E03")), "Downloads lists the followed show, its plugin and its next episode");
        var enabled = Logical<CheckBox>(content.Content).First();
        check(enabled.IsChecked == true && Logical<RadioButton>(content.Content).First(x => (string)x.Content == "Ask me first").IsChecked == true, "auto-download is on and asks first");

        // A countdown notification's Review link pauses the countdown; the app then opens at Downloads.
        FollowStore.Update(store, "show-follow-check", x => x with { Episodes = [x.Episodes[0] with { StartAt = Wire.Now + 300_000 }] });
        check(!BackgroundFollow.Activate(FollowNotifications.Link(FollowSystem.Scheme, "open", token)) &&
            FollowStore.Show(store, "show-follow-check")!.Episodes[0] is { Status: FollowStatus.Pending, StartAt: 0 }, "a notification's Review pauses its countdown and opens the app");

        // Choosing a countdown saves it.
        var countdown = Logical<RadioButton>(content.Content).First(x => (string)x.Content == "Download after a countdown");
        countdown.IsChecked = true;
        Until(() => FollowStore.Settings(store).Countdown);
        check(FollowStore.Settings(store) is { Countdown: true, CountdownMinutes: 5 }, "choosing a countdown saves it");
        Logical<RadioButton>(content.Content).First(x => (string)x.Content == "Ask me first").IsChecked = true;
        Until(() => !FollowStore.Settings(store).Countdown);

        // The desktop icon: on for the waiting episode, opening this app with the episode's download link.
        var desktop = Environment.GetEnvironmentVariable("BLACKPEARL_DESKTOP")!;
        string[] Icons() => Directory.GetFiles(desktop, "*.lnk");
        var iconBox = Logical<CheckBox>(content.Content).First(x => (x.Content as string)?.Contains("icon on the desktop") == true);
        check(iconBox.IsChecked == false && Icons().Length == 0, "the desktop icon is off by default");
        iconBox.IsChecked = true; iconBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Until(() => Icons().Length == 1);
        check(FollowStore.Settings(store).DesktopIcon && Icons() is [var icon] && Path.GetFileName(icon).StartsWith("1 new episode - BlackPearl"),
            "with the option on, a waiting episode puts an icon on the desktop that says how many are waiting");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(Icons()[0]);
        string target = link.TargetPath, arguments = link.Arguments, tip = link.Description, picture = link.IconLocation;
        var expectedPicture = Path.Combine(AppContext.BaseDirectory, "Assets", "NewEpisodes.ico");
        var ownPicture = picture.EndsWith(Path.Combine("Assets", "NewEpisodes.ico") + ",0", StringComparison.OrdinalIgnoreCase) && File.Exists(picture[..^2]);
        if (!ownPicture) Console.WriteLine($"Desktop icon picture: '{picture}'; expected '{expectedPicture},0' (exists: {File.Exists(expectedPicture)}); process: '{Environment.ProcessPath}'");
        check(ownPicture, "the desktop icon has its own new-episodes picture");
        check(string.Equals(target, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && arguments.Contains("--data-dir") && arguments.Contains(":desktop?token=") &&
            tip == "Download Follow Check Show S01E02", "opening the icon starts BlackPearl with a link that downloads the episodes it lists");
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);

        // Skip from the list: nothing waits any more, so the icon goes away.
        ButtonNamed("Skip").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Until(() => FollowStore.Show(store, "show-follow-check")!.Episodes[0].Status == FollowStatus.Skipped && Icons().Length == 0);
        check(Icons().Length == 0, "the icon goes away once its episodes are skipped or downloaded");
        texts = Texts();
        check(FollowStore.Show(store, "show-follow-check")!.Episodes[0].Status == FollowStatus.Skipped && !texts.Contains("Ready to download") && texts.Contains("Recent") &&
            texts.Contains("Skipped") && Logical<Button>(content.Content).Any(x => x.Content as string == "Look again"), "Skip moves the episode to Recent, where it can be looked for again");

        // The switch turns the hourly check off.
        var toggle = Logical<CheckBox>(content.Content).First();
        toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Until(() => !FollowStore.Settings(store).Enabled && Texts().Contains("Off. Followed shows aren't checked."));
        check(!FollowStore.Settings(store).Enabled && Texts().Contains("Off. Followed shows aren't checked."), "the switch turns auto-download off");

        FollowStore.Remove(store, "show-follow-check"); FollowStore.SaveSettings(store, new FollowSettings());
        pluginsField.SetValue(window, previousPlugins);
        store.Save(file with { Available = false }); store.RemoveUnavailable([file.Id]); folders.Remove(root);
        Await(Call("RefreshLibrary"));
        Call("ShowPage", "Library");
    }
}
