using LibVLCSharp.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private sealed class SubtitleRuleDraft(string audio, string subtitles) : INotifyPropertyChanged
    {
        private string audioLanguage = TrackLanguages.Normalize(audio) ?? audio, subtitleLanguages = subtitles;
        public string AudioLanguage { get => audioLanguage; set { audioLanguage = TrackLanguages.Normalize(value) ?? value ?? ""; PropertyChanged?.Invoke(this, new(nameof(AudioLanguage))); } }
        public string SubtitleLanguages { get => subtitleLanguages; set { subtitleLanguages = value; PropertyChanged?.Invoke(this, new(nameof(SubtitleLanguages))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<SubtitleRuleDraft> subtitleRuleDrafts = [];
    private string[] audioLanguages = [], subtitleLanguages = [];
    private string[] videoAudioLanguages = [], videoSubtitleLanguages = [];
    private SubtitleLanguageRule[] subtitleLanguageRules = [], videoSubtitleLanguageRules = [];
    private string? subtitleAudioLanguage;
    private int? videoDefaultSubtitle;
    private int preferredTrackGeneration = -1;
    private bool audioTrackChosen, subtitleTrackChosen, subtitleTrackManuallyChosen;

    private void LoadTrackPreferences()
    {
        audioLanguages = subtitleLanguages = []; subtitleLanguageRules = [];
        try { audioLanguages = TrackLanguages.Parse(library.Setting("audioLanguages") ?? ""); } catch (ArgumentException) { }
        try { subtitleLanguages = TrackLanguages.Parse(library.Setting("subtitleLanguages") ?? "", true); } catch (ArgumentException) { }
        try
        {
            var savedRules = Wire.Read<SubtitleLanguageRule[]>(library.Setting("subtitleLanguageRules") ?? "[]") ?? [];
            if (savedRules.Any(rule => rule?.AudioLanguage is null || rule.Languages is null)) throw new ArgumentException("Invalid saved subtitle rules.");
            subtitleLanguageRules = TrackLanguages.ParseSubtitleRules(savedRules.Select(rule =>
                new KeyValuePair<string, string>(rule.AudioLanguage, string.Join(", ", rule.Languages))));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { }
        PreferredAudioLanguages.Text = string.Join(", ", audioLanguages);
        PreferredSubtitleLanguages.Text = string.Join(", ", subtitleLanguages);
        subtitleRuleDrafts.Clear();
        foreach (var rule in subtitleLanguageRules) AddSubtitleRuleDraft(rule.AudioLanguage, string.Join(", ", rule.Languages));
    }
    private void AddSubtitleRuleDraft(string audio = "", string subtitles = "")
    {
        var draft = new SubtitleRuleDraft(audio, subtitles);
        draft.PropertyChanged += (_, _) => PreferencesChanged(this, new RoutedEventArgs());
        subtitleRuleDrafts.Add(draft);
    }
    private void AddSubtitleRule(object sender, RoutedEventArgs e)
    {
        AddSubtitleRuleDraft(); PreferencesChanged(sender, e);
        SubtitleLanguageRules.UpdateLayout();
        var container = SubtitleLanguageRules.ItemContainerGenerator.ContainerFromIndex(subtitleRuleDrafts.Count - 1) as FrameworkElement;
        container?.BringIntoView();
        container?.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }
    private void RemoveSubtitleRule(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SubtitleRuleDraft draft) return;
        subtitleRuleDrafts.Remove(draft); PreferencesChanged(sender, e);
    }
    private bool TryReadTrackPreferences(out string[] audio, out string[] subtitles, out SubtitleLanguageRule[] rules)
    {
        audio = subtitles = []; rules = [];
        try
        {
            audio = TrackLanguages.Parse(PreferredAudioLanguages.Text);
            subtitles = TrackLanguages.Parse(PreferredSubtitleLanguages.Text, true);
            rules = TrackLanguages.ParseSubtitleRules(subtitleRuleDrafts.Select(rule =>
                new KeyValuePair<string, string>(rule.AudioLanguage, rule.SubtitleLanguages)));
            return true;
        }
        catch (ArgumentException ex) { TrackPreferencesError.Text = ex.Message; return false; }
    }
    private void PersistTrackPreferences(string[] audio, string[] subtitles, SubtitleLanguageRule[] rules)
    {
        audioLanguages = audio; subtitleLanguages = subtitles; subtitleLanguageRules = rules;
        library.Setting("audioLanguages", string.Join(", ", audio));
        library.Setting("subtitleLanguages", string.Join(", ", subtitles));
        library.Setting("subtitleLanguageRules", Wire.Serialize(rules));
    }
    private bool SaveTrackPreferences()
    {
        if (!TryReadTrackPreferences(out var audio, out var subtitles, out var rules)) return false;
        PersistTrackPreferences(audio, subtitles, rules); return true;
    }
    private LanguageTrack[] LanguageTracks(TrackChoice[] choices, TrackType type)
    {
        using var media = player?.Media;
        var metadata = media?.Tracks ?? [];
        return choices.Select(choice => new LanguageTrack(choice.Id, choice.Name,
            metadata.FirstOrDefault(track => track.Id == choice.Id && track.TrackType == type).Language)).ToArray();
    }
    private int? ApplySubtitlePreference(LanguageTrack? audio, LanguageTrack[] subtitles, bool restoreDefault = false)
    {
        if (player is null || subtitleTrackManuallyChosen || room?.Identity?.Host == false) return null;
        var preferences = TrackLanguages.SubtitlePreferences(audio, videoSubtitleLanguages, videoSubtitleLanguageRules);
        var match = TrackLanguages.Select(subtitles, preferences, true);
        if ((match ?? (restoreDefault ? videoDefaultSubtitle : null)) is not { } preferred ||
            preferred != -1 && !subtitles.Any(track => track.Id == preferred)) return null;
        if (player.Spu != preferred && !player.SetSpu(preferred)) return null;
        subtitleTrackChosen = match is not null; return preferred;
    }
    private int? ApplySubtitlesForAudio(int id)
    {
        if (player is null) return null;
        var audio = LanguageTracks(player.AudioTrackDescription.Select(track => new TrackChoice(track.Id, track.Name)).ToArray(), TrackType.Audio);
        var subtitles = LanguageTracks(player.SpuDescription.Select(track => new TrackChoice(track.Id, RoomSubtitleName(track.Id, track.Name))).ToArray(), TrackType.Text);
        var selected = audio.FirstOrDefault(track => track.Id == id);
        subtitleAudioLanguage = TrackLanguages.LanguageOf(selected);
        subtitleTrackChosen = false;
        return ApplySubtitlePreference(selected, subtitles, restoreDefault: true);
    }
    private void ApplyTrackPreferences(TrackChoice[] audio, TrackChoice[] subtitles)
    {
        if (player is null || !ready) return;
        if (room?.Identity?.Host == false) return;
        if (preferredTrackGeneration != loadGeneration)
        {
            preferredTrackGeneration = loadGeneration; audioTrackChosen = subtitleTrackChosen = subtitleTrackManuallyChosen = false;
            subtitleAudioLanguage = null;
            videoDefaultSubtitle = player.Spu;
            videoAudioLanguages = audioLanguages; videoSubtitleLanguages = subtitleLanguages;
            videoSubtitleLanguageRules = subtitleLanguageRules;
        }
        var audioChoices = LanguageTracks(audio, TrackType.Audio);
        if (room?.Snapshot?.Tracks?.Audio is null && !audioTrackChosen && TrackLanguages.Select(audioChoices, videoAudioLanguages) is { } preferredAudio)
            audioTrackChosen = player.AudioTrack == preferredAudio || player.SetAudioTrack(preferredAudio);
        var selectedAudio = audioChoices.FirstOrDefault(track => track.Id == player.AudioTrack);
        var language = TrackLanguages.LanguageOf(selectedAudio);
        var restoreDefault = language != subtitleAudioLanguage && subtitleTrackChosen;
        if (language != subtitleAudioLanguage) { subtitleAudioLanguage = language; subtitleTrackChosen = false; }
        if (room?.Snapshot?.Tracks?.Subtitles is null && !subtitleTrackChosen)
            ApplySubtitlePreference(selectedAudio, LanguageTracks(subtitles, TrackType.Text), restoreDefault);
    }
}
