using System.IO;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private bool CanEditQueue()
    {
        if (room?.Identity?.Host == false) { SetStatus("The host controls the queue."); return false; }
        return true;
    }
    private IEnumerable<MediaItem> ResolveQueue(SavedQueue saved) => saved.MediaIds.Select(id => items.FirstOrDefault(x => x.Id == id)).OfType<MediaItem>();
    private void SaveActiveQueueEdits()
    {
        var index = savedQueues.FindIndex(x => x.Id == activeQueueId);
        if (index >= 0) savedQueues[index] = savedQueues[index] with { MediaIds = queue.Select(x => x.Id).ToArray() };
        SaveQueues(); RefreshQueuePickers();
    }
    private void SaveQueues()
    {
        library.Setting("queues", Wire.Serialize(savedQueues));
        library.Setting("activeQueue", activeQueueId ?? "");
    }
    private void RefreshQueuePickers(string? selectedId = null)
    {
        if (QueuePicker is null || PlayerQueuePicker is null) return;
        selectedId ??= (QueuePicker.SelectedItem as SavedQueue)?.Id ?? activeQueueId;
        updatingQueues = true;
        QueuePicker.ItemsSource = savedQueues.ToArray();
        QueuePicker.SelectedItem = savedQueues.FirstOrDefault(x => x.Id == selectedId) ?? savedQueues.FirstOrDefault();
        PlayerQueuePicker.ItemsSource = savedQueues.ToArray();
        PlayerQueuePicker.SelectedItem = savedQueues.FirstOrDefault(x => x.Id == activeQueueId);
        updatingQueues = false;
        RefreshSavedItems();
    }
    private void RefreshSavedItems() => SavedQueueItems.ItemsSource = QueuePicker.SelectedItem is SavedQueue saved ? ResolveQueue(saved).ToArray() : Array.Empty<MediaItem>();
    private void SavedQueueChanged(object sender, SelectionChangedEventArgs e) { if (!updatingQueues && SavedQueueItems is not null) RefreshSavedItems(); }
    private void SelectPlaybackQueue(SavedQueue saved)
    {
        activeQueueId = saved.Id; queue.Clear(); queuePosition = -1;
        foreach (var item in ResolveQueue(saved)) queue.Add(item);
        SaveQueues(); RefreshQueuePickers(saved.Id); PublishQueue();
    }
    private void PlayerQueueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingQueues || PlayerQueuePicker.SelectedItem is not SavedQueue saved) return;
        if (!CanEditQueue()) { RefreshQueuePickers(); return; }
        SelectPlaybackQueue(saved);
    }
    private void CreateQueue(object sender, RoutedEventArgs e)
    {
        var name = Dialogs.Prompt(this, "Create queue", "Queue name");
        if (name is null) return;
        var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []);
        savedQueues.Add(saved); SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void RenameQueue(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved) return;
        var name = Dialogs.Prompt(this, "Rename queue", "Queue name", saved.Name);
        if (name is null) return;
        savedQueues[savedQueues.IndexOf(saved)] = saved with { Name = name };
        SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void DeleteQueue(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved || !CanEditQueue()) return;
        savedQueues.Remove(saved);
        if (savedQueues.Count == 0) savedQueues.Add(new(Guid.NewGuid().ToString("N"), "Default queue", []));
        if (activeQueueId == saved.Id) SelectPlaybackQueue(savedQueues[0]);
        SaveQueues(); RefreshQueuePickers();
    }
    private void PlaySavedQueue(object sender, RoutedEventArgs e) => StartQueue(QueuePicker.SelectedItem as SavedQueue, sender, e);
    private void PlayPlayerQueue(object sender, RoutedEventArgs e) => StartQueue(PlayerQueuePicker.SelectedItem as SavedQueue, sender, e);
    private void StartQueue(SavedQueue? saved, object sender, RoutedEventArgs e)
    {
        if (saved is null || !CanEditQueue()) return;
        if (!ResolveQueue(saved).Any(x => File.Exists(x.Path))) { SetStatus("This queue has no available videos."); return; }
        SelectPlaybackQueue(saved); NextQueued(sender, e);
    }
    private void UpdateSavedQueue(SavedQueue saved, string[] ids)
    {
        var updated = saved with { MediaIds = ids };
        savedQueues[savedQueues.IndexOf(saved)] = updated;
        if (activeQueueId == saved.Id) SelectPlaybackQueue(updated);
        SaveQueues(); RefreshQueuePickers(saved.Id);
    }
    private void RemoveSavedItem(object sender, RoutedEventArgs e)
    {
        if (!CanEditQueue() || QueuePicker.SelectedItem is not SavedQueue saved || SavedQueueItems.SelectedIndex < 0) return;
        UpdateSavedQueue(saved, saved.MediaIds.Where((_, index) => index != SavedQueueItems.SelectedIndex).ToArray());
    }
    private void EditQueueOrder(object sender, RoutedEventArgs e)
    {
        if (!CanEditQueue() || QueuePicker.SelectedItem is not SavedQueue saved) return;
        var index = SavedQueueItems.SelectedIndex;
        var destination = index + ((string)((Button)sender).Tag == "Earlier" ? -1 : 1);
        if (index < 0 || destination < 0 || destination >= saved.MediaIds.Length) return;
        var ids = saved.MediaIds.ToArray(); (ids[index], ids[destination]) = (ids[destination], ids[index]);
        UpdateSavedQueue(saved, ids); SavedQueueItems.SelectedIndex = destination;
    }
    private void AddQueueVideos(object sender, RoutedEventArgs e)
    {
        if (QueuePicker.SelectedItem is not SavedQueue saved || !CanEditQueue()) return;
        var win = new Window { Owner = this, Title = "Add videos", Width = 650, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var add = new Button { Content = "Add selected", IsDefault = true }; DockPanel.SetDock(add, Dock.Bottom); panel.Children.Add(add);
        var list = new ListBox { ItemsSource = items.Where(x => x.Available).ToArray(), DisplayMemberPath = "QueueTitle", SelectionMode = SelectionMode.Extended }; panel.Children.Add(list);
        add.Click += (_, _) => { if (list.SelectedItems.Count > 0) win.DialogResult = true; };
        win.Content = panel;
        if (win.ShowDialog() == true) AddToSavedQueue(saved, list.SelectedItems.Cast<MediaItem>());
    }
    private void AddToSavedQueue(SavedQueue saved, IEnumerable<MediaItem> source)
    {
        if (!CanEditQueue()) return;
        var added = source.Where(x => x.Available && File.Exists(x.Path)).ToArray();
        if (added.Length == 0) { SetStatus("No available videos to add."); return; }
        savedQueues[savedQueues.IndexOf(saved)] = saved with { MediaIds = saved.MediaIds.Concat(added.Select(x => x.Id)).ToArray() };
        if (activeQueueId == saved.Id) { foreach (var item in added) queue.Add(item); PublishQueue(); }
        SaveQueues(); RefreshQueuePickers(saved.Id); SetStatus($"Added {added.Length} videos to {saved.Name}.");
    }
    private MenuItem QueueMenu(IEnumerable<MediaItem> source)
    {
        var videos = source.ToArray();
        var menu = new MenuItem { Header = "Add to queue", IsEnabled = room?.Identity?.Host != false };
        foreach (var saved in savedQueues)
        {
            var entry = new MenuItem { Header = saved.Name }; entry.Click += (_, _) => AddToSavedQueue(saved, videos); menu.Items.Add(entry);
        }
        var create = new MenuItem { Header = "New queue…" };
        create.Click += (_, _) =>
        {
            var name = Dialogs.Prompt(this, "Create queue", "Queue name"); if (name is null) return;
            var saved = new SavedQueue(Guid.NewGuid().ToString("N"), name, []); savedQueues.Add(saved); AddToSavedQueue(saved, videos);
        };
        menu.Items.Add(new Separator()); menu.Items.Add(create); return menu;
    }
    private void ShowAddToQueueMenu(FrameworkElement target, IEnumerable<MediaItem> source)
    {
        var menu = new ContextMenu { PlacementTarget = target }; menu.Items.Add(QueueMenu(source)); target.ContextMenu = menu; menu.IsOpen = true;
    }
    private void CardContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button button || button.Tag is not LibraryCard card) return;
        var videos = LibraryCatalog.CardItems(items, card).ToArray();
        var menu = new ContextMenu { PlacementTarget = button };
        var open = new MenuItem { Header = card.Level is "series" or "season" ? "Open" : "Details" }; open.Click += (_, _) => SelectMovie(button, new RoutedEventArgs()); menu.Items.Add(open);
        var play = new MenuItem { Header = card.Level is "series" or "season" ? "Play all" : "Play", IsEnabled = room?.Identity?.Host != false && videos.Any(x => x.Available && File.Exists(x.Path)) };
        play.Click += (_, _) =>
        {
            if (!CanEditQueue()) return;
            queue.Clear(); queuePosition = -1; activeQueueId = null; foreach (var video in videos.Where(x => x.Available && File.Exists(x.Path))) queue.Add(video);
            PublishQueue(); NextQueued(button, new RoutedEventArgs());
        };
        menu.Items.Add(play); menu.Items.Add(QueueMenu(videos));
        if (card.Level is "movie" or "episode")
        {
            var copy = new MenuItem { Header = "Copy file path" }; copy.Click += (_, _) => { try { Clipboard.SetText(card.Media.Path); } catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); } }; menu.Items.Add(copy);
        }
        button.ContextMenu = menu; menu.IsOpen = true; e.Handled = true;
    }
    private void ExcludedSelectionChanged(object sender, SelectionChangedEventArgs e) { if (RestoreFolderButton is not null) RestoreFolderButton.IsEnabled = !scanning && ExcludedFolderList.SelectedItem is string; }
    private void ExcludeFolder(object sender, RoutedEventArgs e)
    {
        if (scanning) return;
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Exclude folders from the library", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FolderNames.Where(path => !exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))) exclusions.Add(path);
        SaveExclusions();
    }
    private void RestoreFolder(object sender, RoutedEventArgs e)
    {
        if (scanning || ExcludedFolderList.SelectedItem is not string path) return;
        exclusions.Remove(path); SaveExclusions();
    }
    private void SaveExclusions()
    {
        library.Setting("exclusions", Wire.Serialize(exclusions)); RefreshLibrary(); scanDelay.Stop(); scanDelay.Start(); SetStatus("Folder exclusions updated.");
    }
}
