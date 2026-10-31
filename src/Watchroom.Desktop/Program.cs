using System.IO;
using System.Runtime.CompilerServices;

namespace Watchroom.Desktop;

// A video opened while Watchroom already runs is handed to the running app before WPF loads, so it starts playing there
// sooner. Update relaunches always start their own window. Nothing here may touch App or other WPF types: referring to
// them loads WPF.
public static class Program
{
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("WATCHROOM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchroom");
    // True when this process is the data directory's running app.
    public static bool Primary { get; private set; }
    public static string? OpenedFile { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        OpenedFile = OpenFileHandoff.FileArgument(args);
        Primary = OpenFileHandoff.Claim();
        if (!Primary && !args.Contains("--updated") && OpenedFile is not null && OpenFileHandoff.Forward(OpenedFile)) return;
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
