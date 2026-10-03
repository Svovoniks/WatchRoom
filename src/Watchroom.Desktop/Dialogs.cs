using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public static class Dialogs
{
    public sealed record WatchRoomChoice(SavedRoom? Room, string? NewRoomName);

    public static WatchRoomChoice? ChooseWatchRoom(Window owner, IEnumerable<SavedRoom> rooms, SavedRoom? current,
        Func<string, string?> validateName)
    {
        var win = new Window { Owner = owner, Title = "Watch together", Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Choose a room", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Play this video in a room you host, or create a new one.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 16) });
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

    public static string? Prompt(Window owner, string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        var win = new Window { Owner = owner, Title = title, Width = 530, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initial, MinWidth = 400 }; panel.Children.Add(input);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12), FontWeight = FontWeights.SemiBold };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(error, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(error);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var button = new Button { Content = "Continue", IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(initial) };
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
    public static MetadataMatch? Choose(Window owner, List<MetadataMatch> matches)
    {
        var win = new Window { Owner = owner, Title = "Choose the correct title", Width = 620, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) }; var button = new Button { Content = "Use this match", IsDefault = true, Margin = new Thickness(0,15,0,0) };
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var list = new ListBox { ItemsSource = matches }; panel.Children.Add(list);
        button.Click += (_, _) => { if (list.SelectedItem is not null) win.DialogResult = true; };
        win.Content = panel; return win.ShowDialog() == true ? list.SelectedItem as MetadataMatch : null;
    }
}
