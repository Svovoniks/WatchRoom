using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Watchroom.Core;
using Watchroom.Desktop;

static class UiFindingsChecks
{
    public static void Run()
    {
        var window = new MainWindow();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object Field(string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        var store = (LibraryStore)Field("library");
        var subtitleRules = (IList)Field("subtitleRuleDrafts");
        void AddRule(string audio, string subtitles)
        {
            Call("AddSubtitleRule", window, new RoutedEventArgs());
            var draft = subtitleRules[^1]!;
            draft.GetType().GetProperty("AudioLanguage")!.SetValue(draft, audio);
            draft.GetType().GetProperty("SubtitleLanguages")!.SetValue(draft, subtitles);
        }
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
        var root = (FrameworkElement)window.Content;
        void Layout(int width = 900, int height = 600)
        { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
        void Capture(string name)
        {
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/ui-findings-fixed/layout");
            using var output = File.Create("artifacts/ui-findings-fixed/layout/" + name + ".png"); encoder.Save(output);
        }
        try
        {
            var queues = (List<SavedQueue>)Field("savedQueues");
            queues.Clear(); queues.Add(new("empty", "Empty queue", []));
            Call("RefreshQueuePickers", "empty"); Call("ShowPage", "Queues"); Layout();
            Check(!Control<Button>("PlaySavedQueueButton").IsEnabled && !Control<Button>("SavedQueueEarlier").IsEnabled && !Control<Button>("SavedQueueLater").IsEnabled,
                "empty queue disables playback and both reorder actions");
            Check(Control<Button>("AddQueueVideosButton").Style == window.FindResource("Primary"), "empty queue promotes adding videos");
            Check(Control<ListBox>("SavedQueueItems").ActualHeight >= 120, "smallest queue layout leaves room for entries without clipped empty-state text");
            var picker = Control<ListBox>("QueuePicker");
            var queuePeer = UIElementAutomationPeer.CreatePeerForElement(picker)!;
            Check(queuePeer.GetChildren().Any(p => p.GetName() == "Empty queue, 0 videos"), "queue accessible name is human-readable without record internals");
            Capture("queues-empty-900");
            var file = Path.Combine(App.DataDirectory, "ui-queue-fixture.mp4"); File.WriteAllText(file, "UI-only fixture");
            var media = new MediaItem("ui-available", file, "Available video", "Movie");
            ((List<MediaItem>)Field("items")).Add(media);
            queues.Add(new("filled", "Mixed availability", [media.Id, "absent", media.Id]));
            Call("RefreshQueuePickers", "filled");
            Check(Control<Button>("PlaySavedQueueButton").IsEnabled, "one available file enables playback despite missing queue entries");
            var entries = Control<ListBox>("SavedQueueItems"); entries.SelectedIndex = 0;
            Check(!Control<Button>("SavedQueueEarlier").IsEnabled && Control<Button>("SavedQueueLater").IsEnabled, "first entry can only move later");
            entries.SelectedIndex = 2;
            Check(Control<Button>("SavedQueueEarlier").IsEnabled && !Control<Button>("SavedQueueLater").IsEnabled, "last entry can only move earlier");
            entries.SelectedIndex = -1;
            Check(!Control<Button>("SavedQueueEarlier").IsEnabled && !Control<Button>("SavedQueueLater").IsEnabled, "clearing selection disables reordering");
            File.Delete(file); Call("UpdateSavedQueueActions");
            Check(!Control<Button>("PlaySavedQueueButton").IsEnabled && Control<TextBlock>("SavedQueueFeedback").Text.Contains("No available videos"), "disappeared file disables playback and provides inline recovery");
            Call("AddQueueVideos", window, new RoutedEventArgs());
            Check(Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Visible, "adding videos with no available files opens useful folder setup");
            Call("ShowPage", "Queues");

            Call("SetStatus", "This queue has no available videos."); Call("ShowPage", "Settings");
            Check(!Control<TextBlock>("StatusText").Text.Contains("queue"), "page-scoped queue error clears on navigation");
            typeof(MainWindow).GetField("scanning", flags)!.SetValue(window, true);
            Control<TextBlock>("ScanStatus").Text = "Scanning test fixtures";
            Call("ShowPage", "Folders");
            Check(Control<TextBlock>("StatusText").Text == "Scanning test fixtures", "ongoing scan feedback survives navigation");
            typeof(MainWindow).GetField("scanning", flags)!.SetValue(window, false);

            var rooms = (ObservableCollection<SavedRoom>)Field("savedRooms"); rooms.Clear(); Call("ShowPage", "Rooms");
            Check(Control<FrameworkElement>("RoomsEmptyState").Visibility == Visibility.Visible && Control<FrameworkElement>("SavedRoomsContent").Visibility == Visibility.Collapsed,
                "zero saved rooms show helpful empty state without disabled management panel");
            Check(Control<Button>("CreateSavedRoomButton").Visibility == Visibility.Collapsed, "empty room does not duplicate header actions");
            Layout(); Capture("rooms-empty-900");
            var host = new SavedRoom("host", "Host room", "https://room.test", new string('A', 24), new string('1', 64));
            var guest = new SavedRoom("guest", "Guest room", host.Server, new string('B', 24));
            rooms.Add(host); rooms.Add(guest); Control<ListBox>("SavedRoomsList").SelectedItem = host;
            Check(Control<FrameworkElement>("SavedRoomHostControls").Visibility == Visibility.Visible, "selected host room exposes its own permissions");
            Control<ListBox>("SavedRoomsList").SelectedItem = guest;
            Check(Control<FrameworkElement>("SavedRoomHostControls").Visibility == Visibility.Collapsed && Control<FrameworkElement>("SavedRoomGuestManagement").Visibility == Visibility.Collapsed,
                "guest room hides host-only controls");

            var folders = (ObservableCollection<LibraryFolder>)Field("folders"); folders.Clear();
            folders.Add(new(App.DataDirectory, "Anime")); Control<ListBox>("FolderList").SelectedIndex = 0;
            Check(Control<FrameworkElement>("FolderEditor").Visibility == Visibility.Visible && (string)((ComboBoxItem)Control<ComboBox>("FolderKind").SelectedItem).Content == "Anime",
                "selecting a folder reveals editing with its existing category");
            Call("ShowPage", "Folders"); Layout(); Capture("folder-selected-900");
            Control<ListBox>("FolderList").SelectedIndex = -1;
            Check(Control<FrameworkElement>("FolderEditor").Visibility == Visibility.Collapsed, "no folder selection hides category editing");

            Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Profile");
            var originalName = Control<TextBox>("DisplayNameBox").Text;
            Control<TextBox>("DisplayNameBox").Text = "Draft name";
            Check(Control<Button>("SavePreferencesButton").IsEnabled && Control<Button>("CancelPreferencesButton").IsEnabled, "profile edits reveal persistent Save and Cancel actions");
            Check((string)typeof(MainWindow).GetProperty("SavedDisplayName", flags)!.GetValue(window)! == originalName, "room connections ignore unsaved profile drafts");
            Call("ShowPage", "Settings");
            Check(Control<TextBox>("DisplayNameBox").Text == "Draft name", "selecting the current settings page preserves its draft");
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(Control<TextBox>("DisplayNameBox").Text == originalName && !Control<Button>("SavePreferencesButton").IsEnabled, "Cancel restores persisted values and clears dirty state");
            Check(window.FindName("SaveMetadataKeyButton") is null, "TMDB uses the common Save changes action without a separate key button");
            Control<PasswordBox>("MetadataToken").Password = "draft-fixture-key";
            Check(Control<Button>("SavePreferencesButton").IsEnabled && MetadataCredential.Load(App.DataDirectory) == "", "key edits enable Save without writing the credential vault");
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(Control<PasswordBox>("MetadataToken").Password == "" && !Control<Button>("SavePreferencesButton").IsEnabled, "Cancel discards an unsaved key and clears dirty state");
            AddRule("Japanese", "Russian, English, off");
            Check(Control<Button>("SavePreferencesButton").IsEnabled && store.Setting("subtitleLanguageRules") is null, "audio-specific subtitle drafts enable Save without writing settings");
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(subtitleRules.Count == 0 && !Control<Button>("SavePreferencesButton").IsEnabled, "Cancel discards added subtitle rules");
            Control<TextBox>("PreferredAudioLanguages").Text = "Russian, English";
            Control<TextBox>("PreferredSubtitleLanguages").Text = "English, off";
            Check(Control<Button>("SavePreferencesButton").IsEnabled && ((string[])Field("audioLanguages")).Length == 0,
                "track language drafts enable Save without changing active preferences");
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(Control<TextBox>("PreferredAudioLanguages").Text == "" && Control<TextBox>("PreferredSubtitleLanguages").Text == "",
                "Cancel restores both track language preferences");
            Control<TextBox>("DisplayNameBox").Text = " ";
            Check((bool)Call("TrySavePreferences")! == false && Control<TextBlock>("DisplayNameError").Text.Length > 0, "empty name gets field-specific validation");
            Control<TextBox>("DisplayNameBox").Text = "Saved name"; Control<TextBox>("ServerBox").Text = "invalid";
            Check((bool)Call("TrySavePreferences")! == false && Control<TextBlock>("ServerError").Text.Length > 0, "invalid server gets field-specific validation");
            Control<TextBox>("ServerBox").Text = "https://room.test";
            Control<TextBox>("MetadataRefreshDays").Text = "999";
            Control<PasswordBox>("MetadataToken").Password = "invalid-settings-fixture-key";
            Check((bool)Call("TrySavePreferences")! == false && Control<TextBlock>("RefreshDaysError").Text.Length > 0 && Control<FrameworkElement>("MetadataPreferences").Visibility == Visibility.Visible,
                "invalid refresh frequency selects its section and identifies the offending field");
            Check(store.Setting("name") != "Saved name", "invalid preferences do not partially save profile fields");
            Check(MetadataCredential.Load(App.DataDirectory) == "", "failed settings validation does not save a drafted key");
            Control<TextBox>("MetadataRefreshDays").Text = "365";
            Call("ChangeRefreshDays", Control<Button>("RefreshMore"), new RoutedEventArgs());
            Check(Control<TextBox>("MetadataRefreshDays").Text == "365", "refresh stepper clamps to the upper limit");
            Control<TextBox>("PreferredAudioLanguages").Text = "Not a language";
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("TrackPreferencesError").Text.Length > 0 &&
                Control<FrameworkElement>("PlaybackPreferences").Visibility == Visibility.Visible && store.Setting("name") != "Saved name",
                "invalid track preferences identify Playback and do not partially save settings");
            Control<TextBox>("PreferredAudioLanguages").Text = "Russian, English";
            Control<TextBox>("PreferredSubtitleLanguages").Text = "English, off";
            AddRule("Japanese", "Russian, English, off"); AddRule("ja-JP", "off");
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("TrackPreferencesError").Text.Contains("already") && store.Setting("subtitleLanguageRules") is null,
                "duplicate audio rules reject the whole draft with an actionable error");
            Call("RemoveSubtitleRule", new Button { DataContext = subtitleRules[1] }, new RoutedEventArgs());
            AddRule("English", "off");
            var providers = (IList)Field("providerChoices");
            foreach (var provider in providers) provider!.GetType().GetProperty("Enabled")!.SetValue(provider, false);
            Control<CheckBox>("NeverRefresh").IsChecked = true;
            Control<ComboBox>("MetadataLanguage").SelectedValue = "fr-FR"; Control<ComboBox>("MetadataCountry").SelectedValue = "FR";
            Control<PasswordBox>("MetadataToken").Password = new string('x', 1281);
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("MetadataKeyStatus").Text.Contains("too long") && store.Setting("audioLanguages") is null && store.Setting("name") != "Saved name",
                "credential storage failure identifies Metadata and leaves other settings unsaved");
            Control<PasswordBox>("MetadataToken").Password = " fixture-key-one ";
            Check((bool)Call("TrySavePreferences")!, "valid structured preferences save successfully");
            Check(store.Setting("audioLanguages") == "ru, en" && store.Setting("subtitleLanguages") == "en, off",
                "Save persists normalized audio and subtitle preferences together");
            var savedRules = Wire.Read<SubtitleLanguageRule[]>(store.Setting("subtitleLanguageRules")!);
            Check(savedRules.Length == 2 && savedRules[0].AudioLanguage == "ja" && savedRules[0].Languages.SequenceEqual(["ru", "en", "off"]) && savedRules[1].Languages.SequenceEqual(["off"]),
                "Save changes persists audio-specific subtitle priority lists in order");
            Check(MetadataCredential.Load(App.DataDirectory) == "fixture-key-one" && (string)Field("savedMetadataToken") == "fixture-key-one",
                "Save changes stores the trimmed key in the credential vault and activates it");
            Check(!((string)Call("PreferenceSnapshot")!).Contains("fixture-key-one"), "preference snapshots exclude the secret key");
            var restoredWindow = new MainWindow();
            try
            {
                Check(((PasswordBox)restoredWindow.FindName("MetadataToken")).Password == "fixture-key-one" &&
                    !((Button)restoredWindow.FindName("SavePreferencesButton")).IsEnabled, "a reopened settings window restores the saved key without an unsaved draft");
            }
            finally { restoredWindow.Close(); }
            var options = MetadataOptions.Load(store);
            Check(options.ProviderOrder.Length == 0 && options.RefreshDays == 0 && options.Language == "fr-FR" && options.Country == "FR", "local-only providers, Never and locale selections round-trip");
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(Control<CheckBox>("NeverRefresh").IsChecked == true && !Control<TextBox>("MetadataRefreshDays").IsEnabled, "Never restores with its numeric field disabled");
            Check(Control<TextBox>("PreferredAudioLanguages").Text == "ru, en" && !Control<Button>("SavePreferencesButton").IsEnabled,
                "saved track preferences reload without leaving a dirty draft");
            Check(subtitleRules.Count == 2 && (string)subtitleRules[0]!.GetType().GetProperty("AudioLanguage")!.GetValue(subtitleRules[0])! == "ja",
                "saved subtitle rules reload with normalized audio languages");
            Control<PasswordBox>("MetadataToken").Password = "replacement-fixture-key";
            Call("CancelPreferences", window, new RoutedEventArgs());
            Check(Control<PasswordBox>("MetadataToken").Password == "fixture-key-one" && MetadataCredential.Load(App.DataDirectory) == "fixture-key-one", "Cancel restores the saved key after an unsaved replacement");
            Control<PasswordBox>("MetadataToken").Clear();
            Check(MetadataCredential.Load(App.DataDirectory) == "fixture-key-one", "clearing a key remains a draft until Save changes");
            Check((bool)Call("TrySavePreferences")! && MetadataCredential.Load(App.DataDirectory) == "" && (string)Field("savedMetadataToken") == "", "Save changes removes a cleared key from the vault and active metadata client");
            var first = providers[0]!; first.GetType().GetProperty("Enabled")!.SetValue(first, true);
            var move = new Button { Tag = "Down", DataContext = first }; Call("ProviderMoved", move, new RoutedEventArgs());
            Check(ReferenceEquals(providers[1], first), "provider move changes the fallback ordering");
            Check((bool)Call("TrySavePreferences")! && MetadataOptions.Load(store).ProviderOrder.Length == 1, "provider toggles persist independently of ordering");

            foreach (var size in new[] { (900, 600), (1280, 810) })
            foreach (var section in new[] { "Profile", "Metadata", "Playback", "Appearance", "Updates" })
            {
                Call("ShowPreferenceSection", section); Layout(size.Item1, size.Item2);
                var save = Control<Button>("SavePreferencesButton");
                var bounds = save.TransformToAncestor(root).TransformBounds(new Rect(new Point(), save.RenderSize));
                Check(bounds.Bottom <= root.ActualHeight && bounds.Left >= 0 && bounds.Right <= root.ActualWidth && save.ActualHeight >= 40,
                    $"Save stays visible in {section} at {size}");
                if (section == "Playback") Capture("playback-" + size.Item1);
                if (section == "Metadata")
                {
                    Control<ScrollViewer>("MetadataPreferences").ScrollToEnd(); Layout(size.Item1, size.Item2);
                    var after = save.TransformToAncestor(root).TransformBounds(new Rect(new Point(), save.RenderSize));
                    Check(after == bounds, "scrolling settings never moves the Save bar at " + size);
                    Control<ScrollViewer>("MetadataPreferences").ScrollToHome(); Layout(size.Item1, size.Item2);
                    Capture("metadata-" + size.Item1);
                }
            }
            Call("ToggleTheme", window, new RoutedEventArgs()); Call("ShowPreferenceSection", "Playback");
            foreach (var size in new[] { (900, 600), (1280, 810) }) { Layout(size.Item1, size.Item2); Capture("playback-light-" + size.Item1); }
            Call("ShowPage", "Welcome"); Layout(); Capture("welcome-900");
            Check(Control<FrameworkElement>("WelcomePage").Visibility == Visibility.Visible && Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Collapsed,
                "welcome provides a separate first-run choice before folder management");
            Console.WriteLine($"{checks} UI findings checks passed.");
        }
        finally { MetadataCredential.Save(App.DataDirectory, ""); window.Close(); }
    }
}
