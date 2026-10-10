using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class LanguagePriorityPicker : UserControl
{
    public sealed record LanguageOption(string Code, string Name);
    private sealed record Priority(string Code, string Name, int Rank, bool CanMoveUp, bool CanMoveDown);
    public static IReadOnlyList<LanguageOption> AudioOptions { get; } = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(culture => culture.Name.Length > 0)
        .GroupBy(culture => TrackLanguages.Normalize(culture.Name))
        .Where(group => group.Key is not null)
        .Select(group => new LanguageOption(group.Key!, (group.FirstOrDefault(culture => culture.Name == group.Key) ?? group.First()).EnglishName))
        .OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static readonly LanguageOption off = new("off", "Off (disable subtitles)");

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LanguagePriorityPicker),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, PreferenceChanged));
    public static readonly DependencyProperty AllowOffProperty = DependencyProperty.Register(nameof(AllowOff), typeof(bool), typeof(LanguagePriorityPicker),
        new PropertyMetadata(false, PreferenceChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool AllowOff { get => (bool)GetValue(AllowOffProperty); set => SetValue(AllowOffProperty, value); }
    public event RoutedEventHandler? TextChanged;

    public LanguagePriorityPicker() { InitializeComponent(); RefreshChoices(); }
    public void Clear() => SetCurrentValue(TextProperty, "");
    private static void PreferenceChanged(DependencyObject source, DependencyPropertyChangedEventArgs e)
    {
        var picker = (LanguagePriorityPicker)source;
        if (picker.Priorities is null) return;
        picker.RefreshChoices();
        if (e.Property == TextProperty) picker.TextChanged?.Invoke(picker, new RoutedEventArgs());
    }
    private string[] Codes()
    {
        try { return TrackLanguages.Parse(Text ?? "", AllowOff); }
        catch (ArgumentException) { return []; } // Saved preference validation remains in the settings save path.
    }
    private void RefreshChoices()
    {
        var codes = Codes();
        var options = AllowOff ? AudioOptions.Prepend(off).ToArray() : AudioOptions.ToArray();
        Priorities.ItemsSource = codes.Select((code, index) => new Priority(code, options.FirstOrDefault(option => option.Code == code)?.Name ?? code,
            index + 1, index > 0, index < codes.Length - 1)).ToArray();
        LanguageOptions.ItemsSource = options.Where(option => !codes.Contains(option.Code)).ToArray();
        LanguageOptions.SelectedIndex = -1;
    }
    private void AddLanguage(object sender, RoutedEventArgs e)
    {
        if (LanguageOptions.SelectedItem is not LanguageOption choice) return;
        SetCurrentValue(TextProperty, string.Join(", ", Codes().Append(choice.Code).Distinct()));
        LanguageOptions.Focus();
    }
    private void RemoveLanguage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Priority choice) return;
        SetCurrentValue(TextProperty, string.Join(", ", Codes().Where(code => code != choice.Code)));
    }
    private void MoveLanguage(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Priority choice) return;
        var codes = Codes().ToList(); var index = codes.IndexOf(choice.Code);
        var destination = index + ((string)((Button)sender).Tag == "Up" ? -1 : 1);
        if (index < 0 || destination < 0 || destination >= codes.Count) return;
        codes.RemoveAt(index); codes.Insert(destination, choice.Code);
        SetCurrentValue(TextProperty, string.Join(", ", codes));
    }
}
