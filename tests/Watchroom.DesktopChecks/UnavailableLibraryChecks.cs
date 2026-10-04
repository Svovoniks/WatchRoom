using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;
using Watchroom.Desktop;

static class UnavailableLibraryChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method, flags)!.Invoke(window, args);
        var file = Path.Combine(directory, "removal-entry.mkv"); File.WriteAllText(file, "keep this file");
        var missing = new MediaItem("removal-missing", file, "Removal show", "Show", Series: "Removal show", Season: 1, Episode: 1, Available: false);
        var available = missing with { Id = "removal-available", Path = file + ".available", Episode = 2, Available = true };
        var otherSeason = missing with { Id = "removal-other-season", Path = file + ".other", Season = 2 };
        var movie = new MediaItem("removal-movie", file + ".movie", "Removal movie", "Movie", Available: false);
        foreach (var item in new[] { missing, available, otherSeason, movie }) store.Save(item);
        store.Setting("position:" + missing.Id, "10000"); store.Setting("watched:" + missing.Id, "true");
        Call("RefreshLibrary");
        var card = new LibraryCard(missing.DisplayTitle, "", null, missing, "season", 1);
        var targets = (IEnumerable<MediaItem>)Call("UnavailableCardItems", card)!;
        check(targets.Select(item => item.Id).SequenceEqual([missing.Id]), "season removal selects only its unavailable entries");
        Call("RemoveUnavailableItems", targets);
        check(!store.All().Any(item => item.Id == missing.Id) && store.All().Any(item => item.Id == available.Id) && store.All().Any(item => item.Id == otherSeason.Id),
            "removing an unavailable season entry preserves playable videos and other seasons");
        check(File.ReadAllText(file) == "keep this file", "library removal leaves media files untouched");
        check(store.Setting("position:" + missing.Id) is null && store.Setting("watched:" + missing.Id) is null,
            "removed entries lose their saved playback history");
        check(store.RemoveUnavailable([available.Id]) == 0 && store.All().Any(item => item.Id == available.Id),
            "storage refuses to remove an available video");
        Call("Select", movie);
        var remove = (Button)window.FindName("DetailRemove");
        check(remove.Visibility == Visibility.Visible && remove.IsEnabled, "unavailable video details offer removal");
        Call("RemoveUnavailableVideo", remove, new RoutedEventArgs());
        check(!store.All().Any(item => item.Id == movie.Id) && typeof(MainWindow).GetField("selected", flags)!.GetValue(window) is null,
            "details removal deletes the entry and returns to library browsing");
        Call("Select", available);
        check(remove.Visibility == Visibility.Collapsed, "available video details hide library removal");
        Call("RemoveUnavailableItems", (object)new[] { otherSeason });
        check(!new LibraryStore(directory).All().Any(item => item.Id == movie.Id), "unavailable entry removal persists across reopening the library");
        Call("ShowPage", "Library");
    }
}
