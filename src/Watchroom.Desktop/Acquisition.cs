using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Acquisition plugins: an Acquire action on missing and upcoming episodes, seasons and films hands the video to a
// plugin that supports its kind, and the files join the library when a scan finds them.
public partial class MainWindow
{
    private sealed record PluginRow(string Name, string Detail, AcquisitionPlugin Plugin)
    {
        public Visibility SettingsVisibility => Plugin.SettingsSchema is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private List<AcquisitionPlugin> acquisitionPlugins = [];
    private readonly HashSet<string> acquiring = new(StringComparer.OrdinalIgnoreCase);
    private static string PluginsDirectory => Environment.GetEnvironmentVariable("WATCHROOM_PLUGINS") ?? Path.Combine(App.DataDirectory, "plugins");

    private void LoadAcquisitionPlugins()
    {
        var (plugins, problems) = ContentAcquisition.Discover(PluginsDirectory);
        acquisitionPlugins = plugins;
        AcquisitionPluginList.ItemsSource = plugins.Select(x => new PluginRow(x.Name, string.Join(" · ", x.ContentTypes.Select(ContentAcquisition.ContentTypeName)) +
            (x.Description.Length > 0 ? " — " + x.Description : ""), x)).ToArray();
        AcquisitionPluginStatus.Text = (plugins.Count == 0 ? $"No plugins yet. Put a plugin's folder, with its plugin.json, in {PluginsDirectory}." : "") +
            (problems.Count == 0 ? "" : (plugins.Count == 0 ? "\n" : "") + "Not loaded: " + string.Join("\n", problems));
        AcquisitionPluginStatus.Visibility = AcquisitionPluginStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ReloadPluginsClick(object sender, RoutedEventArgs e)
    {
        LoadAcquisitionPlugins();
        SetStatus($"{acquisitionPlugins.Count} acquisition {(acquisitionPlugins.Count == 1 ? "plugin" : "plugins")} loaded.");
        if (selected is not null && currentPage == "Details") ShowAcquireDetail(selected);
        ShowAcquireSeason();
    }
    private void OpenPluginsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(PluginsDirectory);
            Process.Start(new ProcessStartInfo(PluginsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { SetStatus("Couldn't open the plugins folder: " + ex.Message); }
    }
    private void ConfigurePluginClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginRow row }) return;
        try { AcquisitionPluginSettings.Create(this, row.Plugin).ShowDialog(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        { Dialogs.Alert(this, row.Name + " settings", "Couldn't load plugin settings: " + ex.Message); }
    }
    private string AcquisitionPreferenceKey(AcquisitionPlugin plugin, AcquisitionRequest request) => "acquisition:" + plugin.Id + ":" + request.ContentType + ":" + request.Title + ":" + request.Year + ":" + request.Season;
    private AcquisitionOptions? AcquisitionPreferences(AcquisitionPlugin plugin, AcquisitionRequest request)
    {
        var json = library.Setting(AcquisitionPreferenceKey(plugin, request));
        try
        {
            if (!string.IsNullOrWhiteSpace(json)) return Wire.Read<AcquisitionOptions>(json);
            var path = Path.Combine(plugin.Directory, "settings.json");
            if (!File.Exists(path)) return null;
            var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            var title = settings?["titles"]?[request.Title];
            return new(title?["title"]?.GetValue<string>() ?? request.Title, title?["url"]?.GetValue<string>(),
                title?["resolution"]?.GetValue<int>() ?? settings?["resolution"]?.GetValue<int>(), title?["filter"]?.GetValue<string>(),
                title?["episodeOffset"]?.GetValue<int>(), title?["season"]?.GetValue<int>());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }
    private async Task PickAcquisition(AcquisitionRequest request, AcquisitionPlugin? initial = null)
    {
        var plugins = PluginsFor(request);
        if (plugins.Length == 0) return;
        var providerKey = "acquisition-provider:" + request.ContentType + ":" + request.Title + ":" + request.Year + ":" + request.Season;
        initial ??= plugins.FirstOrDefault(x => x.Id == library.Setting(providerKey));
        var dialog = new AcquisitionPicker(this, plugins, request, initial, plugin => AcquisitionPreferences(plugin, request), existing: ContentAcquisition.ExistingNames(request, items));
        if (dialog.ShowDialog() != true || dialog.Selection is not { } choice) return;
        if (choice.Remember && choice.Request.Options is { } options)
        {
            library.Setting(AcquisitionPreferenceKey(choice.Plugin, request), Wire.Serialize(options)); library.Setting(providerKey, choice.Plugin.Id);
        }
        await Acquire(choice.Plugin, choice.Request);
    }

    private AcquisitionPlugin[] PluginsFor(AcquisitionRequest request) => GuestLibrary ? [] : acquisitionPlugins.Where(x => x.Supports(request.ContentType)).ToArray();
    // What a card's Acquire would ask for: a placeholder episode or film, or a season with placeholders.
    private AcquisitionRequest? AcquisitionFor(LibraryCard card) => card.Level switch
    {
        "episode" when card.Media.IsVirtual => ContentAcquisition.ForEpisode(card.Media, items, folders),
        "season" when card.Season is { } season => ContentAcquisition.ForSeason(card.Media, season, items, folders),
        "movie" when card.Media.IsVirtual => ContentAcquisition.ForMovie(card.Media, folders),
        _ => null
    };
    private AcquisitionRequest? AcquisitionFor(MediaItem item) => !item.IsVirtual ? null : item.Series is null ? ContentAcquisition.ForMovie(item, folders) : ContentAcquisition.ForEpisode(item, items, folders);

    // The card menu's Acquire entry: one plugin is used directly, several are listed.
    private MenuItem? AcquireMenu(LibraryCard card)
    {
        if (acquisitionPlugins.Count == 0 || AcquisitionFor(card) is not { } request) return null;
        var plugins = PluginsFor(request);
        var header = request.Scope == "season" ? "Acquire season" : "Acquire";
        if (plugins.Length == 0) return new MenuItem { Header = header, IsEnabled = false, ToolTip = $"No plugin gets {ContentAcquisition.ContentTypeName(request.ContentType).ToLowerInvariant()}" };
        if (plugins.Length == 1) { var only = new MenuItem { Header = $"{header} with {plugins[0].Name}" }; only.Click += async (_, _) => await Guard(() => Acquire(plugins[0], request)); return only; }
        var menu = new MenuItem { Header = header };
        foreach (var plugin in plugins) { var entry = new MenuItem { Header = plugin.Name, ToolTip = plugin.Description.Length > 0 ? plugin.Description : null }; entry.Click += async (_, _) => await Guard(() => Acquire(plugin, request)); menu.Items.Add(entry); }
        return menu;
    }
    // The Acquire buttons: one plugin runs at once, several open a menu to pick from.
    private async Task ChoosePlugin(FrameworkElement target, AcquisitionRequest request)
    {
        var plugins = PluginsFor(request);
        if (plugins.Length == 0) { SetStatus($"No acquisition plugin gets {ContentAcquisition.ContentTypeName(request.ContentType).ToLowerInvariant()}."); return; }
        if (plugins.Any(x => x.Interactive)) { await PickAcquisition(request); return; }
        if (plugins.Length == 1) { await Acquire(plugins[0], request); return; }
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var plugin in plugins) { var entry = new MenuItem { Header = plugin.Name, ToolTip = plugin.Description.Length > 0 ? plugin.Description : null }; entry.Click += async (_, _) => await Guard(() => Acquire(plugin, request)); menu.Items.Add(entry); }
        target.ContextMenu = menu; menu.IsOpen = true;
    }
    private async Task Acquire(AcquisitionPlugin plugin, AcquisitionRequest request)
    {
        if (plugin.Interactive && request.Action is null) { await PickAcquisition(request, plugin); return; }
        var key = plugin.Id + "\n" + request.Scope + "\n" + request.Name;
        if (!acquiring.Add(key)) { SetStatus($"{plugin.Name} is already working on {request.Name}."); return; }
        try
        {
            SetStatus($"Asking {plugin.Name} for {request.Name}…");
            var result = await Task.Run(() => ContentAcquisition.RunAsync(plugin, request, lifetime.Token));
            if (closing) return;
            // A plugin's message that already names the video isn't prefixed with it again.
            var named = result.Message.Contains(request.Name, StringComparison.OrdinalIgnoreCase) || result.Message.Contains(request.Title, StringComparison.OrdinalIgnoreCase);
            var text = result.Success ? named ? result.Message : $"{request.Name} · {result.Message}" : $"{request.Name} · not acquired · {result.Message}";
            SetStatus(text); InAppDialog.Toast(this, text, TimeSpan.FromSeconds(result.Success ? 6 : 10));
            // A plugin that finished downloading leaves files for the next scan; the folder watcher may not see a folder it just made.
            if (result.Success) { scanDelay.Stop(); scanDelay.Start(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { acquiring.Remove(key); }
    }

    private void ShowAcquireDetail(MediaItem item)
    {
        var request = GuestLibrary ? null : AcquisitionFor(item);
        var plugins = request is null ? [] : PluginsFor(request);
        DetailAcquire.Visibility = plugins.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailAcquire.ToolTip = plugins.Length == 1 ? $"Ask {plugins[0].Name} to get {request!.Name}" : "Ask an acquisition plugin to get this video";
    }
    private async void AcquireDetailClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (selected is null || AcquisitionFor(selected) is not { } request) { SetStatus("There's nothing to acquire for this video."); return; }
        await ChoosePlugin(DetailAcquire, request);
    });

    // The season being browsed offers to acquire its missing and upcoming episodes.
    private void ShowAcquireSeason()
    {
        if (AcquireSeasonButton is null) return;
        var member = GuestLibrary || browseSeries is null || browseSeason is not { } season ? null : items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind) && (x.Season ?? -1) == season);
        var request = member is null || acquisitionPlugins.Count == 0 ? null : ContentAcquisition.ForSeason(member, browseSeason!.Value, items, folders);
        var plugins = request is null ? [] : PluginsFor(request);
        AcquireSeasonButton.Visibility = plugins.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (request is not null)
        {
            var count = request.Episodes.Length;
            AcquireSeasonButton.Content = $"Acquire season · {count} {(count == 1 ? "episode" : "episodes")}";
            AcquireSeasonButton.ToolTip = (plugins.Length == 1 ? $"Ask {plugins[0].Name} to get" : "Ask an acquisition plugin to get") + $" the missing and upcoming episodes of {request.Name}";
        }
    }
    private async void AcquireSeasonClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var member = browseSeries is null || browseSeason is not { } season ? null : items.FirstOrDefault(x => LibraryIdentity.InShow(x, browseSeries, browseKind) && (x.Season ?? -1) == season);
        if (member is null || ContentAcquisition.ForSeason(member, browseSeason!.Value, items, folders) is not { } request) { SetStatus("Nothing in this season is missing."); return; }
        await ChoosePlugin(AcquireSeasonButton, request);
    });
}
