using System.IO;
using System.Windows;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class App : Application
{
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("WATCHROOM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchroom");
    protected override void OnStartup(StartupEventArgs e)
    {
        // The WPF shell uses a software surface for reliable native-video interop on
        // hybrid GPUs and remote desktops. libVLC still uses hardware video decoding.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Directory.CreateDirectory(DataDirectory);
        PlaybackDiagnostics.Initialize(Path.Combine(DataDirectory, "logs"));
        PlaybackDiagnostics.Record("app-start", new { version = typeof(App).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.VersionString });
        DispatcherUnhandledException += (_, args) =>
        {
            PlaybackDiagnostics.Record("ui-error", new { error = args.Exception.GetType().Name, stack = args.Exception.StackTrace });
            File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), DateTime.UtcNow + " " + args.Exception + Environment.NewLine);
            MessageBox.Show(args.Exception.Message, "Watchroom", MessageBoxButton.OK, MessageBoxImage.Warning); args.Handled = true;
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        PlaybackDiagnostics.Record("app-exit", new { e.ApplicationExitCode });
        try { PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        base.OnExit(e);
    }
}
