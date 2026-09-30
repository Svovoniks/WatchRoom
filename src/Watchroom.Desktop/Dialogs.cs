using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public static class Dialogs
{
    public static string? Prompt(Window owner, string title, string label, string initial = "")
    {
        var win = new Window { Owner = owner, Title = title, Width = 530, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initial, MinWidth = 400 }; panel.Children.Add(input);
        var button = new Button { Content = "Continue", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => win.DialogResult = true; panel.Children.Add(button); win.Content = panel;
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
