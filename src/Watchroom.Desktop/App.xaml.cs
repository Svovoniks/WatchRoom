using System.IO;
using System.Windows;
using System.Windows.Input;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class App : Application
{
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("WATCHROOM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchroom");
    protected override void OnStartup(StartupEventArgs e)
    {
        // The library, scrolling and animations draw on the GPU. Surfaces layered over the native video
        // (LibVLC's see-through control window and the player menus) keep the software surface for reliable
        // interop on hybrid GPUs. Remote desktops, GPUs without hardware rendering and WATCHROOM_RENDER=software
        // draw everything in software. libVLC uses hardware video decoding either way.
        if (SoftwareRendering)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        else
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window { } window && window.GetType().Name == "ForegroundWindow") UseSoftwareSurface(window);
            }));
        Directory.CreateDirectory(DataDirectory);
        PlaybackDiagnostics.Initialize(Path.Combine(DataDirectory, "logs"));
        PlaybackDiagnostics.Record("app-start", new { version = typeof(App).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.VersionString });
        DispatcherUnhandledException += (_, args) =>
        {
            PlaybackDiagnostics.Record("ui-error", new { error = args.Exception.GetType().Name, stack = args.Exception.StackTrace });
            File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), DateTime.UtcNow + " " + args.Exception + Environment.NewLine);
            args.Handled = true;
            // One notice at a time: an error that repeats while the notice is open is only logged.
            if (InAppDialog.Open.Any(dialog => dialog.Title == "Something went wrong")) return;
            if (MainWindow is { IsLoaded: true } window) Dialogs.Alert(window, "Something went wrong", args.Exception.Message);
            else MessageBox.Show(args.Exception.Message, "Watchroom", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        // Only the app's own focus ring marks keyboard focus; WPF's dotted rectangles never draw.
        EventManager.RegisterClassHandler(typeof(FrameworkElement), Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { FocusVisualStyle: { } style } element && !ReferenceEquals(style, Resources["FocusRing"])) element.FocusVisualStyle = null;
        }));
        base.OnStartup(e);
        if (e.Args.Contains("--updated"))
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is MainWindow)
                {
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchroom", "updates"); Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "ready"), "ready");
                }
            }));
        }
    }
    public static bool SoftwareRendering { get; } = Environment.GetEnvironmentVariable("WATCHROOM_RENDER") is { } mode
        ? !mode.Equals("hardware", StringComparison.OrdinalIgnoreCase)
        : SystemParameters.IsRemoteSession || System.Windows.Media.RenderCapability.Tier >> 16 == 0;
    public static void UseSoftwareSurface(System.Windows.Media.Visual visual)
    {
        if (System.Windows.Interop.HwndSource.FromVisual(visual) is System.Windows.Interop.HwndSource { CompositionTarget: { } target })
            target.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        PlaybackDiagnostics.Record("app-exit", new { e.ApplicationExitCode });
        try { PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        base.OnExit(e);
    }
}
