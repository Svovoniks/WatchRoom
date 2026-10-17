using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Watchroom.Core;
using Watchroom.Desktop;

// Design review: renders the real window over a copy of a library (WATCHROOM_DATA) to PNG files.
// Usage: --render-screens <output directory> [width height]
static class ScreenRender
{
    public static async Task Run(MainWindow window, string output, int width, int height)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
        void Set(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(window, args);
        async Task Settle(int milliseconds = 1500)
        {
            var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < until) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Directory.CreateDirectory(output);
        async Task Capture(string name)
        {
            await Settle();
            var root = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
            Console.WriteLine("Saved " + name);
        }
        window.Width = width; window.Height = height;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (Field("libraryLoaded") is not true && DateTime.UtcNow < deadline) await Settle(200);
        var items = ((List<MediaItem>)Field("items")!).Where(item => item.Available && !item.IsExtra).ToList();
        foreach (var theme in new[] { "dark", "light" })
        {
            Set("light", theme == "light"); Call("ApplyTheme");
            Call("ShowPage", "Home"); await Capture($"{theme}-home");
            Call("NavigateLibrary", 0, null, null, null); await Capture($"{theme}-library");
            if (theme == "light") continue;
            Call("NavigateLibrary", 1, null, null, null); await Capture($"{theme}-movies");
            var episode = items.FirstOrDefault(item => item.Series is not null && item.Season > 0);
            if (episode is not null)
            {
                Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(episode), "Show", null); await Capture($"{theme}-show");
                Call("NavigateLibrary", 2, LibraryIdentity.ShowKey(episode), "Show", episode.Season); await Capture($"{theme}-season");
                Call("Select", episode); await Capture($"{theme}-episode-details");
            }
            var movie = items.FirstOrDefault(item => item.Series is null && item.Kind == "Movie");
            if (movie is not null) { Call("Select", movie); await Capture($"{theme}-movie-details"); }
            foreach (var page in new[] { "Queues", "Rooms", "Folders", "Room" }) { Call("ShowPage", page); await Capture($"{theme}-{page.ToLowerInvariant()}"); }
            Call("ShowPage", "Settings");
            foreach (var section in new[] { "Profile", "Metadata", "Playback", "Appearance", "Updates" })
            { Call("ShowPreferenceSection", section); await Capture($"{theme}-settings-{section.ToLowerInvariant()}"); }
        }
    }
}
