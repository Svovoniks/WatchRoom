using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class MainWindow
{
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient updateHttp = new() { Timeout = TimeSpan.FromMinutes(20) };
    private AppUpdate? availableUpdate;
    private bool checkingUpdates, installingUpdate;
    private static Version InstalledVersion => new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
    private static string UpdateRepository => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(x => x.Key == "UpdateRepository").Value!;
    private void InitializeUpdates()
    {
        UpdateStatus.Text = $"Installed version {InstalledVersion}";
        AutoCheckUpdates.IsChecked = library.Setting("autoUpdates") != "false";
        updateTimer.Tick += async (_, _) => { updateTimer.Interval = TimeSpan.FromHours(6); if (AutoCheckUpdates.IsChecked == true) await CheckAppUpdates(false); };
        if (Environment.GetEnvironmentVariable("WATCHROOM_DISABLE_UPDATES") != "1") updateTimer.Start();
    }
    private void AutoUpdatesChanged(object sender, RoutedEventArgs e)
    { if (library is not null) library.Setting("autoUpdates", AutoCheckUpdates.IsChecked == true ? "true" : "false"); }
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckAppUpdates(true);
    private async Task CheckAppUpdates(bool manual)
    {
        if (checkingUpdates || installingUpdate || closing) return;
        checkingUpdates = true; CheckUpdatesButton.IsEnabled = false;
        if (manual) UpdateStatus.Text = "Checking GitHub releases…";
        try
        {
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            availableUpdate = await new GitHubUpdates(updateHttp, UpdateRepository).CheckAsync(InstalledVersion, checkTimeout.Token);
            if (closing) return;
            UpdateStatus.Text = availableUpdate is null ? $"Version {InstalledVersion} · No newer release available." : $"Version {availableUpdate.Version} is available.";
            if (availableUpdate is not null && (manual || library.Setting("dismissedUpdate") != availableUpdate.Version.ToString()))
            {
                UpdateCardTitle.Text = $"Watchroom {availableUpdate.Version} is available";
                UpdateCardStatus.Text = "Download and install the latest version from GitHub. The app will close and reopen; your library and settings are kept.";
                UpdateCard.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) { if (manual && !closing) UpdateStatus.Text = "Update check timed out. Try again."; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        { if (manual) UpdateStatus.Text = "Could not check for updates: " + ex.Message; }
        finally { checkingUpdates = false; CheckUpdatesButton.IsEnabled = true; }
    }
    private void DismissUpdate(object sender, RoutedEventArgs e)
    { if (installingUpdate) return; library.Setting("dismissedUpdate", availableUpdate?.Version.ToString() ?? ""); UpdateCard.Visibility = Visibility.Collapsed; }
    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null || installingUpdate) return;
        installingUpdate = true; InstallUpdateButton.IsEnabled = false; DismissUpdateButton.IsEnabled = false;
        UpdateDownloadProgress.Visibility = Visibility.Visible;
        try
        {
            UpdateCardStatus.Text = "Downloading and verifying the installer…";
            var progress = new Progress<double>(value => { UpdateDownloadProgress.Value = value; });
            var installer = await new GitHubUpdates(updateHttp, UpdateRepository).DownloadAsync(availableUpdate, Path.Combine(App.DataDirectory, "updates"), progress, lifetime.Token);
            if (closing) return;
            var start = new ProcessStartInfo(installer) { UseShellExecute = false };
            foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/WATCHROOMUPDATE=1", "/LOG=" + Path.Combine(App.DataDirectory, "updates", "install.log"), "/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) }) start.ArgumentList.Add(argument);
            if (Process.Start(start) is null) throw new IOException("Could not start the installer.");
            Close();
        }
        catch (OperationCanceledException) { if (!closing) UpdateCardStatus.Text = "Download interrupted. Try again."; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.ComponentModel.Win32Exception)
        { UpdateCardStatus.Text = "Update failed: " + ex.Message; }
        finally { installingUpdate = false; InstallUpdateButton.IsEnabled = true; DismissUpdateButton.IsEnabled = true; UpdateDownloadProgress.Visibility = Visibility.Collapsed; }
    }
}
