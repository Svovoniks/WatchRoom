using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Settings > Plugins: Installed lists the plugins in the plugins folder, with Update when BlackPearlPlugins has a newer
// copy; Available lists the catalog's other plugins. The catalog loads the first time the tab opens, and each install,
// update or removal reloads the app's plugins.
public partial class MainWindow
{
    private sealed record PluginRow(string Name, string Description, string Types, AcquisitionPlugin Plugin, CatalogPlugin? Published, bool Update)
    {
        public Visibility SettingsVisibility => Plugin.SettingsSchema is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility UpdateVisibility => Update ? Visibility.Visible : Visibility.Collapsed;
        public string Spoken => Name + (Update ? ", update available" : "");
    }
    private sealed record CatalogRow(string Name, string Description, string Types, CatalogPlugin Plugin);
    private static readonly HttpClient pluginHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private PluginCatalog? pluginCatalog;
    private string? pluginCatalogSource = AppEnvironment.Get("PLUGIN_CATALOG");
    private bool pluginsBusy, pluginViewChosen;
    private static string PluginsDirectory => AppEnvironment.Get("PLUGINS") ?? Path.Combine(App.DataDirectory, "plugins");
    private static string TypeNames(IEnumerable<string> types) => string.Join(" · ", types.Select(ContentAcquisition.ContentTypeName));

    private void LoadAcquisitionPlugins()
    {
        var (plugins, problems) = ContentAcquisition.Discover(PluginsDirectory);
        acquisitionPlugins = plugins;
        AcquisitionPluginList.ItemsSource = plugins.Select(plugin =>
        {
            var published = pluginCatalog?.Plugins.FirstOrDefault(x => x.Folder.Equals(Path.GetFileName(plugin.Directory), StringComparison.OrdinalIgnoreCase));
            return new PluginRow(plugin.Name, plugin.Description, TypeNames(plugin.ContentTypes), plugin, published,
                published is not null && pluginCatalog!.State(published, PluginsDirectory) == CatalogPluginState.UpdateAvailable);
        }).ToArray();
        AcquisitionPluginStatus.Text = (plugins.Count == 0 ? "No plugins installed yet. Choose Available to install published ones." : "") +
            (problems.Count == 0 ? "" : (plugins.Count == 0 ? "\n" : "") + "Not loaded: " + string.Join("\n", problems));
        AcquisitionPluginStatus.Visibility = AcquisitionPluginStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CatalogPluginList.ItemsSource = pluginCatalog?.Plugins.Where(x => pluginCatalog.State(x, PluginsDirectory) == CatalogPluginState.NotInstalled)
            .Select(x => new CatalogRow(x.Name, x.Description, TypeNames(x.ContentTypes), x)).ToArray() ?? [];
        var available = CatalogPluginList.Items.Count;
        CatalogEmpty.Text = pluginCatalog is null || available > 0 ? "" : pluginCatalog.Plugins.Count == 0 ? "BlackPearlPlugins has no plugins yet." : "You have every published plugin.";
        InstalledPluginsView.Content = $"Installed · {plugins.Count}";
        AvailablePluginsView.Content = pluginCatalog is null ? "Available" : $"Available · {available}";
    }
    private void RefreshAcquisitionPlugins()
    {
        LoadAcquisitionPlugins();
        if (selected is not null && currentPage == "Details") ShowAcquireDetail(selected);
        ShowAcquireSeason();
    }
    // The first visit opens Available when nothing is installed, and loads the catalog for both views.
    private void ShowPluginsSection()
    {
        if (!pluginViewChosen) { pluginViewChosen = true; (acquisitionPlugins.Count == 0 ? AvailablePluginsView : InstalledPluginsView).IsChecked = true; }
        if (pluginCatalog is null && !pluginsBusy) _ = Guard(LoadPluginCatalog);
    }
    private void PluginViewChanged(object sender, RoutedEventArgs e)
    {
        if (InstalledPluginsPanel is null) return;
        var installed = InstalledPluginsView.IsChecked == true;
        InstalledPluginsPanel.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        AvailablePluginsPanel.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ReloadPluginsClick(object sender, RoutedEventArgs e)
    {
        RefreshAcquisitionPlugins();
        SetStatus($"{acquisitionPlugins.Count} acquisition {(acquisitionPlugins.Count == 1 ? "plugin" : "plugins")} loaded.");
        if (!pluginsBusy) _ = Guard(LoadPluginCatalog);
    }
    private async void CatalogRefreshClick(object sender, RoutedEventArgs e) => await Guard(LoadPluginCatalog);
    private async Task LoadPluginCatalog()
    {
        if (pluginsBusy) return;
        SetPluginsBusy(true, "Checking BlackPearlPlugins…");
        try
        {
            pluginCatalog = await PluginCatalog.LoadAsync(pluginHttp, pluginCatalogSource, lifetime.Token);
            RefreshAcquisitionPlugins();
            var updates = ((PluginRow[])AcquisitionPluginList.ItemsSource).Count(x => x.Update);
            CatalogStatus.Text = (updates > 0 ? $"{updates} {(updates == 1 ? "update" : "updates")} available." : acquisitionPlugins.Count > 0 ? "Your plugins are up to date." : "") +
                (pluginCatalog.Problems.Count > 0 ? " Not offered: " + string.Join("; ", pluginCatalog.Problems) : "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (lifetime.IsCancellationRequested) return;
            CatalogStatus.Text = "Couldn't check BlackPearlPlugins: " + (ex is TaskCanceledException ? "the request timed out." : ex.Message);
        }
        finally { SetPluginsBusy(false, null); }
    }
    private void SetPluginsBusy(bool busy, string? status)
    {
        pluginsBusy = busy;
        AcquisitionPluginList.IsEnabled = CatalogPluginList.IsEnabled = CatalogRefresh.IsEnabled = ReloadPluginsButton.IsEnabled = !busy;
        if (status is not null) CatalogStatus.Text = status;
    }
    // Installs, updates and removals run off the UI thread; the plugins reload even after a half-finished change.
    private async Task ChangePlugins(string working, Action change, string done)
    {
        if (pluginsBusy) return;
        SetPluginsBusy(true, working);
        try { await Task.Run(change); CatalogStatus.Text = done; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { CatalogStatus.Text = "Couldn't change the plugin: " + ex.Message; }
        finally { SetPluginsBusy(false, null); RefreshAcquisitionPlugins(); }
    }
    private async void InstallCatalogPluginClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (sender is not FrameworkElement { DataContext: CatalogRow row } || pluginCatalog is not { } catalog) return;
        await ChangePlugins($"Installing {row.Name}…", () => catalog.Install(row.Plugin, PluginsDirectory), $"Installed {row.Name}. It's under Installed.");
    });
    private async void UpdatePluginClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (sender is not FrameworkElement { DataContext: PluginRow { Published: { } published } row } || pluginCatalog is not { } catalog) return;
        await ChangePlugins($"Updating {row.Name}…", () => catalog.Install(published, PluginsDirectory), $"Updated {row.Name}.");
    });
    private async void RemovePluginClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (sender is not FrameworkElement { DataContext: PluginRow row }) return;
        if (!Dialogs.Confirm(this, "Remove " + row.Name, $"Remove {row.Name} and the settings saved for it? You can install it again later.", "Remove")) return;
        // A plugin from the catalog takes its unused support folders with it; any other plugin has its folder deleted.
        var folder = Path.GetFileName(row.Plugin.Directory);
        var entry = row.Published ?? new CatalogPlugin(folder, row.Plugin.Id, row.Plugin.Name, row.Plugin.Description, row.Plugin.ContentTypes, [folder]);
        await ChangePlugins($"Removing {row.Name}…", () => PluginCatalog.Remove(entry, PluginsDirectory), $"Removed {row.Name}.");
    });
    private void OpenCatalogPage(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
        e.Handled = true;
    }
}
