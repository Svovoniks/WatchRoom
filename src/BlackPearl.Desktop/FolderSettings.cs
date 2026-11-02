using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;
using Microsoft.Win32;

namespace BlackPearl.Desktop;

// Settings > Folders: the library folders on the left and the selected one on the right, with its category and the
// subfolders scans skip. Each skipped subfolder is shown under the deepest library folder that holds it.
public partial class MainWindow
{
    private sealed record FolderRow(LibraryFolder Folder, string Name, string Location, string Caption, int Count)
    {
        public string Glyph => Folder.Kind == MusicFiles.Kind ? "" : "";
        public string Spoken => $"{Folder.Path}, {Caption}";
    }
    private sealed record ExclusionRow(string Path, string Name);
    private readonly ObservableCollection<FolderRow> folderRows = [];
    private bool showingFolder;

    private void InitializeFolderSettings()
    {
        FolderList.ItemsSource = folderRows;
        folders.CollectionChanged += (_, _) => RefreshFolderRows();
        exclusions.CollectionChanged += (_, _) => ShowSelectedFolder();
        RefreshFolderRows();
        // Settings first shows Folders before the rows exist.
        if (preferenceSection == "Folders") SelectFirstFolder();
    }
    private static string FolderName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
    // A drive root such as E:\ keeps its separator when trimmed, so it isn't given a second one.
    private static string FolderPrefix(string folder) =>
        Path.TrimEndingDirectorySeparator(folder) is var root && Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    private static bool Inside(string path, string folder) =>
        FolderPrefix(folder) is var prefix && path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    // The deepest library folder holding a path, so a Music folder inside a Mixed one keeps its own files.
    private LibraryFolder? FolderOf(string path) => folders.Where(x => Inside(path, x.Path)).MaxBy(x => x.Path.Length);

    // Rebuilt when folders change and after each library refresh, keeping the selected folder selected.
    private void RefreshFolderRows()
    {
        var chosen = (FolderList.SelectedItem as FolderRow)?.Folder.Path;
        // Runs after every library refresh, so each folder's prefix is built once, deepest first.
        var prefixes = folders.Select(x => (Folder: x, Prefix: FolderPrefix(x.Path)))
            .OrderByDescending(x => x.Prefix.Length).ToArray();
        var counts = new Dictionary<LibraryFolder, int>();
        foreach (var item in items)
        {
            if (item.IsVirtual) continue;
            foreach (var (folder, prefix) in prefixes)
                if (item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { counts[folder] = counts.GetValueOrDefault(folder) + 1; break; }
        }
        var rows = new List<FolderRow>();
        foreach (var folder in folders)
        {
            var count = counts.GetValueOrDefault(folder);
            var noun = folder.Kind == MusicFiles.Kind ? count == 1 ? "song" : "songs" : count == 1 ? "video" : "videos";
            // Where the folder is: its parent, or for a folder inside another library folder, the path from that one.
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder.Path));
            var outer = folders.Where(x => x != folder && Inside(folder.Path, x.Path)).MaxBy(x => x.Path.Length);
            var location = parent is null ? "" : outer is null ? parent
                : Path.GetRelativePath(outer.Path, parent) is "." ? FolderName(outer.Path) : Path.Combine(FolderName(outer.Path), Path.GetRelativePath(outer.Path, parent));
            rows.Add(new(folder, FolderName(folder.Path), location.Length == 0 ? "" : "  " + location, $"{KindName(folder.Kind)} · {count:N0} {noun}", count));
        }
        // An unchanged list keeps its items, so a refresh doesn't take keyboard focus out of it.
        if (rows.SequenceEqual(folderRows)) { ShowSelectedFolder(); return; }
        folderRows.Clear();
        foreach (var row in rows) folderRows.Add(row);
        FolderList.SelectedItem = folderRows.FirstOrDefault(x => x.Folder.Path.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        ShowSelectedFolder();
    }
    private static string KindName(string kind) => kind switch { "Show" => "Shows", "Movie" => "Movies", _ => kind };

    private void FolderSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedFolder();
    private void ShowSelectedFolder()
    {
        if (FolderEditor is null) return;
        var row = FolderList.SelectedItem as FolderRow;
        FolderEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        FolderEmptyDetail.Visibility = row is null && folderRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var other = exclusions.Where(x => FolderOf(x) is null).Select(x => new ExclusionRow(x, x)).ToArray();
        OtherExclusionList.ItemsSource = other;
        OtherExclusionsPanel.Visibility = other.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (row is null) return;
        FolderTitle.Text = row.Name;
        FolderDetail.Text = $"{row.Folder.Path} · {row.Caption[(row.Caption.IndexOf('·') + 2)..]}";
        showingFolder = true;
        try { foreach (var choice in FolderKinds.Children.OfType<RadioButton>()) choice.IsChecked = (string)choice.Tag == row.Folder.Kind; }
        finally { showingFolder = false; }
        var skipped = exclusions.Where(x => FolderOf(x) == row.Folder).Order(StringComparer.OrdinalIgnoreCase)
            .Select(x => new ExclusionRow(x, Path.GetRelativePath(row.Folder.Path, x))).ToArray();
        FolderExclusionList.ItemsSource = skipped;
        FolderExclusionsLabel.Text = skipped.Length == 0 ? "Skipped subfolders" : $"Skipped subfolders · {skipped.Length}";
        FolderNoExclusions.Visibility = skipped.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFolderControls();
    }
    // A scan reads the folders and exclusions, so they don't change until it finishes.
    private void UpdateFolderControls()
    {
        if (FolderKinds is null) return;
        var selectedFolder = FolderList.SelectedItem is FolderRow;
        AddFolderButton.IsEnabled = MixedFolderMusic.IsEnabled = FolderKinds.IsEnabled = !scanning;
        FolderExclusionList.IsEnabled = OtherExclusionList.IsEnabled = !scanning;
        ExcludeFolderButton.IsEnabled = RemoveFolderButton.IsEnabled = !scanning && selectedFolder;
        ScanButton.IsEnabled = !scanning && folders.Count > 0;
    }
    // Settings opens Folders with a folder showing, rather than an empty pane.
    private void SelectFirstFolder() { if (FolderList.SelectedItem is null && folderRows.Count > 0) FolderList.SelectedIndex = 0; }

    private void AddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a movie, show, anime or music folder", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        var added = picker.FolderNames.Where(path => !folders.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var path in added) folders.Add(new(path, "Mixed"));
        SaveFolders();
        if (added.Length > 0) FolderList.SelectedItem = folderRows.FirstOrDefault(x => x.Folder.Path == added[^1]);
        scanDelay.Stop(); scanDelay.Start(); SetStatus("Folder added. Scanning your library…");
    }
    private void RemoveFolder(object sender, RoutedEventArgs e)
    {
        if (scanning || FolderList.SelectedItem is not FolderRow row) return;
        if (!Dialogs.Confirm(this, "Remove " + row.Name, $"Stop scanning {row.Folder.Path}? Its files stay where they are, and adding the folder again brings them back.", "Remove folder")) return;
        folders.Remove(row.Folder); SaveFolders(); scanDelay.Stop(); scanDelay.Start();
        _ = Guard(async () => { await RefreshLibrary(); SetStatus("Folder removed from library."); });
    }
    private void FolderKindChecked(object sender, RoutedEventArgs e)
    {
        if (showingFolder || scanning || FolderList.SelectedItem is not FolderRow row || sender is not RadioButton { Tag: string kind } || kind == row.Folder.Kind) return;
        folders[folders.IndexOf(row.Folder)] = row.Folder with { Kind = kind };
        SaveFolders(); scanDelay.Stop(); scanDelay.Start(); SetStatus($"{row.Name} is now a {KindName(kind)} folder. Scanning your library…");
    }
    private void OpenSelectedFolder(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderRow row) return;
        try { Process.Start(new ProcessStartInfo(row.Folder.Path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { SetStatus("Couldn't open the folder: " + ex.Message); }
    }

    private void ExcludeFolder(object sender, RoutedEventArgs e)
    {
        if (scanning || FolderList.SelectedItem is not FolderRow row) return;
        var picker = new OpenFolderDialog { Title = $"Choose subfolders of {row.Name} to skip", Multiselect = true, InitialDirectory = row.Folder.Path };
        if (picker.ShowDialog(this) != true) return;
        var chosen = picker.FolderNames.Where(path => !exclusions.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (chosen.Any(path => !Inside(path, row.Folder.Path)))
        {
            Dialogs.Alert(this, "Choose a subfolder", $"Choose folders inside {row.Folder.Path}. To leave out a whole library folder, remove it instead.");
            chosen = chosen.Where(path => Inside(path, row.Folder.Path)).ToArray();
        }
        if (chosen.Length == 0) return;
        foreach (var path in chosen) exclusions.Add(path);
        SaveExclusions();
    }
    private void RestoreFolder(object sender, RoutedEventArgs e)
    {
        if (scanning || sender is not FrameworkElement { DataContext: ExclusionRow row }) return;
        exclusions.Remove(row.Path); SaveExclusions();
    }
    private void SaveExclusions()
    {
        library.Setting("exclusions", Wire.Serialize(exclusions)); scanDelay.Stop(); scanDelay.Start();
        _ = Guard(async () => { await RefreshLibrary(); SetStatus("Skipped subfolders updated."); });
    }
}
