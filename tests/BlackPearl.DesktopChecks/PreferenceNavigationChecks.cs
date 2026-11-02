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
        var originalName = name.Text;
        name.Text = "Navigation draft";
        var history = HistoryCount();
        Answer("Keep editing", () => Call("NavigateLibrary", 3, null!, null!, null!));
        check((string)Field("currentPage") == "Settings" && name.Text == "Navigation draft" && HistoryCount() == history,
            "cancelling library navigation keeps settings and history intact");
        Answer("Close", () => Call("ShowPage", "Queues"));
        check((string)Field("currentPage") == "Settings" && name.Text == "Navigation draft",
            "closing the prompt keeps the unsaved draft on settings");
        Answer("Keep editing", () => ClickSection("Playback"));
        check(Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible,
            "cancelling a settings section switch retains the current section");
        Answer("Keep editing", () => Call("BrowseHistory", true));
        check(HistoryCount() == history && (string)Field("currentPage") == "Settings",
            "cancelled Back navigation does not consume history");
        Answer("Discard changes", () => Call("ShowPage", "Queues"));
        check((string)Field("currentPage") == "Queues" && name.Text == originalName && !Control<Button>("SavePreferencesButton").IsEnabled,
            "discarding changes restores saved settings and completes navigation");

        Call("ShowPage", "Settings");
        name.Text = "Saved on navigation";
        Answer("Save changes", () => Call("ShowPage", "Rooms"));
        check(store.Setting("name") == "Saved on navigation" && (string)Field("currentPage") == "Rooms" && !Control<Button>("SavePreferencesButton").IsEnabled,
            "saving from the prompt persists settings before navigating");
        Call("ShowPage", "Settings"); name.Text = " ";
        Answer("Save changes", () => ClickSection("Playback"));
        check((string)Field("currentPage") == "Settings" && Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible &&
            Control<TextBlock>("DisplayNameError").Text.Length > 0 && store.Setting("name") == "Saved on navigation",
            "failed validation stays in settings and identifies the invalid field");
        Call("LoadPreferences");
        Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "English";
        Answer("Save changes", () => ClickSection("Metadata"));
        check(store.Setting("audioLanguages") == "en" && Control<FrameworkElement>("MetadataPreferences").Visibility == Visibility.Visible,
            "saving on a settings section switch applies drafts before changing section");
        Control<PasswordBox>("MetadataToken").Password = "discarded-navigation-key";
        Answer("Discard changes", () => ClickSection("Playback"));
        check(Control<PasswordBox>("MetadataToken").Password == "" && MetadataCredential.Load(App.DataDirectory) == "" &&
            Control<FrameworkElement>("PlaybackPreferences").Visibility == Visibility.Visible,
            "credential-only drafts prompt and discard without writing the vault");
        name.Text = originalName; Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "";
        check((bool)Call("TrySavePreferences")!, "navigation fixtures restore the original settings");

        // Folders lives in Settings: links to it open that section, which applies changes at once and has no Save bar.
        Call("ShowPage", "Rooms"); Call("ShowPage", "Folders");
        check(window.FindName("FoldersNav") is null && (string)Field("currentPage") == "Settings" &&
            Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Visible && Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Collapsed &&
            Control<FrameworkElement>("PreferenceFooter").Visibility == Visibility.Collapsed,
            "Folders opens as a Settings section without the Save bar and has no sidebar entry");
        ClickSection("Plugins");
        // The logical tree holds the section's content before its template is applied.
        bool InSection(DependencyObject? element, string section)
        {
            for (; element is not null; element = LogicalTreeHelper.GetParent(element))
                if (element is FrameworkElement { Name: var found } && found == section) return true;
            return false;
        }
        check(Control<FrameworkElement>("PluginPreferences").Visibility == Visibility.Visible && Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Collapsed &&
            InSection(Control<Button>("ManagePluginsButton"), "PluginPreferences") && InSection(Control<Button>("AddFolderButton"), "FoldersPage") &&
            Control<FrameworkElement>("PreferenceFooter").Visibility == Visibility.Collapsed,
            "plugins have their own Settings section, apart from Folders and without the Save bar");
        ClickSection("Profile"); name.Text = "Folders draft";
        Answer("Keep editing", () => Call("ShowPage", "Folders"));
        check(Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible && Control<FrameworkElement>("PreferenceFooter").Visibility == Visibility.Visible &&
            name.Text == "Folders draft", "opening Folders from Settings asks about unsaved changes first");
        Call("LoadPreferences");
        Call("ShowPage", "Queues");
        check((string)Field("currentPage") == "Queues", "saved settings allow navigation without a prompt");
    }
}
