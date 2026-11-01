using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using BlackPearl.Core;

namespace BlackPearl.Desktop;
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BlackPearl"));
        if (updateDirectory is null)
        {
            UpdateStatus.Text = $"Version {InstalledVersion} · This copy runs outside the installed app, so it doesn't update itself.";
            AutoCheckUpdates.IsEnabled = CheckUpdatesButton.IsEnabled = false; return;
        }
        updateTimer.Tick += async (_, _) => { updateTimer.Interval = TimeSpan.FromHours(6); if (AutoCheckUpdates.IsChecked == true) await CheckAppUpdates(false); };
        if (AppEnvironment.Get("DISABLE_UPDATES") != "1") updateTimer.Start();
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
            // Update installers that ran without reaching this version (refused, blocked or interrupted) are not tried twice.
            if (availableUpdate is { Partial: true } && library.Setting("partialUpdate") == availableUpdate.Version.ToString())
                availableUpdate = availableUpdate with { Steps = [availableUpdate.Full] };
            if (closing) return;
            UpdateStatus.Text = availableUpdate is null ? $"Version {InstalledVersion} · No newer release available." : $"Version {availableUpdate.Version} is available.";
            if (availableUpdate is not null && (manual || library.Setting("dismissedUpdate") != availableUpdate.Version.ToString()))
            {
                UpdateCardTitle.Text = $"BlackPearl {availableUpdate.Version} is available";
                UpdateCardStatus.Text = $"Download and install the latest version from GitHub ({availableUpdate.Size / 1048576.0:0.#} MB). The app will close and reopen; your library and settings are kept.";
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
            var update = availableUpdate;
            UpdateCardStatus.Text = update.Partial ? "Checking the installed files, then downloading the update…" : "Downloading and verifying the installer…";
            var progress = new Progress<double>(value => { UpdateDownloadProgress.Value = value; });
            var updates = new GitHubUpdates(updateHttp, UpdateRepository);
            var folder = Path.Combine(App.DataDirectory, "updates");
            var installers = await updates.DownloadAsync(update, folder, updateDirectory, progress, lifetime.Token);
            if (closing) return;
            UpdateDownloadProgress.IsIndeterminate = true;
            UpdateCardStatus.Text = "Installing and restarting BlackPearl… The installer will show progress.";
            var partial = Path.GetFileName(installers[0]) != update.Full.Name;
            if (partial) library.Setting("partialUpdate", update.Version.ToString());
            try { await StartInstallers(installers); }
            // Antivirus software may hold back or remove a downloaded installer; the full one is the well-known file.
            catch (System.ComponentModel.Win32Exception) when (partial)
            {
                UpdateDownloadProgress.IsIndeterminate = false;
                UpdateCardStatus.Text = "Downloading the full installer…";
                installers = await updates.DownloadAsync(update with { Steps = [update.Full] }, folder, null, progress, lifetime.Token);
                if (closing) return;
                UpdateDownloadProgress.IsIndeterminate = true;
                UpdateCardStatus.Text = "Installing and restarting BlackPearl… The installer will show progress.";
                await StartInstallers(installers);
            }
            Close();
        }
        catch (OperationCanceledException) { if (!closing) UpdateCardStatus.Text = "Download interrupted. Try again."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception)
        { UpdateCardStatus.Text = "Update failed: " + ex.Message; }
        finally { installingUpdate = false; InstallUpdateButton.IsEnabled = true; DismissUpdateButton.IsEnabled = true; UpdateDownloadProgress.IsIndeterminate = false; UpdateDownloadProgress.Visibility = Visibility.Collapsed; }
    }
    // A chain of update installers runs one after another from a small script once the app has closed. Only the last
    // one reopens BlackPearl; when a step fails the script reopens the version that is installed by then. cmd reads the
    // script in the console code page, so it names only its own folder (%~dp0, where the installers are downloaded) and
    // takes the installation folder from the environment; profile paths in any language stay intact.
    private async Task StartInstallers(IReadOnlyList<string> installers)
    {
        if (installers.Count == 1) { await StartInstaller(installers[0]); return; }
        var folder = Path.Combine(App.DataDirectory, "updates");
        if (installers.Any(x => !string.Equals(Path.GetDirectoryName(x), folder, StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(x), @"^[A-Za-z0-9.-]+$")))
            throw new IOException("Unexpected installer location.");
        var script = new System.Text.StringBuilder("@echo off\r\n");
        for (var i = 0; i < installers.Count; i++)
        {
            var last = i == installers.Count - 1;
            script.Append($"start \"\" /wait \"%~dp0{Path.GetFileName(installers[i])}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
                .Append(last ? " /BLACKPEARLUPDATE=1" : "").Append($" \"/LOG=%~dp0install-{i + 1}.log\" \"/DIR=%BLACKPEARL_INSTALL%\"\r\nif errorlevel 1 goto failed\r\n");
        }
        script.Append("exit /b 0\r\n:failed\r\nstart \"\" \"%BLACKPEARL_INSTALL%\\BlackPearl.exe\"\r\nexit /b 1\r\n");
        var path = Path.Combine(folder, "install-chain.cmd");
        await File.WriteAllTextAsync(path, script.ToString(), lifetime.Token);
        // /s with doubled quotes keeps the script path intact even when the profile folder has spaces or parentheses.
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{path}\"\"") { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["BLACKPEARL_INSTALL"] = updateDirectory;
        if (Process.Start(start) is null) throw new IOException("Could not start the update.");
    }
    // Antivirus software can deny or lock a just-downloaded installer while it scans it, so those errors are retried.
    private async Task StartInstaller(string installer)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = false };
        foreach (var argument in new[] { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/BLACKPEARLUPDATE=1", "/LOG=" + Path.Combine(App.DataDirectory, "updates", "install.log"), "/DIR=" + updateDirectory }) start.ArgumentList.Add(argument);
        for (var attempt = 1; ; attempt++)
        {
            try { if (Process.Start(start) is null) throw new IOException("Could not start the installer."); return; }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode is 5 or 32 && attempt < 8) { await Task.Delay(2000, lifetime.Token); }
        }
    }
}
