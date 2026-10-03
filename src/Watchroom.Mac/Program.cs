using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Watchroom.Core;

namespace Watchroom.Mac;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--diagnostics"))
        {
            try
            {
                NativeMedia.Initialize();
                using var vlc = new LibVLCSharp.Shared.LibVLC("--quiet");
                using var peer = new PeerTransport([]);
                Console.WriteLine("LibVLC and libdatachannel loaded successfully.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

public sealed class App : Application
{
    public static string DataDirectory => Environment.GetEnvironmentVariable("WATCHROOM_DATA") ??
        (OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Watchroom") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Watchroom-MacPreview"));
    public override void Initialize()
    {
        Name = "Watchroom";
        Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
