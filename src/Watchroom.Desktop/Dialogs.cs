using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Net.Http;
using Watchroom.Core;

namespace Watchroom.Desktop;

public static class Dialogs
{
    public static MessageBoxResult ConfirmUnsavedSettings(Window owner)
    {
        var result = MessageBoxResult.Cancel;
        var win = new Window { Owner = owner, Title = "Unsaved settings", Width = 530, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Save your settings before switching tabs?", FontSize = 20,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "You have unsaved changes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Keep editing", IsCancel = true };
        var discard = new Button { Content = "Discard changes" };
        var save = new Button { Content = "Save changes", IsDefault = true };
        save.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        cancel.Click += (_, _) => win.DialogResult = false;
        discard.Click += (_, _) => { result = MessageBoxResult.No; win.DialogResult = true; };
        save.Click += (_, _) => { result = MessageBoxResult.Yes; win.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(discard); actions.Children.Add(save); panel.Children.Add(actions);
        win.Content = panel;
        win.ShowDialog();
        return result;
    }

    public static bool OfferNextEpisode(Window owner, MediaItem next)
    {
        var win = new Window { Owner = owner, Title = "Watch next episode?", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = next.Series, FontSize = 20, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = $"Season {next.Season} · {next.EpisodeDisplayTitle}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var later = new Button { Content = "Not now", IsCancel = true };
        var play = new Button { Content = "Play next episode", IsDefault = true };
        play.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        play.Click += (_, _) => win.DialogResult = true;
        actions.Children.Add(later); actions.Children.Add(play); panel.Children.Add(actions);
        win.Content = panel;
        return win.ShowDialog() == true;
    }

    public sealed record WatchRoomChoice(SavedRoom? Room, string? NewRoomName);

    public static WatchRoomChoice? ChooseWatchRoom(Window owner, IEnumerable<SavedRoom> rooms, SavedRoom? current,
        Func<string, string?> validateName)
    {
        var win = new Window { Owner = owner, Title = "Watch together", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Room", Margin = new Thickness(0, 0, 0, 4) });
        var choices = rooms.Select(room => new WatchRoomOption(room.Name + (room.Id == current?.Id ? " · Current room" : ""), room)).ToList();
        choices.Add(new("Create new room", null));
        var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(WatchRoomOption.Label), MinHeight = 40,
            SelectedIndex = current is null ? 0 : Math.Max(0, choices.FindIndex(choice => choice.Room?.Id == current.Id)), Margin = new Thickness(0, 0, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(picker, "Room");
        panel.Children.Add(picker);
        var namePanel = new StackPanel();
        namePanel.Children.Add(new TextBlock { Text = "New room name" });
        var name = new TextBox { MaxLength = 100 };
        System.Windows.Automation.AutomationProperties.SetName(name, "New room name");
        namePanel.Children.Add(name); panel.Children.Add(namePanel);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var play = new Button { IsDefault = true };
        play.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        void Refresh()
        {
            var creating = (picker.SelectedItem as WatchRoomOption)?.Room is null;
            namePanel.Visibility = creating ? Visibility.Visible : Visibility.Collapsed;
            error.Text = creating && !string.IsNullOrWhiteSpace(name.Text) ? validateName(name.Text.Trim()) ?? "" : "";
            play.Content = creating ? "Create and play" : "Play in this room";
            play.IsEnabled = !creating || !string.IsNullOrWhiteSpace(name.Text) && error.Text.Length == 0;
        }
        picker.SelectionChanged += (_, _) => Refresh();
        name.TextChanged += (_, _) => Refresh();
        play.Click += (_, _) => win.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(play); panel.Children.Add(actions);
        win.Content = panel; Refresh();
        win.Loaded += (_, _) => { if (choices.Count == 1) name.Focus(); else picker.Focus(); };
        if (win.ShowDialog() != true) return null;
        var chosen = ((WatchRoomOption)picker.SelectedItem).Room;
        return new(chosen, chosen is null ? name.Text.Trim() : null);
    }

    private sealed record WatchRoomOption(string Label, SavedRoom? Room);

    public static string? Prompt(Window owner, string title, string label, string initial = "", Func<string, string?>? validate = null, string action = "Continue", string? help = null)
    {
        var win = new Window { Owner = owner, Title = title, Width = 530, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initial, MinWidth = 400 }; panel.Children.Add(input);
        if (help is not null)
        {
            var hint = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(hint);
        }
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12), FontWeight = FontWeights.SemiBold };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(error);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var button = new Button { Content = action, IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(initial) };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        void Validate()
        {
            error.Text = string.IsNullOrWhiteSpace(input.Text) ? "" : validate?.Invoke(input.Text.Trim()) ?? "";
            button.IsEnabled = !string.IsNullOrWhiteSpace(input.Text) && error.Text.Length == 0;
        }
        input.TextChanged += (_, _) => Validate(); Validate();
        button.Click += (_, _) => win.DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(button); panel.Children.Add(actions); win.Content = panel;
        win.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return win.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static MetadataMatch? Choose(Window owner, List<MetadataMatch> matches, Func<MetadataMatch, Task<string?>>? loadPoster = null, string? message = null)
    {
        var win = MetadataPicker(owner, matches, loadPoster, message);
        return win.ShowDialog() == true ? ((ListBox)win.FindName("MetadataChoices")).SelectedItem as MetadataMatch : null;
    }
    internal static Window MetadataPicker(Window owner, List<MetadataMatch> matches, Func<MetadataMatch, Task<string?>>? loadPoster, string? message)
    {
        var win = new Window { Owner = owner, Title = "Choose the correct title", Width = 760, Height = 520, MinWidth = 600, MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        NameScope.SetNameScope(win, new NameScope());
        var panel = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var button = new Button { Content = "Use this match", IsDefault = true, IsEnabled = false };
        button.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        actions.Children.Add(cancel); actions.Children.Add(button); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var explanation = new TextBlock { Text = "Choose by year and animation type. Similar names can refer to different adaptations." +
            (string.IsNullOrWhiteSpace(message) ? "" : "\n" + message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(explanation, Dock.Top); panel.Children.Add(explanation);
        var preview = new StackPanel { Width = 150, Margin = new Thickness(16, 0, 0, 0) };
        var image = new Image { Width = 130, Height = 195, Stretch = System.Windows.Media.Stretch.Uniform };
        var posterStatus = new TextBlock { Text = "Select a title to preview its poster.", TextWrapping = TextWrapping.Wrap };
        preview.Children.Add(image); preview.Children.Add(posterStatus); DockPanel.SetDock(preview, Dock.Right); panel.Children.Add(preview);
        var template = new DataTemplate(typeof(MetadataMatch));
        var row = new FrameworkElementFactory(typeof(StackPanel)); row.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 8, 4, 8));
        foreach (var (property, size) in new[] { (nameof(MetadataMatch.Title), 16.0), (nameof(MetadataMatch.Description), 12.0), (nameof(MetadataMatch.Evidence), 12.0) })
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property));
            text.SetValue(TextBlock.FontSizeProperty, size); text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); row.AppendChild(text);
        }
        template.VisualTree = row;
        var list = new ListBox { ItemsSource = matches, ItemTemplate = template, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(list, "Matching titles with year, animation type and provider");
        win.RegisterName("MetadataChoices", list); win.RegisterName("MetadataPreview", image); win.RegisterName("UseMetadataMatch", button);
        panel.Children.Add(list);
        var closed = false; win.Closed += (_, _) => closed = true;
        list.SelectionChanged += async (_, _) =>
        {
            button.IsEnabled = list.SelectedItem is MetadataMatch;
            image.Source = null;
            if (list.SelectedItem is not MetadataMatch chosen) return;
            posterStatus.Text = loadPoster is null || chosen.Poster is null ? "No poster available." : "Loading poster…";
            if (loadPoster is null || chosen.Poster is null) return;
            try
            {
                var path = await loadPoster(chosen);
                if (closed || !ReferenceEquals(list.SelectedItem, chosen)) return;
                if (path is null) { posterStatus.Text = "No poster available."; return; }
                using var file = File.OpenRead(path);
                var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 150; bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap; posterStatus.Text = chosen.Description;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or InvalidOperationException or OperationCanceledException)
            { if (!closed && ReferenceEquals(list.SelectedItem, chosen)) posterStatus.Text = "Poster unavailable. You can still choose this title."; }
        };
        button.Click += (_, _) => { if (list.SelectedItem is not null) win.DialogResult = true; };
        win.Content = panel; return win;
    }
}
