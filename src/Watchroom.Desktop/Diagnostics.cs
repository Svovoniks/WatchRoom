using System.Diagnostics;
using System.IO;
using System.Windows;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private async void OpenLogsFolder(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            var directory = PlaybackDiagnostics.DirectoryPath ?? Path.Combine(App.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            await PlaybackDiagnostics.FlushAsync();
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        });
    }
}
