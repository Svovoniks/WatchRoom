using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

// Songs play with their artwork and names in place of a picture, keep the controls on screen, carry on to the
// next track of their album without a countdown, and start from the beginning.
static class MusicPlaybackChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Named<T>(string name) => (T)window.FindName(name);
        async Task<bool> Until(Func<bool> condition, int milliseconds = 8000)
        {
            for (var waited = 0; waited < milliseconds; waited += 50) { if (condition()) return true; await Task.Delay(50); }
            return condition();
        }
        check(Named<CheckBox>("MixedFolderMusic").IsChecked == false && !Field<LibraryStore>("library").MixedFoldersListMusic,
            "Mixed folders leave music out until chosen in Folders");
        var root = Path.Combine(Path.GetTempPath(), "wr-music-" + Guid.NewGuid().ToString("N"));
        var album = Path.Combine(root, "Fixture Artist", "Fixture Album");
        Directory.CreateDirectory(album);
        var paths = new[] { "01 - First Song.wav", "02 - Second Song.wav" }.Select(name => Path.Combine(album, name)).ToArray();
        foreach (var path in paths) File.WriteAllBytes(path, Wave(6));
        File.WriteAllBytes(Path.Combine(album, "cover.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aXioAAAAASUVORK5CYII="));
        var video = Path.Combine(root, "Fixture film.avi"); VideoFixture.Write(video, 8);
        var first = MusicFiles.Parse(paths[0], root); var second = MusicFiles.Parse(paths[1], root);
        var film = new MediaItem("music-check-film", video, "Fixture film", "Movie");
        var items = Field<List<MediaItem>>("items"); var originalItems = items;
        var queue = Field<List<MediaItem>>("queue"); var previousQueue = queue.ToArray(); var previousPosition = Field<int>("queuePosition");
        var panel = Named<Grid>("AudioPlayback"); var controls = Named<Border>("PlaybackControls");
        try
        {
            Set("items", items.Concat([first, second, film]).ToList());
            string Status(string name) => System.Windows.Automation.AutomationProperties.GetItemStatus(Named<Button>(name));
            Call("NavigateLibrary", LibraryCatalog.Music, null, null, null);
            var trail = (IEnumerable<object>)Named<ItemsControl>("LibraryBreadcrumbs").ItemsSource;
            check(Named<Button>("MusicLibraryNav").Visibility == Visibility.Visible && Named<Button>("MusicLibraryNav").Parent != Named<StackPanel>("LibraryCategories") &&
                Status("MusicLibraryNav") == "Selected" && Status("LibraryNav") == "" && trail.First().ToString()!.Contains("Music"),
                "Music has its own sidebar entry apart from the video library, with its own breadcrumb trail");
            Call("NavigateLibrary", 0, null, null, null);
            check(Status("LibraryNav") == "Selected" && Status("MusicLibraryNav") == "", "the video library is selected apart from Music");
            queue.Clear(); Set("queuePosition", -1);
            Field<LibraryStore>("library").Setting("position:" + first.Id, "4000");
            await (Task)Call("PlayLocalItem", first)!;
            check(panel.Visibility == Visibility.Visible && Named<TextBlock>("AudioTitle").Text == "First Song" &&
                Named<TextBlock>("AudioArtist").Text == "Fixture Artist" && Named<TextBlock>("AudioAlbum").Text == "Fixture Album",
                "a song shows its title, artist and album in the player as soon as it starts");
            check(Field<long>("localResume") == 0, "a song starts from the beginning instead of a saved position");
            check(await Until(() => Named<Image>("AudioArtwork").Source is not null), "the player shows the album's cover");
            check(await Until(() => Field<bool>("ready")), "the song starts playing");
            Set("lastPlayerInteraction", 0L); Call("PlaybackTickCore");
            check(controls.Visibility == Visibility.Visible, "the player controls stay on screen while music plays");
            Call("RefreshEpisodeEndAction");
            check(Named<Button>("EpisodeEndButton").Visibility == Visibility.Collapsed, "music has no Next episode or Finish button");
            Call("RefreshQueueList", true);
            check(Named<ListBox>("QueueList").Items.Count == 2 && Named<TextBlock>("QueueStatus").Text == "Fixture Album · plays in order",
                "the player lists the album's tracks");

            await (Task)Call("PlaybackEnded", first, null, Field<int>("loadGeneration"))!;
            check(Field<MediaItem>("playingItem")?.Id == second.Id && Named<Border>("UpNextCard").Visibility == Visibility.Collapsed,
                "the next track of the album plays straight away, without a countdown");
            check(Named<TextBlock>("AudioTitle").Text == "Second Song", "the player names the new track");

            await (Task)Call("PlayLocalItem", film)!;
            check(panel.Visibility == Visibility.Collapsed, "a video replaces the music view with its picture");
            check(await Until(() => Field<bool>("ready")), "the video starts playing");
            Set("lastPlayerInteraction", 0L); Call("PlaybackTickCore"); await Task.Delay(1700); Call("PlaybackTickCore");
            check(panel.Visibility == Visibility.Collapsed, "a video with a picture is not mistaken for music");
        }
        finally
        {
            await (Task)Call("Disconnect", false, false)!;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Set("items", originalItems); queue.Clear(); queue.AddRange(previousQueue); Set("queuePosition", previousPosition);
            check(panel.Visibility == Visibility.Collapsed, "leaving playback clears the music view");
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] Wave(int seconds)
    {
        var data = seconds * 16000;
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + data); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(data); writer.Write(new byte[data]);
        writer.Flush(); return stream.ToArray();
    }
}
