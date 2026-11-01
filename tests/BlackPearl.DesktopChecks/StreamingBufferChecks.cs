using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class StreamingBufferChecks
{
    public static void Run(MainWindow window, LibraryStore store, string directory, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var input = (TextBox)window.FindName("StreamingBufferSeconds");
        var error = (TextBlock)window.FindName("StreamingBufferError");
        Call("LoadPreferences"); Call("ShowPreferenceSection", "Playback");
        check(input.Text == "30", "streaming buffer defaults to thirty seconds in Playback settings");
        foreach (var invalid in new[] { "", "0", "61", "3.5", "abc" })
        {
            input.Text = invalid;
            check((bool)Call("TrySavePreferences")! == false && error.Text.Length > 0 && store.Setting(StreamingBufferOptions.SettingKey) is null,
                "invalid streaming buffer is rejected without persisting: " + invalid);
        }
        input.Text = "12";
        check((bool)Call("TrySavePreferences")! && StreamingBufferOptions.Load(new LibraryStore(directory)) == 12,
            "saved streaming buffer survives a new library settings reader");
        input.Text = "40"; Call("CancelPreferences", window, new RoutedEventArgs());
        check(input.Text == "12", "canceling a draft buffer change restores the saved setting");
        store.Setting(StreamingBufferOptions.SettingKey, "corrupt"); Call("LoadPreferences");
        check(input.Text == "30", "corrupt saved streaming buffer falls back to the default");
        input.Text = "30"; Call("TrySavePreferences");
    }
}
