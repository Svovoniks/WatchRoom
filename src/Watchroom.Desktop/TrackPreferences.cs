using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private string[] audioLanguages = [], subtitleLanguages = [];
    private string[] videoAudioLanguages = [], videoSubtitleLanguages = [];
    private int preferredTrackGeneration = -1;
    private bool audioTrackChosen, subtitleTrackChosen;

    private void LoadTrackPreferences()
    {
        try { audioLanguages = TrackLanguages.Parse(library.Setting("audioLanguages") ?? ""); } catch (ArgumentException) { }
        try { subtitleLanguages = TrackLanguages.Parse(library.Setting("subtitleLanguages") ?? "", true); } catch (ArgumentException) { }
        PreferredAudioLanguages.Text = string.Join(", ", audioLanguages);
        PreferredSubtitleLanguages.Text = string.Join(", ", subtitleLanguages);
    }
    private bool SaveTrackPreferences()
    {
        try
        {
            var audio = TrackLanguages.Parse(PreferredAudioLanguages.Text);
            var subtitles = TrackLanguages.Parse(PreferredSubtitleLanguages.Text, true);
            audioLanguages = audio; subtitleLanguages = subtitles;
            library.Setting("audioLanguages", string.Join(", ", audio));
            library.Setting("subtitleLanguages", string.Join(", ", subtitles));
            return true;
        }
        catch (ArgumentException ex) { TrackPreferencesError.Text = ex.Message; return false; }
    }
    private void ApplyTrackPreferences(TrackChoice[] audio, TrackChoice[] subtitles)
    {
        if (player is null || !ready) return;
        if (room?.Identity?.Host == false) return;
        if (preferredTrackGeneration != loadGeneration)
        {
            preferredTrackGeneration = loadGeneration; audioTrackChosen = subtitleTrackChosen = false;
            videoAudioLanguages = audioLanguages; videoSubtitleLanguages = subtitleLanguages;
        }
        using var media = player.Media;
        var metadata = media?.Tracks ?? [];
        LanguageTrack[] Choices(TrackChoice[] choices, TrackType type) => choices.Select(choice =>
            new LanguageTrack(choice.Id, choice.Name, metadata.FirstOrDefault(track => track.Id == choice.Id && track.TrackType == type).Language)).ToArray();
        if (room?.Snapshot?.Tracks?.Audio is null && !audioTrackChosen && TrackLanguages.Select(Choices(audio, TrackType.Audio), videoAudioLanguages) is { } preferredAudio)
            audioTrackChosen = player.AudioTrack == preferredAudio || player.SetAudioTrack(preferredAudio);
        if (room?.Snapshot?.Tracks?.Subtitles is null && !subtitleTrackChosen && TrackLanguages.Select(Choices(subtitles, TrackType.Text), videoSubtitleLanguages, true) is { } preferredSubtitle)
            subtitleTrackChosen = player.Spu == preferredSubtitle || player.SetSpu(preferredSubtitle);
    }
}
