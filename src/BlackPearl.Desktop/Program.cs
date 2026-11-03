using System.IO;
using System.Runtime.CompilerServices;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// A video opened while BlackPearl already runs is handed to the running app before WPF loads, so it starts playing there
// sooner. Update relaunches always start their own window. Nothing here may touch App or other WPF types: referring to
// them loads WPF.
public static class Program
{
    // The installed app's own folder, whatever BLACKPEARL_DATA says; the installer waits for the relaunched app's ready
    // file in it after an update.
    public static string DefaultDataDirectory { get; } = DefaultData(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    // --data-dir, which auto-download's hourly check and notification links pass, wins over BLACKPEARL_DATA.
    public static string DataDirectory { get; private set; } = AppEnvironment.Get("DATA") ?? DefaultDataDirectory;
    public static string PluginsDirectory => AppEnvironment.Get("PLUGINS") ?? Path.Combine(DataDirectory, "plugins");
    // The app was called Watchroom before. A library from then stays in its Watchroom folder, so settings, artwork, rooms
    // and the saved TMDB key carry over without moving anything while an update is still running from that folder.
    internal static string DefaultData(string localAppData)
    {
        string current = Path.Combine(localAppData, "BlackPearl"), former = Path.Combine(localAppData, "Watchroom");
        return !File.Exists(Path.Combine(current, "library.db")) && File.Exists(Path.Combine(former, "library.db")) ? former : current;
    }
    // True when this process is the data directory's running app.
    public static bool Primary { get; private set; }
    public static string? OpenedFile { get; private set; }
    // Started from a notification's Review button: the app opens at Settings > Downloads.
    public static bool OpenDownloads { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        args = DataArgument(args);
        // Auto-download's notification buttons and hourly check run without a window, and never become the running app.
        // A link wins over every other argument, since a page can put more words after it.
        if (args.FirstOrDefault(x => x.StartsWith(FollowSystem.Scheme + ":", StringComparison.OrdinalIgnoreCase)) is { } link)
        {
            if (BackgroundFollow.Activate(link)) return;
            OpenDownloads = true;
        }
        else if (args.Contains("--check-episodes")) { BackgroundFollow.Check(); return; }
        OpenedFile = OpenDownloads ? null : OpenFileHandoff.FileArgument(args);
        Primary = OpenFileHandoff.Claim();
        if (!Primary && !args.Contains("--updated") && (OpenedFile ?? (OpenDownloads ? OpenFileHandoff.DownloadsRequest : null)) is { } handoff)
        {
            if (OpenFileHandoff.Forward(handoff)) return;
            Primary = OpenFileHandoff.Claimed;
        }
        Run();
    }
    // Takes "--data-dir <folder>" out of the arguments and uses that data folder.
    internal static string[] DataArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        if (index < 0 || index + 1 >= args.Length) return args;
        try { DataDirectory = Path.GetFullPath(args[index + 1]); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return [.. args[..index], .. args[(index + 2)..]];
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
