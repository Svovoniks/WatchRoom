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
    public static string DataDirectory { get; } = AppEnvironment.Get("DATA") ?? DefaultDataDirectory;
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

    [STAThread]
    public static void Main(string[] args)
    {
        OpenedFile = OpenFileHandoff.FileArgument(args);
        Primary = OpenFileHandoff.Claim();
        if (!Primary && !args.Contains("--updated") && OpenedFile is not null)
        {
            if (OpenFileHandoff.Forward(OpenedFile)) return;
            Primary = OpenFileHandoff.Claimed;
        }
        Run();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
