using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Watchroom.Core;

namespace Watchroom.Desktop;
public partial class MainWindow
{
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient updateHttp = new() { Timeout = TimeSpan.FromMinutes(20) };
    private AppUpdate? availableUpdate;
    private bool checkingUpdates, installingUpdate;
    private string? updateDirectory;
    private static Version InstalledVersion => new(Assembly.GetExecutingAssembly().GetName().Version!.ToString(3));
    private static string UpdateRepository => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(x => x.Key == "UpdateRepository").Value!;
    private void InitializeUpdates()
    {
        UpdateStatus.Text = $"Installed version {InstalledVersion}";
        AutoCheckUpdates.IsChecked = library.Setting("autoUpdates") != "false";
        updateDirectory = UpdateLocation.InstallTarget(AppContext.BaseDirectory, RegisteredInstall(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Watchroom"));
        if (updateDirectory is null)
        {
            UpdateStatus.Text = $"Version {InstalledVersion} · This copy runs outside the installed app, so it doesn't update itself.";
            AutoCheckUpdates.IsEnabled = CheckUpdatesButton.IsEnabled = false; return;
        }
        updateTimer.Tick += async (_, _) => { updateTimer.Interval = TimeSpan.FromHours(6); if (AutoCheckUpdates.IsChecked == true) await CheckAppUpdates(false); };
        if (Environment.GetEnvironmentVariable("WATCHROOM_DISABLE_UPDATES") != "1") updateTimer.Start();
    }
    private static string? RegisteredInstall()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{90B3BA58-BBC8-4993-A0EA-25C68D8B1DA6}_is1";
        try { using var entry = Registry.CurrentUser.OpenSubKey(key) ?? Registry.LocalMachine.OpenSubKey(key); return entry?.GetValue("InstallLocation") as string; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }
    private void AutoUpdatesChanged(object sender, RoutedEventArgs e)
    { if (library is not null) library.Setting("autoUpdates", AutoCheckUpdates.IsChecked == true ? "true" : "false"); }
    private async void CheckUpdatesClick(object sender, RoutedEventArgs e) => await CheckAppUpdates(true);
    private async Task CheckAppUpdates(bool manual)
    {
        if (checkingUpdates || installingUpdate || closing || updateDirectory is null) return;
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException)
        { if (manual) UpdateStatus.Text = "Could not check for updates: " + ex.Message; }
        finally { checkingUpdates = false; CheckUpdatesButton.IsEnabled = true; }
    }
    // Native video draws over the main window, so the player shows the card in its video overlay.
    private void PlaceUpdateCard(bool inPlayer)
    {
        System.Windows.Controls.Panel host = inPlayer ? PlayerUpdateHost : WindowRoot;
        if (UpdateCard.Parent == host) return;
        (UpdateCard.Parent as System.Windows.Controls.Panel)?.Children.Remove(UpdateCard);
        host.Children.Add(UpdateCard);
    }
    private void DismissUpdate(object sender, RoutedEventArgs e)
    { if (installingUpdate) return; library.Setting("dismissedUpdate", availableUpdate?.Version.ToString() ?? ""); UpdateCard.Visibility = Visibility.Collapsed; }
    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is null || installingUpdate || updateDirectory is null) return;
        installingUpdate = true; InstallUpdateButton.IsEnabled = false; DismissUpdateButton.IsEnabled = false;
        UpdateDownloadProgress.Visibility = Visibility.Visible;
        try
        {
            UpdateCardStatus.Text = "Downloading and verifying the installer…";
            var progress = new Progress<double>(value => { UpdateDownloadProgress.Value = value; });
            var installer = await new GitHubUpdates(updateHttp, UpdateRepository).DownloadAsync(availableUpdate, Path.Combine(App.DataDirectory, "updates"), progress, lifetime.Token);
            if (closing) return;
            var start = new ProcessStartInfo(installer) { UseShellExecute = false };
            UpdateDownloadProgress.IsIndeterminate = true;
            UpdateCardStatus.Text = "Installing and restarting Watchroom… The installer will show progress.";
            foreach (var argument in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/WATCHROOMUPDATE=1", "/LOG=" + Path.Combine(App.DataDirectory, "updates", "install.log"), "/DIR=" + updateDirectory }) start.ArgumentList.Add(argument);
            if (Process.Start(start) is null) throw new IOException("Could not start the installer.");
            Close();
        }
        catch (OperationCanceledException) { if (!closing) UpdateCardStatus.Text = "Download interrupted. Try again."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception)
        { UpdateCardStatus.Text = "Update failed: " + ex.Message; }
        finally { installingUpdate = false; InstallUpdateButton.IsEnabled = true; DismissUpdateButton.IsEnabled = true; UpdateDownloadProgress.IsIndeterminate = false; UpdateDownloadProgress.Visibility = Visibility.Collapsed; }
    }
}
