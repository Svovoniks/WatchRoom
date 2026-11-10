using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BlackPearl.Desktop;

// Search's filter menus are ComboBoxes drawn as pills (the FilterPill style): an icon and the filter's name until a
// choice is made, then the choice in an accent pill with a button that clears it back to the first item, "Any".
public static class FilterPill
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached("Label", typeof(string), typeof(FilterPill), new FrameworkPropertyMetadata(""));
    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);

    /// <summary>A Segoe Fluent Icons glyph shown before the label.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(string), typeof(FilterPill), new FrameworkPropertyMetadata(""));
    public static string GetIcon(DependencyObject element) => (string)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, string value) => element.SetValue(IconProperty, value);

    /// <summary>Clears the menu given as the parameter back to its first item.</summary>
    public static ICommand Clear { get; } = new ClearCommand();

    private sealed class ClearCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => parameter is ComboBox;
        public void Execute(object? parameter)
        {
            if (parameter is not ComboBox box || box.Items.Count == 0) return;
            box.SelectedIndex = 0;
            box.Focus();
        }
    }
}
