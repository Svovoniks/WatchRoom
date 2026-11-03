using System.IO.Compression;
using System.Text;
using BlackPearl.Core;

// The plugin catalog: plugins published in BlackPearlPlugins, installed into the plugins folder with the support
// folders they use, updated in place without losing their settings, and removed with what only they used.
static class PluginCatalogFixture
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var plugins = Path.Combine(directory, "catalog-installed");
        var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry("BlackPearlPlugins-HEAD/" + name).Open(), new UTF8Encoding(false)); writer.Write(text); }
            Entry("README.md", "not a plugin");
            Entry("plugins/runtime/run.ps1", "'runtime v1'");
            Entry("plugins/runtime/lib/helpers.ps1", "'helpers'");
            Entry("plugins/runtime/settings.schema.json", "[]");
            Entry("plugins/alpha/plugin.json", """{ "name": "Alpha", "description": "Gets anime", "contentTypes": ["anime"], "command": "../runtime/run.ps1", "settingsSchema": "../runtime/settings.schema.json", "group": " Pack " }""");
            Entry("plugins/alpha/settings.json", """{ "shipped": true }""");
            Entry("plugins/beta/plugin.json", """{ "id": "beta-id", "name": "Beta", "contentTypes": ["movie", "tv"], "command": "beta.cmd", "requires": ["runtime"], "group": "pack" }""");
            Entry("plugins/beta/beta.cmd", "@echo off");
            Entry("plugins/orphan/plugin.json", """{ "name": "Orphan", "contentTypes": ["show"], "command": "../absent/run.ps1" }""");
            Entry("plugins/broken/plugin.json", "{ nope");
            Entry("plugins/alpha/../../escape.txt", "outside");
            Entry("plugins/../escape.txt", "outside");
        }
        archive.Position = 0;
        var catalog = PluginCatalog.FromArchive(archive);
        check(catalog.Plugins.Select(x => x.Name).SequenceEqual(["Alpha", "Beta"]) && catalog.Plugins[1] is { Id: "beta-id", Folder: "beta" } beta && beta.ContentTypes.SequenceEqual(["movie", "show"]),
            "the catalog offers each published folder with a usable plugin.json, by name and content type");
        check(catalog.Problems.Count == 2 && catalog.Problems.Any(x => x.StartsWith("orphan:") && x.Contains("absent")) && catalog.Problems.Any(x => x.StartsWith("broken:")),
            "a catalog plugin missing a folder it needs, or with an unreadable manifest, is reported rather than offered");
        var alpha = catalog.Plugins[0]; var betaPlugin = catalog.Plugins[1];
        check(alpha.Folders.SequenceEqual(["alpha", "runtime"]) && betaPlugin.Folders.SequenceEqual(["beta", "runtime"]),
            "a plugin brings the support folders its command or settings reach with ../, and those it requires");
        check(catalog.State(alpha, plugins) == CatalogPluginState.NotInstalled, "a plugin not in the plugins folder is not installed");
        check(catalog.Plugins[0].Group == "Pack" && PluginCatalog.Group(catalog.Plugins, x => x.Group) is [{ Name: "Pack", Members: [{ Name: "Alpha" }, { Name: "Beta" }] }],
            "plugins whose plugin.json names the same group, ignoring case and spaces, form one group");
        var grouped = PluginCatalog.Group(["solo", "z1", "a1", "z2"], x => x switch { "z1" or "z2" => "Zeta", "a1" => "Alpha", _ => null });
        check(grouped is [{ Name: "Alpha", Members: ["a1"] }, { Name: "Zeta", Members: ["z1", "z2"] }, { Name: null, Members: ["solo"] }],
            "groups come by name, even with one plugin, and plugins that name no group follow them unnamed");

        catalog.Install(alpha, plugins);
        check(catalog.State(alpha, plugins) == CatalogPluginState.Installed && File.ReadAllText(Path.Combine(plugins, "runtime", "lib", "helpers.ps1")) == "'helpers'"
            && ContentAcquisition.Discover(plugins).Plugins.Single() is { Id: "alpha", Name: "Alpha", Group: "Pack" },
            "installing copies the plugin and its support folders, nested files included, and the app loads it");
        check(!File.Exists(Path.Combine(plugins, "alpha", "settings.json")) && !File.Exists(Path.Combine(plugins, "escape.txt")) && !File.Exists(Path.Combine(directory, "escape.txt")),
            "the catalog never ships settings.json and never writes outside the plugin's folders");

        File.WriteAllText(Path.Combine(plugins, "alpha", "settings.json"), """{ "downloader": "qbittorrent" }""");
        check(catalog.State(alpha, plugins) == CatalogPluginState.Installed, "settings the user saved don't make a plugin look changed");
        File.WriteAllText(Path.Combine(plugins, "runtime", "run.ps1"), "'runtime v0'");
        check(catalog.State(alpha, plugins) == CatalogPluginState.UpdateAvailable, "a plugin whose shipped files differ from the catalog's has an update");
        catalog.Install(alpha, plugins);
        check(catalog.State(alpha, plugins) == CatalogPluginState.Installed && File.ReadAllText(Path.Combine(plugins, "runtime", "run.ps1")) == "'runtime v1'"
            && File.ReadAllText(Path.Combine(plugins, "alpha", "settings.json")).Contains("qbittorrent"), "updating replaces shipped files and keeps the plugin's settings");

        catalog.Install(betaPlugin, plugins);
        PluginCatalog.Remove(alpha, plugins);
        check(!Directory.Exists(Path.Combine(plugins, "alpha")) && Directory.Exists(Path.Combine(plugins, "runtime")) && catalog.State(betaPlugin, plugins) == CatalogPluginState.Installed,
            "removing a plugin keeps support folders another installed plugin still uses");
        PluginCatalog.Remove(betaPlugin, plugins);
        check(!Directory.Exists(Path.Combine(plugins, "beta")) && !Directory.Exists(Path.Combine(plugins, "runtime")), "removing the last plugin that uses a support folder removes it too");

        // A checkout of the repository works as a catalog too, for trying plugins before they're published.
        var checkout = Path.Combine(directory, "catalog-checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "plugins", "gamma"));
        File.WriteAllText(Path.Combine(checkout, "plugins", "gamma", "plugin.json"), """{ "name": "Gamma", "contentTypes": ["show"], "command": "g.cmd" }""");
        File.WriteAllText(Path.Combine(checkout, "plugins", "gamma", "g.cmd"), "@echo off");
        var local = PluginCatalog.LoadAsync(new HttpClient(), checkout, CancellationToken.None).GetAwaiter().GetResult();
        check(local.Plugins.Single() is { Name: "Gamma", Folders: ["gamma"] }, "a local checkout of the plugins repository can stand in for the published catalog");
    }
}
