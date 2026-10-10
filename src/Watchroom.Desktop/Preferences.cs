using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Watchroom.Core;

namespace Watchroom.Desktop;

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
    private bool loadingPreferences, preferencesReady;
    private string preferencesBaseline = "";
    private string preferenceSection = "";
    private bool HasUnsavedPreferences => preferencesReady &&
        (PreferenceSnapshot() != preferencesBaseline || MetadataToken.Password != savedMetadataToken);
    private string SavedServerAddress => library.Setting("server") ?? "https://watchroom-rooms.svovoniks.chatgpt.site";
    private string SavedDisplayName => library.Setting("name") ?? Environment.UserName;
    private string SavedFfmpeg => library.Setting("ffmpeg") ?? "";
    private bool SavedAutoArtwork => library.Setting("artwork") != "false";

    private void InitializePreferences()
    {
        var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
        MetadataLanguage.ItemsSource = cultures.Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Name, "^[a-z]{2}(?:-[A-Z]{2})?$"))
            .Select(c => new LocaleChoice(c.Name, c.EnglishName)).DistinctBy(c => c.Code).OrderBy(c => c.Name).ToArray();
        MetadataCountry.ItemsSource = cultures.Where(c => !c.IsNeutralCulture && c.Name.Length > 0)
            .Select(c => { try { return new RegionInfo(c.Name); } catch (ArgumentException) { return null; } })
            .OfType<RegionInfo>().Where(r => r.TwoLetterISORegionName.Length == 2)
            .Select(r => new LocaleChoice(r.TwoLetterISORegionName, r.EnglishName)).DistinctBy(c => c.Code).OrderBy(c => c.Name).ToArray();
        MetadataProviders.ItemsSource = providerChoices;
        SubtitleLanguageRules.ItemsSource = subtitleRuleDrafts;
        LoadPreferences();
        foreach (var box in new[] { DisplayNameBox, ServerBox, FfmpegBox, MetadataRefreshDays }) box.TextChanged += PreferencesChanged;
        PreferredAudioLanguages.TextChanged += PreferencesChanged;
        PreferredSubtitleLanguages.TextChanged += PreferencesChanged;
        foreach (var box in new[] { AutoArtwork, ImportMissing, ImportUpcoming, SaveNfo, DisplaySpecials, GroupShows, NeverRefresh })
        { box.Checked += PreferencesChanged; box.Unchecked += PreferencesChanged; }
        MetadataLanguage.SelectionChanged += PreferencesChanged;
        MetadataCountry.SelectionChanged += PreferencesChanged;
        preferencesReady = true;
        ShowPreferenceSection("Profile");
    }
    private void LoadPreferences()
    {
        loadingPreferences = true;
        try
        {
            DisplayNameBox.Text = SavedDisplayName; ServerBox.Text = SavedServerAddress; FfmpegBox.Text = SavedFfmpeg;
            AutoArtwork.IsChecked = SavedAutoArtwork;
            MetadataToken.Password = savedMetadataToken;
            LoadTrackPreferences();
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
            GroupShows.IsChecked = options.GroupShowsByProvider;
            MetadataRefreshDays.IsEnabled = RefreshLess.IsEnabled = RefreshMore.IsEnabled = NeverRefresh.IsChecked != true;
            preferencesBaseline = PreferenceSnapshot();
            SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = false;
            PreferencesStatus.Text = "Changes apply when you save.";
            ClearPreferenceErrors();
            MetadataKeyStatus.Text = savedMetadataToken.Length > 0 ? "Key saved securely." : "";
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
        audio = PreferredAudioLanguages.Text, subtitles = PreferredSubtitleLanguages.Text,
        subtitleRules = subtitleRuleDrafts.Select(rule => new { rule.AudioLanguage, rule.SubtitleLanguages }).ToArray(),
        name = DisplayNameBox.Text, server = ServerBox.Text, ffmpeg = FfmpegBox.Text, artwork = AutoArtwork.IsChecked,
        providers = providerChoices.Select(p => new { p.Id, p.Enabled }).ToArray(), language = MetadataLanguage.SelectedValue,
        country = MetadataCountry.SelectedValue, days = MetadataRefreshDays.Text, never = NeverRefresh.IsChecked,
        missing = ImportMissing.IsChecked, upcoming = ImportUpcoming.IsChecked, nfo = SaveNfo.IsChecked,
        specials = DisplaySpecials.IsChecked, grouping = GroupShows.IsChecked
    });
    private void PreferencesChanged(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady || loadingPreferences) return;
        MetadataRefreshDays.IsEnabled = RefreshLess.IsEnabled = RefreshMore.IsEnabled = NeverRefresh.IsChecked != true;
        var dirty = HasUnsavedPreferences;
        SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = dirty;
        PreferencesStatus.Text = dirty ? "Unsaved changes · Save or cancel when ready." : "Changes apply when you save.";
        ClearPreferenceErrors();
        ValidateRefreshDays();
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
    private void CancelPreferences(object sender, RoutedEventArgs e) => LoadPreferences();
    private bool ConfirmPreferenceNavigation()
    {
        if (!HasUnsavedPreferences) return true;
        switch (Dialogs.ConfirmUnsavedSettings(this))
        {
            case MessageBoxResult.Yes: return TrySavePreferences();
            case MessageBoxResult.No: LoadPreferences(); return true;
            default: return false;
        }
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
        foreach (var panel in new[] { ProfilePreferences, MetadataPreferences, PlaybackPreferences, AppearancePreferences, UpdatePreferences })
            panel.Visibility = (string)panel.Tag == section ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button button in PreferenceSections.Children)
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, (string)button.Tag == section ? "Selected" : "");
    }
    private void ClearPreferenceErrors()
    {
        DisplayNameError.Text = ServerError.Text = RefreshDaysError.Text = LocaleError.Text = TrackPreferencesError.Text = "";
        MetadataKeyStatus.Text = "";
    }
    private bool TrySavePreferences()
    {
        ClearPreferenceErrors();
        if (string.IsNullOrWhiteSpace(DisplayNameBox.Text))
        { DisplayNameError.Text = "Enter a display name."; ShowPreferenceSection("Profile"); DisplayNameBox.Focus(); return false; }
        try { RoomAddress.ValidateServer(ServerBox.Text); }
        catch (ArgumentException ex) { ServerError.Text = ex.Message; ShowPreferenceSection("Profile"); ServerBox.Focus(); return false; }
        if (!ValidateRefreshDays())
        { ShowPreferenceSection("Metadata"); MetadataRefreshDays.BringIntoView(); MetadataRefreshDays.Focus(); return false; }
        if (MetadataLanguage.SelectedValue is not string language || MetadataCountry.SelectedValue is not string country)
        { LocaleError.Text = "Choose a language and country."; ShowPreferenceSection("Metadata"); MetadataLanguage.BringIntoView(); MetadataLanguage.Focus(); return false; }
        if (!TryReadTrackPreferences(out var audio, out var subtitles, out var subtitleRules))
        { ShowPreferenceSection("Playback"); PreferredAudioLanguages.BringIntoView(); return false; }
        if (!SaveMetadataToken())
        { ShowPreferenceSection("Metadata"); MetadataToken.BringIntoView(); MetadataToken.Focus(); return false; }
        PersistTrackPreferences(audio, subtitles, subtitleRules);
        var days = NeverRefresh.IsChecked == true ? 0 : int.Parse(MetadataRefreshDays.Text, CultureInfo.InvariantCulture);
        new MetadataOptions(providerChoices.Where(p => p.Enabled).Select(p => p.Id).ToArray(), language, country, days,
            ImportMissing.IsChecked == true, ImportUpcoming.IsChecked == true, SaveNfo.IsChecked == true,
            DisplaySpecials.IsChecked == true, GroupShows.IsChecked == true).Save(library);
        displaySpecialsWithinSeasons = DisplaySpecials.IsChecked == true;
        library.Setting("name", DisplayNameBox.Text.Trim()); library.Setting("server", ServerBox.Text.Trim().TrimEnd('/'));
        library.Setting("artwork", AutoArtwork.IsChecked == true ? "true" : "false"); library.Setting("ffmpeg", FfmpegBox.Text.Trim());
        preferencesBaseline = PreferenceSnapshot();
        SavePreferencesButton.IsEnabled = CancelPreferencesButton.IsEnabled = false;
        PreferencesStatus.Text = "Settings saved.";
        MetadataKeyStatus.Text = savedMetadataToken.Length > 0 ? "Key saved securely." : "";
        return true;
    }
    private void SaveSettings(object sender, RoutedEventArgs e) => TrySavePreferences();
    private void JoinFromWelcome(object sender, RoutedEventArgs e)
    {
        ShowPage("Rooms"); SaveRoomInvitation(sender, e);
    }
}
