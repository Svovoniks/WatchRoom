using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlackPearl.Core;
using BlackPearl.Desktop;

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
        IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T matching) yield return matching;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }
        void AddLanguage(LanguagePriorityPicker priority, string code)
        {
            // Choosing a language adds it immediately; there is no separate Add button.
            var options = (ComboBox)priority.FindName("LanguageOptions");
            options.SelectedValue = code;
            if (!priority.Text.Split(", ").Contains(code)) throw new Exception("FAIL: choosing " + code + " did not add it");
        }
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
            Call("ShowPage", "Queues"); Call("ShowAllQueues"); Layout();
            var galleryNames = new List<string>();
            void CollectNames(DependencyObject node)
            {
                if (node is Button button) galleryNames.Add(System.Windows.Automation.AutomationProperties.GetName(button));
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) CollectNames(VisualTreeHelper.GetChild(node, i));
            }
            CollectNames(Control<ItemsControl>("QueueGallery"));
            Check(galleryNames.SequenceEqual(["Empty queue, 0 videos", "New queue"]), $"queue gallery names each queue in readable words ({string.Join(" | ", galleryNames)})");
            Capture("queues-gallery-900");
            Call("OpenQueue", "empty"); Layout();
            Check(Control<FrameworkElement>("QueueDetailView").Visibility == Visibility.Visible && Control<FrameworkElement>("QueueGalleryView").Visibility == Visibility.Collapsed,
                "opening a queue replaces the gallery with its videos");
            Check(!Control<Button>("PlaySavedQueueButton").IsEnabled, "empty queue disables playback");
            Check(Control<Button>("AddQueueVideosButton").Style == window.FindResource("Primary"), "empty queue promotes adding videos");
            Check(Control<ListBox>("SavedQueueItems").ActualHeight >= 120, "smallest queue layout leaves room for entries without clipped empty-state text");
            Capture("queues-empty-900");
            var file = Path.Combine(App.DataDirectory, "ui-queue-fixture.mp4"); File.WriteAllText(file, "UI-only fixture");
            var media = new MediaItem("ui-available", file, "Available video", "Movie");
            ((List<MediaItem>)Field("items")).Add(media);
            queues.Add(new("filled", "Mixed availability", [media.Id, "absent", media.Id]));
            Call("OpenQueue", "filled");
            Check(Control<Button>("PlaySavedQueueButton").IsEnabled, "one available file enables playback despite missing queue entries");
            var entries = Control<ListBox>("SavedQueueItems"); entries.SelectedIndex = 1;
            Call("RemoveSavedEntry", queues.Single(x => x.Id == "filled"), entries.SelectedItem!);
            Check(queues.Single(x => x.Id == "filled").MediaIds.SequenceEqual([media.Id, media.Id]) && entries.SelectedIndex == 1,
                "removing the selected queue entry keeps the selection in place");
            var addButton = Control<Button>("DetailQueue");
            Call("ShowAddToQueueMenu", addButton, new[] { media });
            var addChoices = addButton.ContextMenu!.Items.Cast<object>().ToArray();
            Check(addChoices.OfType<MenuItem>().Select(item => (string)item.Header).SequenceEqual(["Empty queue", "Mixed availability", "New queue…"]) &&
                addChoices[0] is MenuItem && addChoices[^2] is Separator && addChoices.OfType<Separator>().Count() == 1,
                "Add to queue lists the queues directly, with one divider before New queue");
            addButton.ContextMenu.IsOpen = false;
            File.Delete(file); Call("UpdateSavedQueueActions");
            Check(!Control<Button>("PlaySavedQueueButton").IsEnabled && Control<TextBlock>("SavedQueueFeedback").Text.Contains("No available videos"), "disappeared file disables playback and provides inline recovery");
            Call("AddQueueVideos", window, new RoutedEventArgs());
            // File checks for Add videos run in the background; let them finish.
            for (var until = DateTime.UtcNow.AddSeconds(5); Control<FrameworkElement>("FoldersPage").Visibility != Visibility.Visible && DateTime.UtcNow < until;)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame); Thread.Sleep(10);
            }
            Check(Control<FrameworkElement>("FoldersPage").Visibility == Visibility.Visible, "adding videos with no available files opens useful folder setup");
            Call("ShowPage", "Queues");

            Call("SetStatus", "This queue has no available videos."); Call("ShowPage", "Settings");
            Check(!Control<TextBlock>("StatusText").Text.Contains("queue"), "page-scoped queue error clears on navigation");
            typeof(MainWindow).GetField("scanning", flags)!.SetValue(window, true);
            Control<TextBlock>("ScanStatus").Text = "Scanning test fixtures";
            Call("ShowPage", "Queues"); Call("ShowPage", "Folders");
            Check(Control<TextBlock>("StatusText").Text == "Scanning test fixtures", "ongoing scan feedback survives navigation");
            typeof(MainWindow).GetField("scanning", flags)!.SetValue(window, false);
            // The status bar's indeterminate progress uses the app's ProgressBar template; show one for real.
            var sweepBar = new ProgressBar { IsIndeterminate = true, Width = 96, Height = 3 };
            var sweepHost = new Window { Width = 200, Height = 80, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000, Top = -2000, Content = sweepBar };
            void Settle()
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            sweepHost.Show(); Settle();
            var sweep = (TranslateTransform)sweepBar.Template.FindName("SweepOffset", sweepBar);
            Check(sweepBar.IsVisible && sweep.HasAnimatedProperties, "indeterminate progress sweeps while it is shown");
            sweepBar.Visibility = Visibility.Collapsed; Settle();
            Check(!sweep.HasAnimatedProperties, "hidden progress stops its sweep instead of rendering frames for the rest of the session");
            sweepHost.Close();

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
            Check(Control<FrameworkElement>("SavedRoomHostControls").Visibility == Visibility.Collapsed && Control<TextBlock>("SavedRoomGuestHeading").Text == "PEOPLE",
                "guest room hides host-only controls and lists people instead of managing guests");

            var folders = (ObservableCollection<LibraryFolder>)Field("folders"); folders.Clear();
            folders.Add(new(App.DataDirectory, "Anime")); Control<ListBox>("FolderList").SelectedIndex = 0;
            var kinds = Control<WrapPanel>("FolderKinds").Children.OfType<RadioButton>().ToArray();
            Check(Control<FrameworkElement>("FolderEditor").Visibility == Visibility.Visible && kinds.Single(x => x.IsChecked == true).Tag as string == "Anime",
                "selecting a folder reveals editing with its existing category");
            // One click applies a category; the folder stays selected while the list is rebuilt.
            kinds.Single(x => (string)x.Tag == "Show").IsChecked = true;
            ((System.Windows.Threading.DispatcherTimer)Field("scanDelay")).Stop();
            Check(folders.Single().Kind == "Show" && Control<ListBox>("FolderList").SelectedItem?.ToString()?.Contains("Shows ·") == true,
                "choosing a category applies it at once and keeps the folder selected");
            var exclusionList = (ObservableCollection<string>)Field("exclusions");
            var skipped = Path.Combine(App.DataDirectory, "extras", "samples"); var elsewhere = Path.Combine(Path.GetPathRoot(App.DataDirectory)!, "BlackPearl elsewhere check");
            exclusionList.Add(skipped); exclusionList.Add(elsewhere);
            Check(Control<ItemsControl>("FolderExclusionList").Items.Count == 1 && Control<TextBlock>("FolderExclusionsLabel").Text.EndsWith("· 1")
                && Control<FrameworkElement>("OtherExclusionsPanel").Visibility == Visibility.Visible,
                "skipped subfolders sit under their folder, and others are listed apart");
            exclusionList.Remove(skipped); exclusionList.Remove(elsewhere);
            // A drive root keeps its separator, so it must not gain a second one before matching paths inside it.
            var inside = typeof(MainWindow).GetMethod("Inside", BindingFlags.Static | BindingFlags.NonPublic)!;
            bool Inside(string path, string folder) => (bool)inside.Invoke(null, [path, folder])!;
            Check(Inside(@"E:\Movies\Film.mkv", @"E:\") && Inside(@"E:\Movies\Film.mkv", @"E:\Movies") && Inside(@"E:\Movies\Film.mkv", @"E:\Movies\")
                && !Inside(@"E:\Movies", @"E:\Movies") && !Inside(@"E:\Movies2\Film.mkv", @"E:\Movies") && !Inside(@"F:\Movies\Film.mkv", @"E:\"),
                "folders match the paths inside them, including drive roots");
            Check(Control<FrameworkElement>("FolderNoExclusions").Visibility == Visibility.Visible && Control<FrameworkElement>("OtherExclusionsPanel").Visibility == Visibility.Collapsed,
                "a folder with nothing skipped says so");
            Call("ShowPage", "Folders"); Layout(); Capture("folder-selected-900");
            Control<ListBox>("FolderList").SelectedIndex = -1;
            Check(Control<FrameworkElement>("FolderEditor").Visibility == Visibility.Collapsed, "no folder selection hides category editing");

            Call("ShowPage", "Settings"); Call("ShowPreferenceSection", "Profile");
            bool Pending() => (bool)typeof(MainWindow).GetProperty("HasUnsavedPreferences", flags)!.GetValue(window)!;
            var autoSave = (System.Windows.Threading.DispatcherTimer)Field("preferencesAutoSave");
            string SavedName() => (string)typeof(MainWindow).GetProperty("SavedDisplayName", flags)!.GetValue(window)!;
            Check(window.FindName("SavePreferencesButton") is null && window.FindName("CancelPreferencesButton") is null && window.FindName("PreferenceFooter") is null,
                "settings apply as they change, with no Save or Cancel bar");
            var originalName = Control<TextBox>("DisplayNameBox").Text;
            Control<TextBox>("DisplayNameBox").Text = "Draft name";
            Check(Pending() && autoSave.IsEnabled && SavedName() == originalName, "typing waits for a pause before applying");
            Call("ShowPage", "Settings");
            Check(Control<TextBox>("DisplayNameBox").Text == "Draft name", "selecting the current settings page preserves what is being typed");
            Call("ApplyPreferences");
            Check(SavedName() == "Draft name" && store.Setting("name") == "Draft name" && !Pending() && !autoSave.IsEnabled, "a typed name applies after the pause");
            Control<TextBox>("DisplayNameBox").Text = " "; Call("ApplyPreferences");
            Check(SavedName() == "Draft name" && Control<TextBlock>("DisplayNameError").Text.Length > 0 && Pending() &&
                Control<FrameworkElement>("ProfilePreferences").Visibility == Visibility.Visible, "an invalid value shows its error and is not applied");
            Call("ShowPreferenceSection", "Playback"); Control<TextBox>("DisplayNameBox").Text = " x"; Call("ApplyPreferences");
            Check(Control<FrameworkElement>("PlaybackPreferences").Visibility == Visibility.Visible && store.Setting("name") == "x",
                "applying a change never switches the settings section");
            Control<TextBox>("DisplayNameBox").Text = originalName; Call("ApplyPreferences");
            Check(store.Setting("name") == originalName.Trim() && !Pending(), "restoring the name applies it");
            Call("ShowPreferenceSection", "Metadata");
            var saveNfo = Control<CheckBox>("SaveNfo").IsChecked == true;
            Control<CheckBox>("SaveNfo").IsChecked = !saveNfo;
            Check(MetadataOptions.Load(store).SaveNfo == !saveNfo && !Pending() && !autoSave.IsEnabled, "switches apply at once");
            Control<CheckBox>("SaveNfo").IsChecked = saveNfo;
            Check(MetadataOptions.Load(store).SaveNfo == saveNfo, "switching back applies at once too");
            Check(window.FindName("SaveMetadataKeyButton") is null, "TMDB keys apply like other settings, without a separate key button");
            Control<PasswordBox>("MetadataToken").Password = "draft-fixture-key";
            Check(Pending() && MetadataCredential.Load(App.DataDirectory) == "", "a typed key waits for a pause before writing the credential vault");
            Call("LoadPreferences");
            Check(Control<PasswordBox>("MetadataToken").Password == "" && !Pending() && !autoSave.IsEnabled, "reloading settings drops a key that hasn't applied yet");
            // The checks below build drafts across several fields to test validation, so applying as they change is held meanwhile.
            typeof(MainWindow).GetField("applyingPreferences", flags)!.SetValue(window, true); var appliedAudio = store.Setting("audioLanguages"); var appliedRules = store.Setting("subtitleLanguageRules");
            Call("ShowPreferenceSection", "Playback"); Layout();
            var audioPriority = Control<LanguagePriorityPicker>("PreferredAudioLanguages");
            var subtitlePriority = Control<LanguagePriorityPicker>("PreferredSubtitleLanguages");
            var audioOptions = (ComboBox)audioPriority.FindName("LanguageOptions");
            Check(!audioOptions.IsEditable && new[] { "en", "ru", "ja" }.All(code => audioOptions.Items.Cast<LanguagePriorityPicker.LanguageOption>().Any(option => option.Code == code)) &&
                !audioOptions.Items.Cast<LanguagePriorityPicker.LanguageOption>().Any(option => option.Code == "off"), "audio priority offers named language choices without an audio Off option");
            AddLanguage(audioPriority, "ru"); AddLanguage(audioPriority, "en"); Layout();
            Check(audioPriority.Text == "ru, en" && !audioOptions.Items.Cast<LanguagePriorityPicker.LanguageOption>().Any(option => option.Code == "ru"),
                "adding languages preserves priority and excludes duplicate choices");
            var audioRows = (ItemsControl)audioPriority.FindName("Priorities");
            Descendants<Button>(audioPriority).First(button => Equals(button.Tag, "Down") && ReferenceEquals(button.DataContext, audioRows.Items[0]))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Check(audioPriority.Text == "en, ru", "priority arrows change the language order");
            Descendants<Button>(audioPriority).First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove language" && ReferenceEquals(button.DataContext, audioRows.Items[0]))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(audioPriority.Text == "ru" && Pending() && store.Setting("audioLanguages") == appliedAudio,
                "removing a language updates only the settings draft until it applies");
            AddLanguage(subtitlePriority, "off");
            Check(subtitlePriority.Text == "off", "subtitle priority offers an explicit Off choice");
            Call("LoadPreferences");
            Call("AddSubtitleRule", window, new RoutedEventArgs()); Layout();
            var ruleControls = Control<ItemsControl>("SubtitleLanguageRules");
            Descendants<ComboBox>(ruleControls).First(picker => System.Windows.Automation.AutomationProperties.GetName(picker) == "Audio language for subtitle rule").SelectedValue = "ja";
            var rulePriority = Descendants<LanguagePriorityPicker>(ruleControls).Single();
            AddLanguage(rulePriority, "en"); AddLanguage(rulePriority, "off");
            Check((string)subtitleRules[0]!.GetType().GetProperty("AudioLanguage")!.GetValue(subtitleRules[0])! == "ja" &&
                (string)subtitleRules[0]!.GetType().GetProperty("SubtitleLanguages")!.GetValue(subtitleRules[0])! == "en, off",
                "audio-specific subtitle rules can be configured entirely with language pickers");
            Call("LoadPreferences");
            AddRule("Japanese", "Russian, English, off");
            Check(Pending() && store.Setting("subtitleLanguageRules") == appliedRules, "audio-specific subtitle drafts are pending without writing settings");
            Call("LoadPreferences");
            Check(subtitleRules.Count == 0 && !Pending(), "reloading discards added subtitle rules");
            Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "Russian, English";
            Control<LanguagePriorityPicker>("PreferredSubtitleLanguages").Text = "English, off";
            Check(Pending() && ((string[])Field("audioLanguages")).Length == 0,
                "track language drafts are pending without changing active preferences");
            Call("LoadPreferences");
            Check(Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text == "" && Control<LanguagePriorityPicker>("PreferredSubtitleLanguages").Text == "",
                "reloading restores both track language preferences");
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
            Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "Not a language";
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("TrackPreferencesError").Text.Length > 0 &&
                Control<FrameworkElement>("PlaybackPreferences").Visibility == Visibility.Visible && store.Setting("name") != "Saved name",
                "invalid track preferences identify Playback and do not partially save settings");
            Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text = "Russian, English";
            Control<LanguagePriorityPicker>("PreferredSubtitleLanguages").Text = "English, off";
            AddRule("Japanese", "Russian, English, off"); AddRule("ja-JP", "off");
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("TrackPreferencesError").Text.Contains("already") && store.Setting("subtitleLanguageRules") == appliedRules,
                "duplicate audio rules reject the whole draft with an actionable error");
            Call("RemoveSubtitleRule", new Button { DataContext = subtitleRules[1] }, new RoutedEventArgs());
            AddRule("English", "off");
            var providers = (IList)Field("providerChoices");
            foreach (var provider in providers) provider!.GetType().GetProperty("Enabled")!.SetValue(provider, false);
            Control<CheckBox>("NeverRefresh").IsChecked = true;
            Control<ComboBox>("MetadataLanguage").SelectedValue = "fr-FR"; Control<ComboBox>("MetadataCountry").SelectedValue = "FR";
            Control<PasswordBox>("MetadataToken").Password = new string('x', 1281);
            Check(!(bool)Call("TrySavePreferences")! && Control<TextBlock>("MetadataKeyStatus").Text.Contains("too long") && store.Setting("audioLanguages") == appliedAudio && store.Setting("name") != "Saved name",
                "credential storage failure identifies Metadata and leaves other settings unsaved");
            Control<PasswordBox>("MetadataToken").Password = " fixture-key-one ";
            Check((bool)Call("TrySavePreferences")!, "valid structured preferences save successfully");
            Check(store.Setting("audioLanguages") == "ru, en" && store.Setting("subtitleLanguages") == "en, off",
                "applying persists normalized audio and subtitle preferences together");
            var savedRules = Wire.Read<SubtitleLanguageRule[]>(store.Setting("subtitleLanguageRules")!);
            Check(savedRules.Length == 2 && savedRules[0].AudioLanguage == "ja" && savedRules[0].Languages.SequenceEqual(["ru", "en", "off"]) && savedRules[1].Languages.SequenceEqual(["off"]),
                "applying persists audio-specific subtitle priority lists in order");
            Check(MetadataCredential.Load(App.DataDirectory) == "fixture-key-one" && (string)Field("savedMetadataToken") == "fixture-key-one",
                "applying stores the trimmed key in the credential vault and activates it");
            Check(!((string)Call("PreferenceSnapshot")!).Contains("fixture-key-one"), "preference snapshots exclude the secret key");
            var restoredWindow = new MainWindow();
            try
            {
                Check(((PasswordBox)restoredWindow.FindName("MetadataToken")).Password == "fixture-key-one" &&
                    !(bool)typeof(MainWindow).GetProperty("HasUnsavedPreferences", flags)!.GetValue(restoredWindow)!, "a reopened settings window restores the saved key without an unsaved draft");
            }
            finally { restoredWindow.Close(); }
            var options = MetadataOptions.Load(store);
            Check(options.ProviderOrder.Length == 0 && options.RefreshDays == 0 && options.Language == "fr-FR" && options.Country == "FR", "local-only providers, Never and locale selections round-trip");
            Call("LoadPreferences");
            Check(Control<CheckBox>("NeverRefresh").IsChecked == true && !Control<TextBox>("MetadataRefreshDays").IsEnabled, "Never restores with its numeric field disabled");
            Check(Control<LanguagePriorityPicker>("PreferredAudioLanguages").Text == "ru, en" && !Pending(),
                "saved track preferences reload without leaving a dirty draft");
            Check(subtitleRules.Count == 2 && (string)subtitleRules[0]!.GetType().GetProperty("AudioLanguage")!.GetValue(subtitleRules[0])! == "ja",
                "saved subtitle rules reload with normalized audio languages");
            Control<PasswordBox>("MetadataToken").Password = "replacement-fixture-key";
            Call("LoadPreferences");
            Check(Control<PasswordBox>("MetadataToken").Password == "fixture-key-one" && MetadataCredential.Load(App.DataDirectory) == "fixture-key-one", "reloading restores the saved key after an unapplied replacement");
            Control<PasswordBox>("MetadataToken").Clear();
            Check(MetadataCredential.Load(App.DataDirectory) == "fixture-key-one", "clearing a key remains a draft until it applies");
            Check((bool)Call("TrySavePreferences")! && MetadataCredential.Load(App.DataDirectory) == "" && (string)Field("savedMetadataToken") == "", "applying removes a cleared key from the vault and active metadata client");
            var first = providers[0]!; first.GetType().GetProperty("Enabled")!.SetValue(first, true);
            var move = new Button { Tag = "Down", DataContext = first }; Call("ProviderMoved", move, new RoutedEventArgs());
            Check(ReferenceEquals(providers[1], first), "provider move changes the fallback ordering");
            Check((bool)Call("TrySavePreferences")! && MetadataOptions.Load(store).ProviderOrder.Length == 1, "provider toggles persist independently of ordering");
            typeof(MainWindow).GetField("applyingPreferences", flags)!.SetValue(window, false);
            Control<CheckBox>("NeverRefresh").IsChecked = false;
            Check(MetadataOptions.Load(store).RefreshDays > 0 && !Pending(), "applying resumes once the draft checks finish");

            foreach (var size in new[] { (900, 600), (1280, 810) })
            foreach (var section in new[] { "Profile", "Metadata", "Playback", "Voice", "Appearance", "Updates" })
            {
                Call("ShowPreferenceSection", section); Layout(size.Item1, size.Item2);
                if (section == "Playback") { Control<ScrollViewer>("PlaybackPreferences").ScrollToHome(); Layout(size.Item1, size.Item2); Capture("playback-" + size.Item1); }
                if (section == "Metadata") { Control<ScrollViewer>("MetadataPreferences").ScrollToHome(); Layout(size.Item1, size.Item2); Capture("metadata-" + size.Item1); }
                if (section == "Voice") { Control<Slider>("VoiceVolumeSlider").BringIntoView(); Layout(size.Item1, size.Item2); Capture("voice-" + size.Item1); }
            }
            Call("ToggleTheme", window, new RoutedEventArgs()); Call("ShowPreferenceSection", "Playback");
            foreach (var size in new[] { (900, 600), (1280, 810) }) { Layout(size.Item1, size.Item2); Capture("playback-light-" + size.Item1); }
            Call("ShowPage", "Welcome"); Layout(); Capture("welcome-900");
            Check(Control<FrameworkElement>("WelcomePage").Visibility == Visibility.Visible && Control<FrameworkElement>("SettingsPage").Visibility == Visibility.Collapsed,
                "welcome provides a separate first-run choice before folder management");
            Console.WriteLine($"{checks} UI findings checks passed.");
        }
        finally { MetadataCredential.Save(App.DataDirectory, ""); window.Close(); }
    }
}
