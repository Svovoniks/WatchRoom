using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlackPearl.Core;
using BlackPearl.Desktop;

static class PreferenceNavigationChecks
{
    public static void Run(MainWindow window, LibraryStore store, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) => (T)window.FindName(name);
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        int HistoryCount() => ((ICollection)Field("backLocations")).Count;
        void Answer(string choice, Action navigate)
        {
            var answered = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) =>
            {
                var dialog = InAppDialog.Open.FirstOrDefault(d => d.Title == "Unsaved settings");
                if (dialog is null) return;
                timer.Stop(); answered = true;
                if (choice == "Close") { dialog.Close(); return; }
                var actions = ((StackPanel)dialog.Content).Children.OfType<WrapPanel>().Single();
                actions.Children.OfType<Button>().Single(b => (string)b.Content == choice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            try { navigate(); }
            finally { timer.Stop(); }
            check(answered, "unsaved settings prompt shown for " + choice);
        }
        void ClickSection(string section) => Call("PreferenceSectionClicked", new Button { Tag = section }, new RoutedEventArgs());

        Call("LoadPreferences"); Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Profile");
        var name = Control<TextBox>("DisplayNameBox");
        bool Pending() => (bool)typeof(MainWindow).GetProperty("HasUnsavedPreferences", flags)!.GetValue(window)!;
        bool Prompted(Action navigate)
        {
            var shown = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (InAppDialog.Open.FirstOrDefault(d => d.Title == "Unsaved settings") is { } dialog) { shown = true; dialog.Close(); } };
            timer.Start();
            try { navigate(); } finally { timer.Stop(); }
            return shown;
        }
        var originalName = name.Text;
        name.Text = "Navigation draft";
        var history = HistoryCount();
        check(!Prompted(() => Call("ShowPage", "Queues")) && (string)Field("currentPage") == "Queues" && store.Setting("name") == "Navigation draft" && !Pending(),
            "leaving Settings applies a change still waiting for a pause, without asking");
        Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Profile");
        Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "English";
        check(!Prompted(() => ClickSection("Metadata")) && store.Setting("audioLanguages") == "en" && Control<FrameworkElement>("MetadataPreferences").Visibility == Visibility.Visible,
            "switching settings sections applies pending changes first");

        ClickSection("Profile"); name.Text = " ";
        history = HistoryCount();
        Answer("Keep editing", () => Call("NavigateLibrary", 3, null!, null!, null!));
        check((string)Field("currentPage") == "Settings" && name.Text == " " && HistoryCount() == history && Control<TextBlock>("DisplayNameError").Text.Length > 0,
            "an invalid change asks before leaving, and keeping it stays in settings with history and the field's error intact");
        Answer("Close", () => Call("ShowPage", "Queues"));
        check((string)Field("currentPage") == "Settings" && name.Text == " ", "closing the prompt keeps the invalid change on settings");
        Answer("Keep editing", () => ClickSection("Playback"));
        check(Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible, "keeping an invalid change retains the current section");
        Answer("Keep editing", () => Call("BrowseHistory", true));
        check(HistoryCount() == history && (string)Field("currentPage") == "Settings", "cancelled Back navigation does not consume history");
        Call("ShowPreferenceSection", "Metadata");
        Answer("Keep editing", () => ClickSection("Playback"));
        check(Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible && Control<TextBlock>("DisplayNameError").Text.Length > 0,
            "keeping an invalid change from another section opens the field that needs fixing");
        Answer("Discard changes", () => Call("ShowPage", "Queues"));
        check((string)Field("currentPage") == "Queues" && name.Text == "Navigation draft" && store.Setting("name") == "Navigation draft" && !Pending(),
            "discarding an invalid change restores the applied settings and completes navigation");

        Call("ShowPage", "Settings"); ClickSection("Metadata");
        Control<PasswordBox>("MetadataToken").Password = "Bearer";
        Answer("Discard changes", () => ClickSection("Playback"));
        check(Control<PasswordBox>("MetadataToken").Password == "" && MetadataCredential.Load(App.DataDirectory) == "" &&
            Control<FrameworkElement>("PlaybackPreferences").Visibility == Visibility.Visible,
            "an invalid key asks and is discarded without writing the vault");
        name.Text = originalName; Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "";
        check((bool)Call("TrySavePreferences")!, "navigation fixtures restore the original settings");

        // Folders lives in Settings: links to it open that section.
        Call("ShowPage", "Rooms"); Call("ShowPage", "Folders");
        check((string)Field("currentPage") == "Settings" &&
            Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Visible && Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Collapsed,
            "Folders opens as a Settings section");
        ClickSection("Plugins");
        // The logical tree holds the section's content before its template is applied.
        bool InSection(DependencyObject? element, string section)
        {
            for (; element is not null; element = LogicalTreeHelper.GetParent(element))
                if (element is FrameworkElement { Name: var found } && found == section) return true;
            return false;
        }
        check(Control<FrameworkElement>("PluginPreferences").Visibility == Visibility.Visible && Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Collapsed &&
            InSection(Control<RadioButton>("AvailablePluginsView"), "PluginPreferences") && InSection(Control<Button>("AddFolderButton"), "FoldersPage"),
            "plugins have their own Settings section, apart from Folders");
        ClickSection("Profile"); name.Text = "";
        Answer("Keep editing", () => Call("ShowPage", "Folders"));
        check(Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible && name.Text == "",
            "opening Folders from Settings asks about an invalid change first");
        Call("LoadPreferences");
        Call("ShowPage", "Queues");
        check((string)Field("currentPage") == "Queues", "applied settings allow navigation without a prompt");
    }
}
