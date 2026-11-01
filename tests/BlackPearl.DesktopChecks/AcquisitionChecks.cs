using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Acquisition plugins in the app: the Folders page lists them, and missing episodes, seasons and films offer Acquire
// when a plugin gets their kind, handing the plugin what to get and where it belongs.
static class AcquisitionChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethods(flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(window, args);
        void Until(Func<bool> done)
        {
            for (var until = DateTime.UtcNow.AddSeconds(15); !done() && DateTime.UtcNow < until;)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
        }
        void Await(object? task) { var pending = (Task)task!; Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult(); }
        string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");
        MetadataSeason Season(int number, params int[] offsets) => new(number, null, null, null, null, offsets.Select((o, i) => new MetadataEpisode(i + 1, $"Chapter {i + 1}", null, null, Day(o), 24)).ToArray());

        // One plugin that gets shows; it keeps the last request it was given.
        var plugins = Path.Combine(directory, "plugins", "show-getter");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "plugin.json"), """{ "name": "Show getter", "description": "Check plugin", "contentTypes": ["show"], "command": "get.cmd" }""");
        File.WriteAllText(Path.Combine(plugins, "get.cmd"), "@echo off\r\ncopy /y \"%BLACKPEARL_REQUEST_FILE%\" \"%BLACKPEARL_PLUGIN_DIR%\\last.json\" >nul\r\necho {\"success\": true, \"message\": \"Queued\"}\r\n");
        var folders = (ObservableCollection<LibraryFolder>)typeof(MainWindow).GetField("folders", flags)!.GetValue(window)!;
        var shows = Path.Combine(directory, "Acquire Shows"); Directory.CreateDirectory(shows);
        var folder = new LibraryFolder(shows, "Show"); folders.Insert(0, folder);
        Call("LoadAcquisitionPlugins");
        var list = (ItemsControl)window.FindName("AcquisitionPluginList");
        check(list.Items.Count == 1 && list.Items[0]!.ToString()!.Contains("Show getter"), "the Folders page lists installed acquisition plugins");

        var show = WantedTitles.Add(store, directory, WantedTitles.FromMatch(new(9201, "tv", "Acquire Check Show", "2022", null, "", "Show"), "Show", [1], false, null),
            [Season(1, -200, -193, 30)]);
        var film = WantedTitles.Add(store, directory, WantedTitles.FromMatch(new(9202, "movie", "Acquire Check Film", "2020", null, "", "Movie", ReleaseDate: Day(-90)), "Movie", [], false, null), []);
        Await(Call("RefreshLibrary"));

        var episode = store.All().Where(x => LibraryIdentity.ShowKey(x) == show.Id && x.IsVirtual).OrderBy(x => x.Episode).First();
        Call("Select", episode);
        var acquire = (Button)window.FindName("DetailAcquire");
        check(acquire.Visibility == Visibility.Visible && (acquire.ToolTip as string)?.Contains("Show getter") == true, "a missing episode offers Acquire when a plugin gets shows");
        var last = Path.Combine(plugins, "last.json"); File.Delete(last);
        Call("AcquireDetailClick", acquire, new RoutedEventArgs());
        // The plugin may still be writing its copy while the file exists; wait until the app has its result.
        var acquiring = (HashSet<string>)typeof(MainWindow).GetField("acquiring", flags)!.GetValue(window)!;
        Until(() => File.Exists(last) && acquiring.Count == 0);
        using (var request = JsonDocument.Parse(File.ReadAllText(last)))
        {
            var root = request.RootElement;
            check(root.GetProperty("scope").GetString() == "episode" && root.GetProperty("name").GetString() == "Acquire Check Show S01E01" &&
                root.GetProperty("targetDirectory").GetString() == Path.Combine(shows, "Acquire Check Show (2022)", "Season 01") &&
                root.GetProperty("libraries").EnumerateArray().Any(x => x.GetProperty("path").GetString() == shows && x.GetProperty("kind").GetString() == "show"),
                "Acquire hands the plugin the episode, its season folder and the library folders");
        }

        Call("Select", store.Get(film.Id)!);
        check(acquire.Visibility == Visibility.Collapsed, "a film offers no Acquire when no plugin gets films");

        Call("NavigateLibrary", 2, show.Id, "Show", 1);
        var seasonButton = (Button)window.FindName("AcquireSeasonButton");
        Until(() => seasonButton.Visibility == Visibility.Visible);
        check(seasonButton.Visibility == Visibility.Visible && (seasonButton.Content as string) == "Acquire season · 3 episodes", "a season with missing episodes offers to acquire them");

        PickerChecks(window, directory, ContentAcquisition.ForSeason(episode, 1, store.All(), folders)!, check);

        foreach (var title in new[] { show, film }) WantedTitles.Remove(store, directory, store.Wanted(title.Id)!);
        folders.Remove(folder);
        Directory.Delete(plugins, true); Call("LoadAcquisitionPlugins");
        Await(Call("RefreshLibrary"));
        Call("ShowPage", "Library");
    }

    // A downloader settings schema like the ones real provider plugins ship: a choice, a number, plain text, a nested password.
    internal static string WriteSettingsSchema(string directory)
    {
        var path = Path.Combine(directory, "settings.schema.json");
        File.WriteAllText(path, """
            [
              { "key": "downloader", "label": "Download with", "type": "choice", "choices": ["magnet", "qbittorrent"], "default": "magnet", "help": "Magnet opens your default torrent client." },
              { "key": "resolution", "label": "Default quality", "type": "number", "default": "1080", "help": "1080, 720, 480 or 2160. Use 0 for any quality." },
              { "key": "qbittorrent.url", "label": "qBittorrent Web UI address", "default": "http://localhost:8080" },
              { "key": "qbittorrent.username", "label": "Web UI username" },
              { "key": "qbittorrent.password", "label": "Web UI password", "type": "password" },
              { "key": "baseUrl", "label": "Provider mirror (optional)", "help": "Leave empty to use the provider's default address." }
            ]
            """);
        return path;
    }

    private static void PickerChecks(MainWindow window, string directory, AcquisitionRequest request, Action<bool, string> check)
    {
        var pluginDirectory = Path.Combine(directory, "picker-plugin"); Directory.CreateDirectory(pluginDirectory);
        var plugin = new AcquisitionPlugin("interactive", "Test provider", "Interactive fixture", ["show"], "unused.exe", [], pluginDirectory, 30, true,
            WriteSettingsSchema(pluginDirectory));
        var legacy = plugin with { Id = "legacy", Name = "Legacy", Interactive = false, SettingsSchema = null };
        var calls = new List<AcquisitionRequest>();
        var picker = new AcquisitionPicker(window, [legacy, plugin], request, null, _ => new(Query: "Provider title", SourceUrl: "https://provider.invalid/saved/", Resolution: 720),
            (_, sent, _) =>
            {
                calls.Add(sent);
                return Task.FromResult(sent.Action == "search" ? new AcquisitionResult(true, "Choose a title", "shows", [new("https://provider.invalid/show/", "Chosen provider title", "Catalog")]) :
                    new AcquisitionResult(true, "Choose releases", "releases", [
                        new("release-1", "Chosen provider title S01E01 720p", "Requested S01E01", true, "S01E01", 720, "Chosen provider title <<episode_number>> 720p"),
                        new("release-1-alt", "Chosen provider title S01E01 720p alternate", "Requested S01E01", true, "S01E01", 720),
                        new("release-2", "Chosen provider title S01E02 720p", "Requested S01E02", true, "S01E02", 720),
                        new("other-release", "Different episode", "Does not match", false, null, 720)]));
            }, automatic: false);
        T Control<T>(string name) where T : FrameworkElement => (T)picker.FindName(name);
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        picker.Show(); window.UpdateLayout(); picker.UpdateLayout();
        check(Equals(Control<ComboBox>("AcquireProvider").SelectedItem, plugin) && Control<TextBox>("AcquireSource").Text == "https://provider.invalid/saved/" && (string)Control<ComboBox>("AcquireQuality").SelectedItem == "720p",
            "the acquisition picker opens on an interactive provider and restores saved source and quality");
        check(!Control<Button>("AcquireDownload").IsEnabled && calls.Count == 0, "with the automatic flow off, opening Acquire neither searches nor starts a download before a choice");
        check(Control<Button>("AcquireSavedPage").IsVisible, "a saved provider page is offered before anything is loaded");
        void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < until) { var frame = new System.Windows.Threading.DispatcherFrame(); window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false); System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(20); }
        }
        Click("AcquireSearch");
        check(calls.Single() is { Action: "search", SelectedReleases: null } && Control<ListBox>("AcquireResults").Items.Count == 1 && !Control<Button>("AcquireDownload").IsEnabled,
            "search displays provider title choices without acquiring anything");
        picker.UpdateLayout();
        // Clicking a provider title lists its releases; there is no separate button for it.
        var titleRow = (ListBoxItem)Control<ListBox>("AcquireResults").ItemContainerGenerator.ContainerFromIndex(0);
        titleRow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
        check(calls.Last().Options is { Query: "Chosen provider title", SourceUrl: "https://provider.invalid/show/", Resolution: 720 } && Control<ListBox>("AcquireResults").Items.Count == 4,
            "clicking a provider title lists its releases with the selected quality");
        check(!Control<Button>("AcquireDownload").IsEnabled, "release previews wait for an explicit selection");
        var list = Control<ListBox>("AcquireResults"); list.SelectedIndex = 0; Click("AcquireTemplate");
        check(Control<TextBox>("AcquireFilter").Text.Contains("<<episode_number>>") && !Control<Button>("AcquireDownload").IsEnabled, "a selected release builds an editable filter and invalidates the old preview");
        var before = calls.Count; Pump(() => calls.Count > before);
        check(calls.Count == before + 1 && calls.Last() is { Action: "releases" } && calls.Last().Options!.Filter!.Contains("<<episode_number>>"), "a changed filter reloads the releases on its own");
        Click("AcquireSelectMatches");
        check(list.SelectedItems.Count == 2 && Control<Button>("AcquireDownload").IsEnabled, "Select matching episodes chooses one release per requested episode");
        check(list.Items.Cast<AcquisitionChoice>().Take(2).SequenceEqual(list.SelectedItems.Cast<AcquisitionChoice>().OrderBy(x => x.Episode)),
            "the releases Select matching picks are listed first");
        list.SelectedItems.Add(list.Items[3]);
        check(!Control<Button>("AcquireDownload").IsEnabled, "unmatched releases cannot be downloaded as requested episodes");
        Click("AcquireSelectMatches");
        Control<ComboBox>("AcquireQuality").SelectedItem = "1080p";
        check(list.Items.Count == 0 && !Control<Button>("AcquireDownload").IsEnabled, "changing quality invalidates earlier release selections");
        before = calls.Count; Pump(() => calls.Count > before && list.Items.Count > 0);
        check(calls.Last().Options is { Resolution: 1080 } && list.Items.Count == 4, "a changed quality reloads the releases on its own");
        Click("AcquireSelectMatches");
        // Render the real WPF content for layout inspection without operating the user's window.
        picker.UpdateLayout();
        var downloadBounds = Control<Button>("AcquireDownload").TransformToAncestor(picker).TransformBounds(new Rect(Control<Button>("AcquireDownload").RenderSize));
        check(downloadBounds.Bottom <= picker.ActualHeight + 1 && downloadBounds.Right <= picker.ActualWidth + 1, "the primary acquisition action fits inside the dialog at the owner's available size");
        if (picker.ActualWidth > 0 && picker.ActualHeight > 0)
        {
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(picker.ActualWidth), (int)Math.Ceiling(picker.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var drawing = new System.Windows.Media.DrawingVisual();
            using (var context = drawing.RenderOpen()) context.DrawRectangle(new System.Windows.Media.VisualBrush(picker), null, new Rect(0, 0, picker.ActualWidth, picker.ActualHeight));
            image.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, "acquisition-picker.png")); encoder.Save(file);
        }
        Click("AcquireDownload");
        check(picker.Selection is { Remember: true, Request.Action: "acquire" } selection && selection.Request.SelectedReleases!.SequenceEqual(["release-1", "release-2"]) && selection.Request.Options!.Filter!.Contains("<<episode_number>>"),
            "confirmation returns only the chosen release IDs and title preferences");
        check(calls.All(x => x.Action is "search" or "releases"), "the picker never downloads while browsing or constructing its result");

        var moviePicker = new AcquisitionPicker(window, [plugin], request with { Scope = "movie" }, null, _ => null, automatic: false);
        moviePicker.Show(); moviePicker.UpdateLayout();
        check(((TextBlock)((StackPanel)((Button)moviePicker.FindName("AcquireSelectMatches")).Content).Children[1]).Text == "Select best match" && !((ComboBox)moviePicker.FindName("AcquireNumbering")).IsVisible,
            "movie acquisition uses movie actions and omits episode-numbering controls");
        moviePicker.Close();

        var relevancePicker = new AcquisitionPicker(window, [plugin], request, null, _ => new(Query: "Hazbin Hotel megusta", Resolution: 1080),
            (_, _, _) => Task.FromResult(new AcquisitionResult(true, "Choose releases", "releases", [
                new("psa", "Hazbin Hotel S02E01 1080p HEVC-PSA", Matches: true, Episode: "S02E01"),
                new("megusta", "Hazbin Hotel S02E01 1080p HEVC x265 MeGusta EZTV", Matches: true, Episode: "S02E01")])), automatic: false);
        relevancePicker.Show();
        ((Button)relevancePicker.FindName("AcquireSearch")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var relevantResults = (ListBox)relevancePicker.FindName("AcquireResults");
        check(relevantResults.Items.Cast<AcquisitionChoice>().Select(x => x.Id).SequenceEqual(["megusta", "psa"]),
            "the picker displays releases in search relevance order rather than alphabetical order");
        ((Button)relevancePicker.FindName("AcquireSelectMatches")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(relevantResults.SelectedItems.Cast<AcquisitionChoice>().Single().Id == "megusta",
            "Select matching episodes uses the most relevant release for each episode");
        relevancePicker.Close();

        // A season pack or OVA has no episode number: it's never picked, but may be downloaded when ticked on its own.
        var packPicker = new AcquisitionPicker(window, [plugin], request, null, _ => null,
            (_, _, _) => Task.FromResult(new AcquisitionResult(true, "Choose releases", "releases", [
                new("e1", "Show S01E01 1080p", "Requested", true, "S01E01", 1080),
                new("ova", "Show - OVA [1080p].mkv", "Special with no episode number", false, null, 1080, Assignable: true)])), automatic: false);
        packPicker.Show();
        ((Button)packPicker.FindName("AcquireSearch")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var packResults = (ListBox)packPicker.FindName("AcquireResults");
        var packDownload = (Button)packPicker.FindName("AcquireDownload");
        ((Button)packPicker.FindName("AcquireSelectMatches")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(packResults.SelectedItems.Cast<AcquisitionChoice>().Select(x => x.Id).SequenceEqual(["e1"]), "Select best matches never ticks a season pack or special");
        packResults.SelectedItems.Add(packResults.Items.Cast<AcquisitionChoice>().Single(x => x.Id == "ova"));
        check(!packDownload.IsEnabled, "a season pack or special cannot be downloaded together with other releases");
        packResults.SelectedItems.Clear(); packResults.SelectedItems.Add(packResults.Items.Cast<AcquisitionChoice>().Single(x => x.Id == "ova"));
        check(packDownload.IsEnabled, "a season pack or special ticked on its own can be downloaded");
        packPicker.Close();

        // Opening Acquire searches, picks the title that is this show (preferring the one with its year) and ticks the
        // best release of each episode, then waits for Download.
        var autoCalls = new List<AcquisitionRequest>();
        AcquisitionPicker? automaticPicker = null; var titlesShown = false;
        automaticPicker = new AcquisitionPicker(window, [plugin], request with { Title = "Dark Matter", OriginalTitle = null, Year = 2024 }, null, _ => null, (_, sent, _) =>
        {
            autoCalls.Add(sent);
            // Before the releases load, the automatically picked title's list must not have been displayed.
            if (sent.Action == "releases") titlesShown |= ((ListBox)automaticPicker!.FindName("AcquireResults")).Items.Count > 0;
            return Task.FromResult(sent.Action == "search"
                ? new AcquisitionResult(true, "Choose a title", "shows", [new("old-show", "Dark Matter"), new("new-show", "Dark Matter (2024)"), new("other", "Dark Matters")])
                : new AcquisitionResult(true, "Choose releases", "releases", [
                    new("e1-alt", "Dark Matter S01E01 720p PSA", "Requested", true, "S01E01", 720),
                    new("e1", "Dark Matter S01E01 720p", "Requested", true, "S01E01", 720),
                    new("e2", "Dark Matter S01E02 720p", "Requested", true, "S01E02", 720),
                    new("other", "Dark Matter S03E01 720p", "Not one of the missing episodes", false, null, 720)]));
        });
        automaticPicker.Show();
        Pump(() => autoCalls.Count >= 2 && ((ListBox)automaticPicker.FindName("AcquireResults")).SelectedItems.Count > 0);
        var autoResults = (ListBox)automaticPicker.FindName("AcquireResults");
        check(autoCalls.Select(x => x.Action).SequenceEqual(["search", "releases"]) && autoCalls[1].Options!.SourceUrl == "new-show",
            "opening Acquire searches and picks the provider title with the show's year");
        check(!titlesShown, "a title picked automatically goes straight to its releases without showing the title list");
        check(autoResults.SelectedItems.Count == 2 && autoResults.SelectedItems.Cast<AcquisitionChoice>().All(x => x.Matches) && ((Button)automaticPicker.FindName("AcquireDownload")).IsEnabled
            && automaticPicker.Selection is null && autoCalls.All(x => x.Action is "search" or "releases"),
            "opening Acquire ticks the best release of each episode and waits for Download");
        automaticPicker.Close();
        autoCalls.Clear();
        var unclear = new AcquisitionPicker(window, [plugin], request with { Title = "Dark Matter", OriginalTitle = null, Year = null }, null, _ => null, (_, sent, _) =>
        {
            autoCalls.Add(sent);
            return Task.FromResult(new AcquisitionResult(true, "Choose a title", "shows", [new("a", "Dark Matter (2015)"), new("b", "Dark Matter (2024)")]));
        });
        unclear.Show(); Pump(() => autoCalls.Count >= 1);
        check(autoCalls.Count == 1 && ((ListBox)unclear.FindName("AcquireResults")).Items.Count == 2, "when no title clearly matches, the picker leaves the choice of title to the user");
        unclear.Close();

        // Late results from cancelled searches must not overwrite newer choices or a closed dialog.
        var late = new TaskCompletionSource<AcquisitionResult>(); CancellationToken cancelled = default;
        var waiting = new AcquisitionPicker(window, [plugin], request, null, _ => null, (_, _, token) => { cancelled = token; return late.Task; }, automatic: false);
        waiting.Show(); ((Button)waiting.FindName("AcquireSearch")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ((TextBox)waiting.FindName("AcquireQuery")).Text = "Another title";
        check(cancelled.IsCancellationRequested, "editing the title cancels the pending provider search");
        waiting.Close(); late.SetResult(new(true, "Old result", "shows", [new("old", "Old title")]));
        var frame = new System.Windows.Threading.DispatcherFrame(); window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false); System.Windows.Threading.Dispatcher.PushFrame(frame);
        check(((ListBox)waiting.FindName("AcquireResults")).Items.Count == 0, "a late search result cannot update a closed picker");

        // Switching provider while another provider's titles are listed must not hand that provider's title to the new one.
        var first = plugin with { Id = "first", Name = "First" }; var second = plugin with { Id = "second", Name = "Second" };
        var switchCalls = new List<(string Plugin, AcquisitionRequest Sent)>();
        var switching = new AcquisitionPicker(window, [first, second], request with { Title = "Dark Matter", OriginalTitle = null, Year = null }, first,
            x => x.Id == "second" ? new(Query: "Dark Matter", SourceUrl: "https://second.invalid/saved/") : null, (used, sent, _) =>
            {
                switchCalls.Add((used.Id, sent));
                return Task.FromResult(sent.Action == "search"
                    ? new AcquisitionResult(true, "Choose a title", "shows", [new("https://first.invalid/2015/", "Dark Matter (2015)"), new("https://first.invalid/2024/", "Dark Matter (2024)")])
                    : new AcquisitionResult(true, "Choose releases", "releases", [new("second-release", "Dark Matter S01E01 720p", "Requested", true, "S01E01", 720)]));
            });
        switching.Show(); Pump(() => switchCalls.Count >= 1);
        var switchResults = (ListBox)switching.FindName("AcquireResults");
        check(switchResults.Items.Count == 2, "the first provider's titles are listed for the user to choose");
        ((ComboBox)switching.FindName("AcquireProvider")).SelectedItem = second; Pump(() => switchCalls.Count >= 2);
        check(switchCalls.Count == 2 && switchCalls[1] is ("second", { Action: "releases" }) && switchCalls[1].Sent.Options is { Query: "Dark Matter", SourceUrl: "https://second.invalid/saved/" }
            && ((TextBox)switching.FindName("AcquireSource")).Text == "https://second.invalid/saved/" && switchResults.Items.Cast<AcquisitionChoice>().All(x => x.Id == "second-release"),
            "switching provider drops the previous provider's titles and opens the new provider's own saved page");
        switching.Close();

        var settingsPath = Path.Combine(pluginDirectory, "settings.json");
        File.WriteAllText(settingsPath, """{"resolution":720,"custom":true,"titles":{"Other":{"filter":"keep"}},"qbittorrent":{"password":"old"}}""");
        var settings = AcquisitionPluginSettings.Create(window, plugin); settings.Show();
        ((ComboBox)settings.FindName("Setting_downloader")).SelectedItem = "qbittorrent";
        ((TextBox)settings.FindName("Setting_resolution")).Text = "invalid";
        ((Button)settings.FindName("SavePluginSettings")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        check(settings.IsOpen && JsonDocument.Parse(File.ReadAllText(settingsPath)).RootElement.GetProperty("resolution").GetInt32() == 720, "invalid numeric plugin settings remain unsaved in the dialog");
        ((TextBox)settings.FindName("Setting_resolution")).Text = "1080";
        ((PasswordBox)settings.FindName("Setting_qbittorrent_password")).Password = "updated";
        ((Button)settings.FindName("SavePluginSettings")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        using var config = JsonDocument.Parse(File.ReadAllText(settingsPath));
        check(!settings.IsOpen && config.RootElement.GetProperty("downloader").GetString() == "qbittorrent" && config.RootElement.GetProperty("qbittorrent").GetProperty("password").GetString() == "updated" && config.RootElement.GetProperty("custom").GetBoolean() && config.RootElement.GetProperty("titles").GetProperty("Other").GetProperty("filter").GetString() == "keep",
            "in-app plugin settings save downloader credentials and preserve existing title bindings and unknown fields");

        // Values the schema doesn't describe exactly survive an unchanged save: an unlisted choice and a number under a text field.
        File.WriteAllText(settingsPath, """{"downloader":"aria2","baseUrl":5}""");
        var unchanged = AcquisitionPluginSettings.Create(window, plugin); unchanged.Show();
        check((string?)((ComboBox)unchanged.FindName("Setting_downloader")).SelectedItem == "aria2", "a stored choice the schema doesn't list is still shown");
        ((Button)unchanged.FindName("SavePluginSettings")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        using var kept = JsonDocument.Parse(File.ReadAllText(settingsPath));
        check(!unchanged.IsOpen && kept.RootElement.GetProperty("downloader").GetString() == "aria2" && kept.RootElement.GetProperty("baseUrl").ValueKind == JsonValueKind.Number,
            "saving unchanged settings keeps an unlisted choice and a value's stored JSON type");
    }
}
