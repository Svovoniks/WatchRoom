using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;
using Watchroom.Desktop;

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
        File.WriteAllText(Path.Combine(plugins, "get.cmd"), "@echo off\r\ncopy /y \"%WATCHROOM_REQUEST_FILE%\" \"%WATCHROOM_PLUGIN_DIR%\\last.json\" >nul\r\necho {\"success\": true, \"message\": \"Queued\"}\r\n");
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

        foreach (var title in new[] { show, film }) WantedTitles.Remove(store, directory, store.Wanted(title.Id)!);
        folders.Remove(folder);
        Directory.Delete(plugins, true); Call("LoadAcquisitionPlugins");
        Await(Call("RefreshLibrary"));
        Call("ShowPage", "Library");
    }
}
