using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlackPearl.Core;

// A plugin the catalog offers. Folders lists the folders it installs: its own first, then the support folders it uses,
// such as a shared runtime its plugin.json reaches with ../runtime/.
// Group: the plugin family its plugin.json names, if any.
public record CatalogPlugin(string Folder, string Id, string Name, string Description, string[] ContentTypes, string[] Folders, string? Group = null);
public enum CatalogPluginState { NotInstalled, Installed, UpdateAvailable }
// Plugins whose plugin.json names the same group. A null Name holds the plugins that name none.
public sealed record PluginGroup<T>(string? Name, IReadOnlyList<T> Members);

// The acquisition plugins published in the BlackPearlPlugins repository. Its plugins folder holds one folder per
// plugin, the same layout as the local plugins folder, so installing copies folders across. Each folder with a
// plugin.json is offered; folders without one are support folders installed with the plugins that use them.
public sealed partial class PluginCatalog
{
    public const string Repository = "Svovoniks/BlackPearlPlugins";
    public static readonly string ArchiveUrl = $"https://codeload.github.com/{Repository}/zip/HEAD";
    public static readonly string PageUrl = $"https://github.com/{Repository}";
    private const long MaxArchiveBytes = 64L << 20;
    private const int MaxFiles = 5000;
    private static readonly HashSet<string> LocalFiles = new(["settings.json"], StringComparer.OrdinalIgnoreCase);

    // Folder name → path within the folder (with '/') → contents.
    private readonly Dictionary<string, Dictionary<string, byte[]>> folders;
    public IReadOnlyList<CatalogPlugin> Plugins { get; }
    public IReadOnlyList<string> Problems { get; }

    private PluginCatalog(Dictionary<string, Dictionary<string, byte[]>> folders)
    {
        this.folders = folders;
        var plugins = new List<CatalogPlugin>(); var problems = new List<string>();
        foreach (var (folder, files) in folders.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!files.TryGetValue(ContentAcquisition.ManifestName, out var json)) continue;
            try
            {
                var manifest = Wire.Read<AcquisitionManifest>(System.Text.Encoding.UTF8.GetString(json));
                var types = (manifest.ContentTypes ?? []).Select(ContentAcquisition.NormalizeType).ToArray();
                if (string.IsNullOrWhiteSpace(manifest.Command) || types.Length == 0 || types.Any(x => x is null)) { problems.Add($"{folder}: plugin.json isn't a usable plugin."); continue; }
                var needed = Requirements(manifest).ToArray();
                if (needed.FirstOrDefault(x => !folders.ContainsKey(x)) is { } absent) { problems.Add($"{folder}: needs {absent}, which the catalog doesn't have."); continue; }
                plugins.Add(new(folder, string.IsNullOrWhiteSpace(manifest.Id) ? folder : manifest.Id.Trim(), string.IsNullOrWhiteSpace(manifest.Name) ? folder : manifest.Name.Trim(),
                    manifest.Description?.Trim() ?? "", types.OfType<string>().Distinct().ToArray(), [folder, .. needed.Where(x => !x.Equals(folder, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)],
                    ContentAcquisition.GroupName(manifest.Group)));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { problems.Add($"{folder}: plugin.json can't be read: {ex.Message}"); }
        }
        Plugins = plugins; Problems = problems;
    }

    // The sibling folders a manifest uses: those named in requires, and those its command, arguments and settings
    // schema reach with ../folder/.
    public static IEnumerable<string> Requirements(AcquisitionManifest manifest)
    {
        foreach (var name in manifest.Requires ?? []) if (ValidFolder(name.Trim())) yield return name.Trim();
        foreach (var path in new[] { manifest.Command, manifest.SettingsSchema }.Concat(manifest.Arguments ?? []))
            if (path is not null && SiblingPath().Match(path.Trim()) is { Success: true } match && ValidFolder(match.Groups[1].Value)) yield return match.Groups[1].Value;
    }
    [GeneratedRegex(@"^\.\.[\\/]([^\\/]+)[\\/]")] private static partial Regex SiblingPath();

    // Plugins that name the same group in plugin.json (ignoring case), such as the Sparrow ports that share one runtime,
    // are shown and updated together. Groups come first by name, members in their given order, then one unnamed group
    // of the plugins that name none. A group's name is spelled as its first member spells it.
    public static IReadOnlyList<PluginGroup<T>> Group<T>(IReadOnlyList<T> items, Func<T, string?> group)
    {
        var named = items.Where(x => group(x) is not null).GroupBy(x => group(x)!, StringComparer.OrdinalIgnoreCase)
            .Select(x => new PluginGroup<T>(group(x.First()), x.ToArray())).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var alone = items.Where(x => group(x) is null).ToArray();
        if (alone.Length > 0) named.Add(new(null, alone));
        return named;
    }
    private static bool ValidFolder(string name) => name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name.Trim() == name && !name.EndsWith('.');

    // A GitHub source archive: one top folder holding the repository, whose plugins folder is read.
    public static PluginCatalog FromArchive(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var folders = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        long total = 0; var count = 0;
        foreach (var entry in zip.Entries)
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            // top/plugins/<folder>/<path…>; directory entries end with an empty part.
            if (parts.Length < 4 || parts[1] != "plugins" || parts[^1].Length == 0) continue;
            if (++count > MaxFiles || (total += entry.Length) > MaxArchiveBytes) throw new InvalidDataException("The plugin catalog is too large.");
            Add(folders, parts[2], parts[3..], () => { using var input = entry.Open(); using var copy = new MemoryStream(); input.CopyTo(copy); return copy.ToArray(); });
        }
        return new(folders);
    }
    // A checkout of the repository, or its plugins folder.
    public static PluginCatalog FromDirectory(string directory)
    {
        var root = Directory.Exists(Path.Combine(directory, "plugins")) ? Path.Combine(directory, "plugins") : directory;
        var folders = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.GetDirectories(root))
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                Add(folders, Path.GetFileName(folder), Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), () => File.ReadAllBytes(file));
        return new(folders);
    }
    private static void Add(Dictionary<string, Dictionary<string, byte[]>> folders, string folder, string[] path, Func<byte[]> read)
    {
        // Names that would leave the folder, or that Windows can't hold, are skipped rather than trusted.
        if (!ValidFolder(folder) || path.Length == 0 || path.Any(x => !ValidFolder(x)) || path[0].StartsWith('.')) return;
        if (path.Length == 1 && LocalFiles.Contains(path[0])) return;
        if (!folders.TryGetValue(folder, out var files)) folders[folder] = files = new(StringComparer.OrdinalIgnoreCase);
        files[string.Join('/', path)] = read();
    }

    // The published catalog, or a local archive or checkout named by source (for testing plugins before publishing).
    public static async Task<PluginCatalog> LoadAsync(HttpClient http, string? source, CancellationToken ct)
    {
        if (source is { Length: > 0 } && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Directory.Exists(source) ? FromDirectory(source) : FromArchive(new MemoryStream(await File.ReadAllBytesAsync(source, ct)));
        using var response = await http.GetAsync(source is { Length: > 0 } ? source : ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidDataException("The plugin catalog is too large.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream(); var chunk = new byte[81920];
        for (int read; (read = await body.ReadAsync(chunk, ct)) > 0;)
        {
            if (buffer.Length + read > MaxArchiveBytes) throw new InvalidDataException("The plugin catalog is too large.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return FromArchive(buffer);
    }

    // Installed when its plugin.json is in the plugins folder; up to date when every file the catalog ships for it
    // and its support folders is there unchanged. Settings the user saved don't count.
    public CatalogPluginState State(CatalogPlugin plugin, string pluginsDirectory)
    {
        if (!File.Exists(Path.Combine(pluginsDirectory, plugin.Folder, ContentAcquisition.ManifestName))) return CatalogPluginState.NotInstalled;
        foreach (var folder in plugin.Folders)
            foreach (var (path, contents) in folders[folder])
            {
                var target = Target(pluginsDirectory, folder, path);
                try { if (!File.Exists(target) || new FileInfo(target).Length != contents.Length || !File.ReadAllBytes(target).AsSpan().SequenceEqual(contents)) return CatalogPluginState.UpdateAvailable; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return CatalogPluginState.UpdateAvailable; }
            }
        return CatalogPluginState.Installed;
    }

    // Copies the plugin and its support folders into the plugins folder, replacing the files the catalog ships and
    // keeping everything else, such as settings.json. Support folders go first and plugin.json last, so a reload in
    // between never finds a plugin whose files aren't all there.
    public void Install(CatalogPlugin plugin, string pluginsDirectory)
    {
        foreach (var folder in plugin.Folders.Reverse())
            foreach (var (path, contents) in folders[folder].OrderBy(x => x.Key.Equals(ContentAcquisition.ManifestName, StringComparison.OrdinalIgnoreCase)))
            {
                var target = Target(pluginsDirectory, folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, contents); File.Move(temporary, target, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
    }

    // Deletes the plugin's folder, with its settings, and each support folder no remaining plugin uses.
    public static void Remove(CatalogPlugin plugin, string pluginsDirectory)
    {
        var own = Target(pluginsDirectory, plugin.Folder, null);
        if (Directory.Exists(own)) Directory.Delete(own, true);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.Exists(pluginsDirectory) ? Directory.GetDirectories(pluginsDirectory) : [])
        {
            var manifest = Path.Combine(folder, ContentAcquisition.ManifestName);
            if (!File.Exists(manifest)) continue;
            used.Add(Path.GetFileName(folder));
            try { used.UnionWith(Requirements(Wire.Read<AcquisitionManifest>(File.ReadAllText(manifest)))); }
            // An unreadable manifest might use anything, so nothing is removed from under it.
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException) { return; }
        }
        foreach (var support in plugin.Folders.Skip(1).Where(x => !used.Contains(x)))
            if (Target(pluginsDirectory, support, null) is var path && Directory.Exists(path)) Directory.Delete(path, true);
    }

    private static string Target(string pluginsDirectory, string folder, string? path)
    {
        var root = Path.GetFullPath(Path.Combine(pluginsDirectory, folder));
        if (path is null) return root;
        var target = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? target : throw new InvalidDataException($"{folder}: {path} is outside the plugin's folder.");
    }
}
