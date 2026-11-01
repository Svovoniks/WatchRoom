using System.Globalization;
using System.IO;
using System.Text;
using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Tracks without a language tag get one from the bundled models (LanguageDetection), once per file: results are
// cached in the library settings. While a track is still being identified, its language counts as unknown and
// automatic track choices wait, so a preferred language found a few seconds in still wins.
// Subtitle files take the language their name gives ("Movie.ru.srt"), or else the language of their text.
public partial class MainWindow
{
    private sealed record SubtitleFile(string Path, string Name, int Generation);

    private readonly Dictionary<(TrackType Type, int Id), string?> detectedLanguages = [];
    private readonly HashSet<(TrackType Type, int Id)> namedLanguages = []; // from a subtitle file's name, not detected
    private readonly HashSet<(TrackType Type, int Id)> pendingLanguages = [];
    // Tracks the file leaves untagged: VLC calls them English (Matroska's default), which is no more than a guess.
    private readonly HashSet<(TrackType Type, int Id)> untaggedLanguages = [];
    // Subtitle tracks known to come from a file: shared with the room, or loaded from a file in local playback.
    private readonly Dictionary<int, SubtitleFile> subtitleFiles = [];
    private readonly List<object> loadingSubtitles = []; // files loaded in local playback, waiting for their tracks
    private int languageDetectionGeneration = -1;
    private CancellationTokenSource? languageDetectionCancellation;

    private string? LocalMediaPath()
    {
        using var media = player?.Media;
        return media?.Mrl is { } mrl && Uri.TryCreate(mrl, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    /// <summary>True while languages of this type (or of any track, for null) may still change for the loaded video.</summary>
    private bool TrackLanguagesPending(TrackType? type = null) =>
        languageDetectionGeneration == loadGeneration && pendingLanguages.Any(track => type is null || track.Type == type);

    /// <summary>The language a track is known by: detected, unknown while pending, otherwise VLC's metadata and name.</summary>
    private (string Name, string? Language) TrackLanguage(TrackType type, int id, string name, string? metadata)
    {
        if (languageDetectionGeneration != loadGeneration) return (name, metadata);
        if (detectedLanguages.TryGetValue((type, id), out var detected) && detected is not null) return (name, detected);
        return pendingLanguages.Contains((type, id)) || untaggedLanguages.Contains((type, id)) ? (TrackTitle(name), null) : (name, metadata);
    }

    private string TrackDisplayName(TrackType type, int id, string name)
    {
        if (id < 0 || languageDetectionGeneration != loadGeneration) return name;
        var title = TrackTitle(name);
        var generic = title.Length == 0 || GenericTrackTitle().IsMatch(title);
        // A loaded file's track is better known by the file's name than VLC's "Track 3".
        if (generic && type == TrackType.Text && subtitleFiles.TryGetValue(id, out var file) && file.Generation == loadGeneration) { title = file.Name; generic = false; }
        if (detectedLanguages.TryGetValue((type, id), out var detected) && detected is not null)
            return namedLanguages.Contains((type, id)) ? (title.Length == 0 ? "Track" : title) + " - [" + LanguageName(detected) + "]"
                : (generic ? "Detected" : title + " · detected") + " - [" + LanguageName(detected) + "]";
        if (pendingLanguages.Contains((type, id))) return (title.Length == 0 ? "Track" : title) + " · identifying language";
        return untaggedLanguages.Contains((type, id)) ? (title.Length == 0 ? "Track" : title) + " - [Unknown language]" : name;
    }
    private static string TrackTitle(string name) => TrackNamePattern().Match(name) is { Success: true } match ? match.Groups[1].Value.Trim() : name;
    private static string LanguageName(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return code; }
    }
    // VLC's codecs for subtitles it read from a separate file: SubRip and other plain formats, SSA/ASS, WebVTT.
    private static bool SubtitleFileCodec(uint codec) => Encoding.ASCII.GetString(BitConverter.GetBytes(codec)) is "subt" or "ssa " or "wvtt";

    /// <summary>
    /// Once per loaded video: finds untagged tracks (Matroska's and MP4's own headers, as VLC reports an absent Matroska
    /// tag as English), applies cached results and starts identifying the rest. Only local files; guests stream from
    /// the host. Subtitle files are handled as they're added (DetectSubtitleFiles).
    /// </summary>
    private void DetectTrackLanguages()
    {
        if (player is null || !ready || languageDetectionGeneration == loadGeneration) return;
        languageDetectionCancellation?.Cancel(); languageDetectionCancellation = null;
        languageDetectionGeneration = loadGeneration;
        detectedLanguages.Clear(); namedLanguages.Clear(); pendingLanguages.Clear(); untaggedLanguages.Clear();
        // Songs aren't speech to identify, and neither is the next track.
        if (PlayingKnownMusic) return;
        foreach (var stale in subtitleFiles.Where(pair => pair.Value.Generation != loadGeneration).Select(pair => pair.Key).ToArray()) subtitleFiles.Remove(stale);
        PrefetchNextLanguages();
        if (room?.Identity?.Host == false || LocalMediaPath() is not { } path) return;

        var container = SubtitleText.Tracks(path); // the header only: a few kilobytes, or MP4's moov
        var subtitles = new Dictionary<int, int>(); // VLC track id → container track number
        var audio = new List<int>();
        var sidecarTracks = new List<int>();
        using (var media = player.Media)
        {
            var tracks = media?.Tracks ?? [];
            if (container.Count > 0)
                // VLC numbers Matroska and MP4 tracks by their position in the file.
                foreach (var track in tracks)
                {
                    if (track.Id < 0 || track.Id >= container.Count) continue;
                    var entry = container[track.Id];
                    if (!entry.Untagged) continue;
                    if (track.TrackType == TrackType.Audio && entry.Type == ContainerTrackType.Audio) { audio.Add(track.Id); untaggedLanguages.Add((TrackType.Audio, track.Id)); }
                    else if (track.TrackType == TrackType.Text && entry.Type == ContainerTrackType.Subtitle)
                    {
                        untaggedLanguages.Add((TrackType.Text, track.Id));
                        if (entry.Text || entry.Image && SubtitleOcr.Available) subtitles[track.Id] = entry.Number;
                    }
                }
            else
                audio.AddRange(player.AudioTrackDescription.Where(choice => choice.Id >= 0 && TrackLanguages.LanguageOf(new LanguageTrack(choice.Id, choice.Name,
                    tracks.FirstOrDefault(track => track.Id == choice.Id && track.TrackType == TrackType.Audio).Language)) is null).Select(choice => choice.Id));
            // Local playback lets VLC load subtitle files beside the video; their tracks follow the file's own.
            if (room is null)
                sidecarTracks.AddRange(tracks.Where(track => track.TrackType == TrackType.Text && track.Id >= 0 && !subtitleFiles.ContainsKey(track.Id) &&
                    (container.Count > 0 ? track.Id >= container.Count : SubtitleFileCodec(track.Codec)) && TrackLanguages.Normalize(track.Language) is null).Select(track => track.Id));
        }
        // VLC doesn't say which file became which track, and its order isn't stable, so untagged sidecars are only named
        // when they're the files VLC left untagged one for one, and then only with a language all of them share.
        var sidecars = sidecarTracks.Count == 0 ? [] : SubtitleFiles.VlcSidecars(path).Where(SubtitleFiles.VlcUntagged).ToArray();
        if (sidecars.Length != sidecarTracks.Count) sidecarTracks.Clear();
        if (!LanguageDetection.SpeechAvailable) audio.Clear();
        if (audio.Count == 0 && subtitles.Count == 0 && sidecarTracks.Count == 0) return;

        if (LibraryStore.TrackLanguageFile(path) is not { } fileKey) return;
        foreach (var (type, id) in audio.Select(id => (TrackType.Audio, id)).Concat(subtitles.Keys.Select(id => (TrackType.Text, id))))
        {
            if (library.Setting(LibraryStore.TrackLanguageKey(fileKey, type == TrackType.Audio, id)) is { } cached) detectedLanguages[(type, id)] = cached.Length == 0 ? null : cached;
            else pendingLanguages.Add((type, id));
        }
        // Subtitle files can change between plays, and reading them is quick, so they aren't cached.
        foreach (var id in sidecarTracks) pendingLanguages.Add((TrackType.Text, id));
        if (pendingLanguages.Count == 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        languageDetectionCancellation = cancellation;
        _ = IdentifyTrackLanguages(path, loadGeneration, player.Length, subtitles, audio, sidecarTracks, sidecars, fileKey, cancellation);
    }

    private async Task IdentifyTrackLanguages(string path, int generation, long durationMs, Dictionary<int, int> subtitles, List<int> audio,
        List<int> sidecarTracks, string[] sidecars, string fileKey, CancellationTokenSource cancellation)
    {
        var ct = cancellation.Token;
        var owned = pendingLanguages.Where(track => track.Type == TrackType.Audio && audio.Contains(track.Id) ||
            track.Type == TrackType.Text && (subtitles.ContainsKey(track.Id) || sidecarTracks.Contains(track.Id))).ToArray();
        bool Current() => !closing && !ct.IsCancellationRequested && generation == loadGeneration && languageDetectionGeneration == generation;
        // Results of the file's own tracks are cached by the shared work (IdentifyWritten, IdentifySpoken).
        void Resolve(TrackType type, int id, string? language, bool named = false)
        {
            if (!Current()) return;
            pendingLanguages.Remove((type, id)); detectedLanguages[(type, id)] = language;
            if (named && language is not null) namedLanguages.Add((type, id));
            PlaybackDiagnostics.Record("track-language", new { type = type.ToString(), id, language, named });
            RefreshTracks();
        }
        try
        {
            if (sidecarTracks.Count > 0)
            {
                var languages = await Task.Run(() => sidecars.Select(file => SubtitleFileLanguage(file, ct)).ToArray(), ct);
                var shared = languages.Select(found => found.Language).Distinct().ToArray();
                var language = shared.Length == 1 ? shared[0] : null;
                foreach (var id in sidecarTracks) Resolve(TrackType.Text, id, language, languages.All(found => found.Named));
            }
            if (subtitles.Keys.Any(id => pendingLanguages.Contains((TrackType.Text, id))))
                foreach (var (id, language) in await IdentifyWritten(path, fileKey, subtitles).WaitAsync(ct))
                    if (pendingLanguages.Contains((TrackType.Text, id))) Resolve(TrackType.Text, id, language);
            // The track playing now first: it decides subtitle rules.
            foreach (var id in audio.Where(id => pendingLanguages.Contains((TrackType.Audio, id))).OrderBy(id => id == player?.AudioTrack ? 0 : 1).ToArray())
            {
                if (!Current()) return;
                Resolve(TrackType.Audio, id, await IdentifySpoken(path, fileKey, id, durationMs).WaitAsync(ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { PlaybackDiagnostics.Record("track-language-failed", new { error = ex.GetType().Name }); }
        finally
        {
            // Tracks left unanalyzed (cancelled or failed) keep the file's own language and are analyzed again next time.
            // Inconclusive results are cached like any other: the same file gives the same evidence.
            if (Current() && owned.Count(pendingLanguages.Remove) > 0) RefreshTracks();
            if (languageDetectionCancellation == cancellation) languageDetectionCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>A subtitle file's language: what its name says, or else what its text reads as.</summary>
    private static (string? Language, bool Named) SubtitleFileLanguage(string path, CancellationToken ct = default) =>
        SubtitleFiles.Language(path) is { } named ? (named, true) : (LanguageDetection.Written(SubtitleText.FromFile(path, ct)), false);

    /// <summary>Names the subtitle tracks known to come from a file, as they appear. Runs for guests too: the files are local.</summary>
    private void DetectSubtitleFiles()
    {
        if (languageDetectionGeneration != loadGeneration) return;
        foreach (var (id, file) in subtitleFiles)
        {
            var key = (TrackType.Text, id);
            if (file.Generation != loadGeneration || detectedLanguages.ContainsKey(key) || pendingLanguages.Contains(key)) continue;
            if (SubtitleFiles.Language(file.Name) is { } named) { detectedLanguages[key] = named; namedLanguages.Add(key); continue; }
            pendingLanguages.Add(key);
            _ = IdentifySubtitleFile(id, file.Path, loadGeneration);
        }
    }
    private async Task IdentifySubtitleFile(int id, string path, int generation)
    {
        string? language = null;
        try { language = await Task.Run(() => SubtitleFileLanguage(path, lifetime.Token).Language, lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PlaybackDiagnostics.Record("track-language-failed", new { error = ex.GetType().Name }); }
        if (closing || generation != loadGeneration || languageDetectionGeneration != generation || !pendingLanguages.Remove((TrackType.Text, id))) return;
        detectedLanguages[(TrackType.Text, id)] = language;
        PlaybackDiagnostics.Record("track-language", new { type = nameof(TrackType.Text), id, language, file = true });
        RefreshTracks();
    }

    /// <summary>
    /// Remembers which track a subtitle file loaded in local playback became, once VLC adds it. VLC adds files in the
    /// order they were loaded, each as a higher track number, so files still waiting take new tracks first come, first served.
    /// </summary>
    private async Task WatchLoadedSubtitle(string path, HashSet<int> before, int generation)
    {
        var waiting = new object();
        loadingSubtitles.Add(waiting);
        try
        {
            for (var deadline = Environment.TickCount64 + 10000; Environment.TickCount64 < deadline;)
            {
                if (closing || generation != loadGeneration || player is null) return;
                var added = loadingSubtitles[0] != waiting ? null : player.SpuDescription.Where(track => track.Id >= 0 && !before.Contains(track.Id) && !subtitleFiles.ContainsKey(track.Id))
                    .Select(track => (int?)track.Id).Min();
                if (added is { } id)
                {
                    subtitleFiles[id] = new(path, Path.GetFileName(path), generation);
                    RefreshTracks();
                    return;
                }
                await Task.Delay(50);
            }
        }
        finally { loadingSubtitles.Remove(waiting); }
    }
}
