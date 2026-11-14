using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// A video page's "File details": the file's location, then everything its headers say about it (VideoFileDetails), one
// column per part: the file, each track, chapters and attachments. Read in the background, and only once the section is
// open: an open section stays open from page to page, and each page reads its own file.
public partial class MainWindow
{
    private const double FileInfoColumn = 340, FileInfoLabel = 150;
    private int fileInfoVersion;
    private string? fileInfoPath;
    private IReadOnlyList<FileDetailGroup> fileInfoGroups = [];

    /// <summary>Clears the details of the previous page, and reads this page's file when the section is open.</summary>
    private void ShowDetailFileInfo()
    {
        // The same file again (the page refreshed after a scan or a rename): keep what was read, or is being read.
        if (fileInfoPath is not null && fileInfoPath == DetailFile.Text) return;
        fileInfoVersion++; fileInfoPath = null; fileInfoGroups = [];
        DetailFileInfo.Children.Clear();
        DetailFileInfoStatus.Visibility = DetailFileInfoCopy.Visibility = Visibility.Collapsed;
        if (DetailFileExpander.IsExpanded) LoadDetailFileInfo();
    }
    private void DetailFileExpanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DetailFileExpander)) LoadDetailFileInfo();
    }
    private async void LoadDetailFileInfo()
    {
        var path = DetailFile.Text;
        if (closing || string.IsNullOrEmpty(path) || fileInfoPath == path) return;
        fileInfoPath = path;
        var version = ++fileInfoVersion;
        DetailFileInfo.Children.Clear();
        DetailFileInfoCopy.Visibility = Visibility.Collapsed;
        DetailFileInfoStatus.Text = "Reading the file…"; DetailFileInfoStatus.Visibility = Visibility.Visible;
        IReadOnlyList<FileDetailGroup> groups;
        try { groups = await Task.Run(() => VideoFileDetails.Read(path, lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { groups = []; } // shown as unreadable, and read again next time
        if (closing || version != fileInfoVersion) return;
        fileInfoGroups = groups;
        if (groups.Count == 0)
        {
            // Not kept as read: opening the section again tries again, once the drive is back.
            fileInfoPath = null;
            DetailFileInfoStatus.Text = "This file can't be read right now. Reconnect its drive, then open File details again.";
            return;
        }
        DetailFileInfoStatus.Visibility = Visibility.Collapsed;
        foreach (var group in groups) DetailFileInfo.Children.Add(FileInfoGroup(group));
        DetailFileInfoCopy.Visibility = Visibility.Visible;
    }

    private static StackPanel FileInfoGroup(FileDetailGroup group)
    {
        var panel = new StackPanel { Width = FileInfoColumn, Margin = new Thickness(0, 0, 36, 22) };
        AutomationProperties.SetName(panel, group.Title);
        var title = new TextBlock { Text = group.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        panel.Children.Add(title);
        var rows = new Grid();
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(FileInfoLabel) });
        rows.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var detail in group.Details)
        {
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = detail.Label, Margin = new Thickness(0, 1, 12, 1) };
            label.SetResourceReference(StyleProperty, "MutedText");
            var value = new TextBlock { Text = detail.Value, TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 1, 0, 1) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            AutomationProperties.SetName(value, detail.Label + ": " + detail.Value);
            Grid.SetRow(label, rows.RowDefinitions.Count - 1); Grid.SetRow(value, rows.RowDefinitions.Count - 1); Grid.SetColumn(value, 1);
            rows.Children.Add(label); rows.Children.Add(value);
        }
        panel.Children.Add(rows);
        return panel;
    }

    private void CopyDetailFileInfo(object sender, RoutedEventArgs e)
    {
        if (fileInfoGroups.Count == 0) return;
        try { Clipboard.SetText(DetailFile.Text + Environment.NewLine + Environment.NewLine + VideoFileDetails.Text(fileInfoGroups)); SetStatus("File details copied."); }
        catch (System.Runtime.InteropServices.ExternalException) { SetStatus("Clipboard is busy."); }
    }
}
