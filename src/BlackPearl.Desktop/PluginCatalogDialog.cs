using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Manage plugins: lists the plugins published in the BlackPearlPlugins repository and installs, updates or removes them
// in the plugins folder. changed runs after each change so the app reloads its plugins.
internal static class PluginCatalogDialog
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

    internal static InAppDialog Create(Window owner, string pluginsDirectory, Action changed, string? source = null)
    {
        var dialog = new InAppDialog { Owner = owner, Title = "Manage plugins", Width = 620, Height = 560 };
        NameScope.SetNameScope(dialog, new NameScope());
        var root = new DockPanel();
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        intro.Inlines.Add("Plugins published in ");
        var link = new Hyperlink(new Run("BlackPearlPlugins")) { NavigateUri = new Uri(PluginCatalog.PageUrl) };
        link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } e.Handled = true; };
        intro.Inlines.Add(link);
        intro.Inlines.Add(". Plugins run programs on this computer and download from the sites they name. Settings you save for a plugin are kept when it updates.");
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var close = new Button { Content = "Close", IsCancel = true }; DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var refresh = new Button { Content = "Refresh", Margin = new Thickness(0, 0, 8, 0) }; refresh.SetResourceReference(ContentControl.ContentTemplateProperty, "RefreshContent");
        DockPanel.SetDock(refresh, Dock.Right); footer.Children.Add(refresh);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); footer.Children.Add(status);
        var list = new StackPanel(); AutomationProperties.SetName(list, "Available plugins");
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dialog.RegisterName("CatalogStatus", status); dialog.RegisterName("CatalogList", list); dialog.RegisterName("CatalogRefresh", refresh);
        dialog.Content = root;

        PluginCatalog? catalog = null; var busy = false; var named = new List<string>();
        void Name(string key, FrameworkElement element) { dialog.RegisterName(key, element); named.Add(key); }
        void Show()
        {
            list.Children.Clear();
            foreach (var key in named) dialog.UnregisterName(key);
            named.Clear();
            if (catalog is null) return;
            foreach (var plugin in catalog.Plugins) list.Children.Add(Row(plugin));
            if (catalog.Plugins.Count == 0) status.Text = "The catalog has no plugins yet.";
        }
        UIElement Row(CatalogPlugin plugin)
        {
            var state = catalog!.State(plugin, pluginsDirectory);
            var row = new DockPanel { Margin = new Thickness(2, 6, 2, 6) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
            if (state != CatalogPluginState.Installed)
            {
                var install = new Button { Content = state == CatalogPluginState.NotInstalled ? "Install" : "Update" };
                install.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
                AutomationProperties.SetName(install, $"{install.Content} {plugin.Name}");
                install.Click += async (_, _) => await Run(state == CatalogPluginState.NotInstalled ? $"Installing {plugin.Name}…" : $"Updating {plugin.Name}…",
                    () => catalog.Install(plugin, pluginsDirectory), state == CatalogPluginState.NotInstalled ? $"Installed {plugin.Name}." : $"Updated {plugin.Name}.");
                Name("Install_" + Key(plugin), install); actions.Children.Add(install);
            }
            if (state != CatalogPluginState.NotInstalled)
            {
                var remove = new Button { Content = "Remove", Margin = new Thickness(8, 0, 0, 0) };
                AutomationProperties.SetName(remove, "Remove " + plugin.Name);
                remove.Click += async (_, _) =>
                {
                    if (!Dialogs.Confirm(owner, "Remove " + plugin.Name, $"Remove {plugin.Name} and the settings saved for it? You can install it again later.", "Remove")) return;
                    await Run($"Removing {plugin.Name}…", () => PluginCatalog.Remove(plugin, pluginsDirectory), $"Removed {plugin.Name}.");
                };
                Name("Remove_" + Key(plugin), remove); actions.Children.Add(remove);
            }
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(Border.BackgroundProperty, "Elevated");
            var glyph = new TextBlock { Text = "", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
            glyph.SetResourceReference(FrameworkElement.StyleProperty, "Icon"); glyph.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            icon.Child = glyph; DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = plugin.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            var label = state switch { CatalogPluginState.Installed => " · Installed", CatalogPluginState.UpdateAvailable => " · Update available", _ => "" };
            if (label.Length > 0) { var run = new Run(label) { FontWeight = FontWeights.Normal }; run.SetResourceReference(TextElement.ForegroundProperty, state == CatalogPluginState.UpdateAvailable ? "Accent" : "Muted"); name.Inlines.Add(run); }
            text.Children.Add(name);
            var detail = new TextBlock
            {
                Text = string.Join(" · ", plugin.ContentTypes.Select(ContentAcquisition.ContentTypeName)) + (plugin.Description.Length > 0 ? " — " + plugin.Description : ""),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); text.Children.Add(detail);
            row.Children.Add(text);
            AutomationProperties.SetName(row, plugin.Name + label);
            return row;
        }
        async Task Run(string working, Action change, string done)
        {
            if (busy) return;
            busy = true; list.IsEnabled = false; refresh.IsEnabled = false; status.Text = working;
            try { await Task.Run(change); status.Text = done; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { status.Text = "Couldn't change the plugin: " + ex.Message; }
            finally { busy = false; list.IsEnabled = true; refresh.IsEnabled = true; }
            // Even a half-finished change may have touched the plugins folder.
            changed(); Show();
        }
        async Task Load()
        {
            if (busy) return;
            busy = true; refresh.IsEnabled = false; status.Text = "Loading the plugin catalog…";
            try
            {
                catalog = await PluginCatalog.LoadAsync(http, source, CancellationToken.None);
                var updates = catalog.Plugins.Count(x => catalog.State(x, pluginsDirectory) == CatalogPluginState.UpdateAvailable);
                status.Text = $"{catalog.Plugins.Count} {(catalog.Plugins.Count == 1 ? "plugin" : "plugins")}" + (updates > 0 ? $" · {updates} {(updates == 1 ? "update" : "updates")} available" : "") +
                    (catalog.Problems.Count > 0 ? " · Not offered: " + string.Join("; ", catalog.Problems) : "");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
            { status.Text = "Couldn't load the plugin catalog: " + (ex is TaskCanceledException ? "the request timed out." : ex.Message); }
            finally { busy = false; refresh.IsEnabled = true; }
            Show();
        }
        refresh.Click += async (_, _) => await Load();
        dialog.Loaded += async (_, _) => { if (catalog is null && !busy) await Load(); };
        return dialog;
    }
    private static string Key(CatalogPlugin plugin) => new(plugin.Folder.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
