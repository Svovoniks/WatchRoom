using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Copies of one film share a card, and the film page's Version pills switch between them without adding Back steps.
static class FilmVersionChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethods(flags).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
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

        var folder = Path.Combine(directory, "film-versions"); Directory.CreateDirectory(folder);
        MediaItem Copy(string id, string name)
        {
            var path = Path.Combine(folder, name); File.WriteAllText(path, id);
            return new MediaItem(id, path, "Version check film", "Movie", 1999, MetadataProvider: "tmdb", MetadataId: 990099, MetadataType: "movie",
                ProviderIds: new() { ["tmdb"] = "990099" });
        }
        var hd = Copy("version-check-hd", "Version.Check.Film.1999.1080p.BluRay.mkv");
        var uhd = Copy("version-check-uhd", "Version.Check.Film.1999.2160p.UHD.mkv");
        store.Save(hd); store.Save(uhd);
        Await(Call("RefreshLibrary"));

        var history = Field<IList>("backLocations"); var forward = Field<IList>("forwardLocations");
        // The page stays the one the previous checks left, so the checks after these start where they expect.
        var start = Field<string>("currentPage"); var before = history.Count;
        Call("Select", Field<List<MediaItem>>("items").Single(x => x.Id == hd.Id));
        var opened = history.Count;
        Button Pill(string label) => ((Panel)window.FindName("DetailVersions")).Children.OfType<Button>().Single(b => (string)b.Content == label);
        void Click(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        check(opened == before + 1 && Pill("2160p UHD") is not null, "opening a film with two copies shows its Version pills and adds one Back step");

        Click(Pill("2160p UHD")); Until(() => Field<MediaItem>("selected").Id == uhd.Id);
        Click(Pill("1080p BluRay")); Until(() => Field<MediaItem>("selected").Id == hd.Id);
        Click(Pill("2160p UHD")); Until(() => Field<MediaItem>("selected").Id == uhd.Id);
        check(Field<MediaItem>("selected").Id == uhd.Id && Field<string>("currentPage") == "Details" && history.Count == opened,
            "switching versions changes the page in place without adding Back steps");
        Call("BrowseHistory", true);
        check(Field<string>("currentPage") == start && history.Count == before, "Back from a film after switching versions returns where it was opened from");
        Call("BrowseHistory", false);
        check(Field<string>("currentPage") == "Details" && Field<MediaItem>("selected").Id == uhd.Id && forward.Count == 0,
            "Forward returns to the version last shown");

        // Copies count as one film for watching too: clearing it clears every copy, as an episode's versions do.
        store.Setting("watched:" + hd.Id, "true"); store.Setting("watched:" + uhd.Id, "true");
        Call("MarkWatched", Field<MediaItem>("selected"), "video", null, false, false);
        check(store.Setting("watched:" + hd.Id) == "false" && store.Setting("watched:" + uhd.Id) == "false", "marking a film unwatched clears every copy of it");
        Call("BrowseHistory", true);
        foreach (var item in new[] { hd, uhd }) store.Save(item with { Available = false });
        store.RemoveUnavailable([hd.Id, uhd.Id]);
        Await(Call("RefreshLibrary"));
    }
}
