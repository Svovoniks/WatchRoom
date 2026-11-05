using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

public partial class MainWindow
{
    private sealed record LocaleChoice(string Code, string Name);
    private sealed class ProviderChoice(string id, bool enabled) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public string Name => Id switch { "tmdb" => "TMDB", "tvmaze" => "TVmaze", _ => "Wikipedia" };
        public bool Enabled { get; set; } = enabled;
        public bool CanMoveUp { get; private set; }
        public bool CanMoveDown { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetPosition(int index, int count)
        {
            CanMoveUp = index > 0; CanMoveDown = index < count - 1;
            PropertyChanged?.Invoke(this, new(nameof(CanMoveUp)));
            PropertyChanged?.Invoke(this, new(nameof(CanMoveDown)));
        }
    }
    private readonly ObservableCollection<ProviderChoice> providerChoices = [];
    private bool loadingPreferences, preferencesReady, applyingPreferences;
    private string preferencesBaseline = "";
    private string preferenceSection = "";
    // Settings apply as they change. Typing waits for a pause, so a half-typed value isn't applied or flagged at every keystroke.
    private readonly DispatcherTimer preferencesAutoSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    // Choosing a language and then a country, or stepping through the list, starts one metadata refresh, not one per choice.
    private readonly DispatcherTimer metadataLocaleRefresh = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool HasUnsavedPreferences => preferencesReady &&
        (PreferenceSnapshot() != preferencesBaseline || MetadataToken.Password != savedMetadataToken);
    // Saving Settings stores the address shown, so earlier defaults also move to the current one: the retired Sites service,
    // and the VPS's IP-based name, which still answers so saved rooms and older invitations keep working.
    private const string DefaultServerAddress = "https://backrooms.svovolabs.com";
    private static readonly string[] RetiredServerAddresses = ["https://watchroom-rooms.svovoniks.chatgpt.site", "https://31-76-63-229.sslip.io"];
    private string SavedServerAddress => library.Setting("server") is { Length: > 0 } saved && !RetiredServerAddresses.Contains(saved) ? saved : DefaultServerAddress;
    private string SavedDisplayName => library.Setting("name") ?? Environment.UserName;
    private bool SavedAutoArtwork => library.Setting("artwork") != "false";
    private int SavedStreamingBufferSeconds => StreamingBufferOptions.Load(library);

    // Listing every culture takes tens of milliseconds, so it runs in the background while the window is built.
    private static readonly Task<(LocaleChoice[] Languages, LocaleChoice[] Countries)> localeChoices = Task.Run(() =>
    {
        var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
        var languages = cultures.Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Name, "^[a-z]{2}(?:-[A-Z]{2})?$"))
            .Select(c => new LocaleChoice(c.Name, c.EnglishName)).DistinctBy(c => c.Code).OrderBy(c => c.Name).ToArray();
        var countries = cultures.Where(c => !c.IsNeutralCulture && c.Name.Length > 0)
            .Select(c => { try { return new RegionInfo(c.Name); } catch (ArgumentException) { return null; } })
            .OfType<RegionInfo>().Where(r => r.TwoLetterISORegionName.Length == 2)
            .Select(r => new LocaleChoice(r.TwoLetterISORegionName, r.EnglishName)).DistinctBy(c => c.Code).OrderBy(c => c.Name).ToArray();
        return (languages, countries);
    });
    // An explicit static constructor starts static fields such as localeChoices when the first window is created,
    // rather than when one is first read.
    static MainWindow() { }

    private void InitializePreferences()
    {
        var (languages, countries) = localeChoices.GetAwaiter().GetResult();
        MetadataLanguage.ItemsSource = languages; MetadataCountry.ItemsSource = countries;
        MetadataProviders.ItemsSource = providerChoices;
        SubtitleLanguageRules.ItemsSource = subtitleRuleDrafts;
        LoadPreferences();
        foreach (var box in new[] { DisplayNameBox, ServerBox, MetadataRefreshDays, StreamingBufferSeconds }) box.TextChanged += PreferencesChanged;
        PreferredAudioLanguages.TextChanged += PreferencesChanged;
        PreferredSubtitleLanguages.TextChanged += PreferencesChanged;
        foreach (var box in new[] { AutoArtwork, ImportMissing, ImportUpcoming, SaveNfo, DisplaySpecials, GroupShows, NeverRefresh })
        { box.Checked += PreferencesChanged; box.Unchecked += PreferencesChanged; }
        MetadataLanguage.SelectionChanged += PreferencesChanged;
        MetadataCountry.SelectionChanged += PreferencesChanged;
        foreach (var box in new[] { VoicePushToTalk, VoiceAlwaysOn, VoiceDucking, VoiceAutoJoin })
        { box.Checked += PreferencesChanged; box.Unchecked += PreferencesChanged; }
        VoiceInputPicker.SelectionChanged += PreferencesChanged; VoiceOutputPicker.SelectionChanged += PreferencesChanged; VoiceProcessingPicker.SelectionChanged += PreferencesChanged;
        // The voice volume is heard as it moves, before the pause that saves it.
        VoiceVolumeSlider.ValueChanged += (sender, e) => { PreviewVoiceVolume(e.NewValue); PreferencesChanged(sender, new RoutedEventArgs()); };
        preferencesAutoSave.Tick += (_, _) => ApplyPreferences();
        metadataLocaleRefresh.Tick += (_, _) =>
        {
            metadataLocaleRefresh.Stop();
            _ = Guard(async () => { await FetchArtwork(lifetime.Token, followUp: true); await RefreshLibrary(); });
        };
        preferencesReady = true;
        ShowPreferenceSection("Folders");
    }
    private void LoadPreferences()
    {
        loadingPreferences = true;
        try
        {
            DisplayNameBox.Text = SavedDisplayName; ServerBox.Text = SavedServerAddress;
            AutoArtwork.IsChecked = SavedAutoArtwork;
            StreamingBufferSeconds.Text = SavedStreamingBufferSeconds.ToString(CultureInfo.InvariantCulture);
            MetadataToken.Password = savedMetadataToken;
            LoadTrackPreferences();
            LoadVoicePreferenceControls();
            var options = MetadataOptions.Load(library);
            providerChoices.Clear();
            foreach (var id in options.ProviderOrder.Concat(new[] { "tmdb", "tvmaze", "wikipedia" }).Distinct())
                if (id is "tmdb" or "tvmaze" or "wikipedia") providerChoices.Add(new(id, options.ProviderOrder.Contains(id)));
            UpdateProviderPositions();
            EnsureLocaleChoice(MetadataLanguage, options.Language);
            EnsureLocaleChoice(MetadataCountry, options.Country);
            MetadataLanguage.SelectedValue = options.Language; MetadataCountry.SelectedValue = options.Country;
            NeverRefresh.IsChecked = options.RefreshDays == 0;
            MetadataRefreshDays.Text = Math.Max(1, options.RefreshDays).ToString(CultureInfo.InvariantCulture);
            ImportMissing.IsChecked = options.ImportMissing; ImportUpcoming.IsChecked = options.ImportUpcoming;
            SaveNfo.IsChecked = options.SaveNfo; DisplaySpecials.IsChecked = options.DisplaySpecialsWithinSeasons;
            GroupShows.IsChecked = options.CombineShowFolders;
            MetadataRefreshDays.IsEnabled = RefreshLess.IsEnabled = RefreshMore.IsEnabled = NeverRefresh.IsChecked != true;
            preferencesBaseline = PreferenceSnapshot();
            preferencesAutoSave.Stop();
            ClearPreferenceErrors();
            MetadataKeyStatus.Text = savedMetadataToken.Length > 0 ? "Personal key saved securely." : TmdbCredentials.DefaultStatus;
        }
        finally { loadingPreferences = false; }
    }
    // Retain a previously saved locale even if this Windows installation omits it.
    private static void EnsureLocaleChoice(ComboBox picker, string code)
    {
        var choices = ((IEnumerable<LocaleChoice>)picker.ItemsSource).ToArray();
        if (!choices.Any(c => c.Code == code)) picker.ItemsSource = choices.Append(new LocaleChoice(code, code)).ToArray();
    }
    private string PreferenceSnapshot() => Wire.Serialize(new
    {
        bufferSeconds = StreamingBufferSeconds.Text, audio = PreferredAudioLanguages.Text, subtitles = PreferredSubtitleLanguages.Text,
        subtitleRules = subtitleRuleDrafts.Select(rule => new { rule.AudioLanguage, rule.SubtitleLanguages }).ToArray(),
        name = DisplayNameBox.Text, server = ServerBox.Text, artwork = AutoArtwork.IsChecked,
        providers = providerChoices.Select(p => new { p.Id, p.Enabled }).ToArray(), language = MetadataLanguage.SelectedValue,
        country = MetadataCountry.SelectedValue, days = MetadataRefreshDays.Text, never = NeverRefresh.IsChecked,
        missing = ImportMissing.IsChecked, upcoming = ImportUpcoming.IsChecked, nfo = SaveNfo.IsChecked,
        specials = DisplaySpecials.IsChecked, grouping = GroupShows.IsChecked, voice = DraftVoicePreferences()
    });
    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady || loadingPreferences) return;
        MetadataRefreshDays.IsEnabled = RefreshLess.IsEnabled = RefreshMore.IsEnabled = NeverRefresh.IsChecked != true;
        ClearPreferenceErrors();
        ValidateRefreshDays();
        ValidateStreamingBuffer();
        // Switches, pickers and buttons apply at once; typed text and slider moves apply after a pause.
        preferencesAutoSave.Stop();
        if (sender is ButtonBase or Selector) ApplyPreferences(); else if (HasUnsavedPreferences) preferencesAutoSave.Start();
        // The microphone test follows the chosen microphone and processing before they are saved.
        if (sender == VoiceInputPicker || sender == VoiceProcessingPicker) _ = Guard(RestartVoiceTest);
    }
    private bool ValidateStreamingBuffer()
    {
        var valid = StreamingBufferOptions.TryParse(StreamingBufferSeconds.Text, out _);
        StreamingBufferError.Text = valid ? "" : "Enter a whole number from 2 to 60 seconds.";
        return valid;
    }
    private bool ValidateRefreshDays()
    {
        var valid = NeverRefresh.IsChecked == true || int.TryParse(MetadataRefreshDays.Text, out var days) && days is >= 1 and <= 365;
        RefreshDaysError.Text = valid ? "" : "Enter a whole number from 1 to 365, or choose Never.";
        return valid;
    }
    private void ChangeRefreshDays(object sender, RoutedEventArgs e)
    {
        var days = int.TryParse(MetadataRefreshDays.Text, out var value) ? value : 7;
        MetadataRefreshDays.Text = Math.Clamp(days + ((Button)sender == RefreshLess ? -1 : 1), 1, 365).ToString();
    }
    private void ProviderMoved(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProviderChoice provider) return;
        var index = providerChoices.IndexOf(provider);
        var destination = index + ((string)((Button)sender).Tag == "Up" ? -1 : 1);
        if (destination < 0 || destination >= providerChoices.Count) return;
        providerChoices.Move(index, destination); MetadataProviders.SelectedItem = provider;
        UpdateProviderPositions();
        PreferencesChanged(sender, e);
    }
    private void UpdateProviderPositions()
    {
        for (var i = 0; i < providerChoices.Count; i++) providerChoices[i].SetPosition(i, providerChoices.Count);
    }
    // Applies changes that are still waiting for a pause in typing. Invalid values stay unapplied and show their error.
    private void ApplyPreferences()
    {
        preferencesAutoSave.Stop();
        // Saving can update controls, such as the voice device pickers, whose change events would save again.
        if (applyingPreferences || !HasUnsavedPreferences) return;
        applyingPreferences = true;
        try { SavePreferences(reveal: false); } finally { applyingPreferences = false; }
    }
    // Leaving Settings applies what is pending, and asks only when a change is invalid and so can't be applied.
    private bool ConfirmPreferenceNavigation()
    {
        preferencesAutoSave.Stop();
        if (!HasUnsavedPreferences || SavePreferences(reveal: false)) return true;
        if (Dialogs.ConfirmUnsavedSettings(this) == MessageBoxResult.No) { LoadPreferences(); return true; }
        SavePreferences(reveal: true); // Opens and focuses the field that needs fixing.
        return false;
    }
    private bool ConfirmPageNavigation(string name) => name == currentPage || currentPage != "Settings" || ConfirmPreferenceNavigation();
    private void PreferenceSectionClicked(object sender, RoutedEventArgs e)
    {
        var section = (string)((Button)sender).Tag;
        if (section != preferenceSection && !ConfirmPreferenceNavigation()) return;
        ShowPreferenceSection(section);
    }
    private void ShowPreferenceSection(string section)
    {
        preferenceSection = section;
        foreach (var panel in new FrameworkElement[] { FoldersPage, PluginPreferences, DownloadsPreferences, ProfilePreferences, MetadataPreferences, PlaybackPreferences, VoicePreferences, AppearancePreferences, UpdatePreferences })
            panel.Visibility = (string)panel.Tag == section ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Folders") SelectFirstFolder();
        if (section == "Plugins") ShowPluginsSection();
        if (section == "Downloads") ShowDownloadsSection(); else downloadsRefresh.Stop();
        foreach (Button button in PreferenceSections.Children)
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, (string)button.Tag == section ? "Selected" : "");
    }
    private void ClearPreferenceErrors()
    {
        DisplayNameError.Text = ServerError.Text = RefreshDaysError.Text = LocaleError.Text = TrackPreferencesError.Text = StreamingBufferError.Text = "";
        MetadataKeyStatus.Text = "";
    }
    private bool TrySavePreferences() => SavePreferences(reveal: true);
    // Saves every setting shown once all are valid. With reveal, an invalid field's section opens and the field is focused;
    // otherwise only its error shows, so applying a change never moves the page or the caret.
    private bool SavePreferences(bool reveal)
    {
        void ShowPreferenceSection(string section) { if (reveal) this.ShowPreferenceSection(section); }
        void Focus(UIElement element) { if (reveal) { (element as FrameworkElement)?.BringIntoView(); element.Focus(); } }
        ClearPreferenceErrors();
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text))
        { DisplayNameError.Text = "Enter a display name."; ShowPreferenceSection("Profile"); Focus(DisplayNameBox); return false; }
        try { RoomAddress.ValidateServer(ServerBox.Text); }
        catch (ArgumentException ex) { ServerError.Text = ex.Message; ShowPreferenceSection("Profile"); Focus(ServerBox); return false; }
        if (!ValidateRefreshDays())
        { ShowPreferenceSection("Metadata"); Focus(MetadataRefreshDays); return false; }
        if (MetadataLanguage.SelectedValue is not string language || MetadataCountry.SelectedValue is not string country)
        { LocaleError.Text = "Choose a language and country."; ShowPreferenceSection("Metadata"); Focus(MetadataLanguage); return false; }
        if (!TryReadTrackPreferences(out var audio, out var subtitles, out var subtitleRules))
        { ShowPreferenceSection("Playback"); Focus(PreferredAudioLanguages); return false; }
        if (!ValidateStreamingBuffer())
        { ShowPreferenceSection("Playback"); Focus(StreamingBufferSeconds); return false; }
        if (!SaveMetadataToken())
        { ShowPreferenceSection("Metadata"); Focus(MetadataToken); return false; }
        PersistTrackPreferences(audio, subtitles, subtitleRules);
        library.Setting(StreamingBufferOptions.SettingKey, int.Parse(StreamingBufferSeconds.Text, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        var days = NeverRefresh.IsChecked == true ? 0 : int.Parse(MetadataRefreshDays.Text, CultureInfo.InvariantCulture);
        var before = MetadataOptions.Load(library);
        new MetadataOptions(providerChoices.Where(p => p.Enabled).Select(p => p.Id).ToArray(), language, country, days,
            ImportMissing.IsChecked == true, ImportUpcoming.IsChecked == true, SaveNfo.IsChecked == true,
            DisplaySpecials.IsChecked == true, GroupShows.IsChecked == true).Save(library);
        displaySpecialsWithinSeasons = DisplaySpecials.IsChecked == true;
        library.Setting("name", DisplayNameBox.Text.Trim()); library.Setting("server", ServerBox.Text.Trim().TrimEnd('/'));
        library.Setting("artwork", AutoArtwork.IsChecked == true ? "true" : "false");
        // Missing and upcoming episodes follow their settings now rather than at the next scan.
        if (SavedAutoArtwork && (before.ImportMissing != (ImportMissing.IsChecked == true) || before.ImportUpcoming != (ImportUpcoming.IsChecked == true)))
            _ = Guard(async () => { await FetchArtwork(); await RefreshLibrary(); });
        // So do titles and plots in a new language or country, after a pause for further changes. A fetch already running
        // stops at its next title, and this one follows it.
        if (SavedAutoArtwork && (before.Language != language || before.Country != country)) { metadataLocaleRefresh.Stop(); metadataLocaleRefresh.Start(); }
        SaveVoicePreferences();
        preferencesBaseline = PreferenceSnapshot();
        preferencesAutoSave.Stop(); // Normalising the key's text above restarts the pause, with nothing left to apply.
        MetadataKeyStatus.Text = savedMetadataToken.Length > 0 ? "Personal key saved securely." : TmdbCredentials.DefaultStatus;
        return true;
    }
    private void JoinFromWelcome(object sender, RoutedEventArgs e)
    {
        ShowPage("Rooms"); JoinClick(sender, e);
    }
}
