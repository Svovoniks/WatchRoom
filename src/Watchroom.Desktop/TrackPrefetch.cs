using System.IO;
using System.Windows;
using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Untagged tracks of the videos about to be watched are identified ahead of playback: the video whose page is open, and
// the one that plays after the current video. One track at a time, and not while the playing video's own tracks are
// being identified. Playback and this share every track being identified (languageWork), and results go to the cache.
// The video page also lists the file's audio and subtitles, with languages as they're identified.
public partial class MainWindow
{
    /// <summary>An audio or subtitle track as the player numbers it. Untagged: the file doesn't name its language.</summary>
    private sealed record FileTrack(TrackType Type, int Id, string? Language, string? Name, bool Forced, bool Untagged, bool Text, int Number);
    /// <summary>A subtitle file the player loads beside the video, with the language its name or text gives.</summary>
    private sealed record SidecarFile(string Path, string? Language, bool Named);
    /// <summary>Complete is false when the file needed VLC's parser before playback had loaded VLC: its tracks are read again later.</summary>
    private sealed record FileTracks(string Path, string FileKey, IReadOnlyList<FileTrack> Tracks, IReadOnlyList<SidecarFile> Sidecars, bool Complete = true);

    // VLC's parser for containers we don't read ourselves. It starts only once playback has loaded VLC: loading its
    // libraries holds the process loader lock and stalls the UI thread (see StartPlayer). A LibVLC still alive when the
    // process exits crashes it, so it's released at exit, after any parse under way.
    private static LibVLC? trackParser;
    private static bool trackParserClosed;
    private static readonly SemaphoreSlim trackParserGate = new(1, 1);
    private readonly Dictionary<string, Task> languageWork = []; // by cache key: identifications under way, shared with playback
    private readonly HashSet<string> languageWorkFailed = []; // failed this session; playback still retries them
    private readonly Dictionary<string, Task<FileTracks?>> fileTracks = []; // by file version
    private string? prefetchPage, prefetchNext;
    private bool prefetching;
    private int detailTracksVersion;

    /// <summary>Identifies one untagged audio track, or joins the identification already under way.</summary>
    private Task<string?> IdentifySpoken(string path, string fileKey, int id, long durationMs) =>
        SharedLanguageWork(LibraryStore.TrackLanguageKey(fileKey, true, id), async () =>
        {
            var language = await LanguageDetection.SpokenAsync(path, id, durationMs, lifetime.Token);
            library.CacheTrackLanguage(path, fileKey, true, id, language);
            return language;
        });

    /// <summary>Identifies a file's untagged text subtitles (player track id → container track number) from one read of the file.</summary>
    private Task<Dictionary<int, string?>> IdentifyWritten(string path, string fileKey, IReadOnlyDictionary<int, int> tracks) =>
        SharedLanguageWork(fileKey + ":text:" + string.Join(",", tracks.Keys.Order()), async () =>
        {
            var token = lifetime.Token;
            var languages = await Task.Run(() =>
            {
                var text = SubtitleText.Sample(path, tracks.Values.ToArray(), token);
                return tracks.ToDictionary(pair => pair.Key, pair => LanguageDetection.Written(text.GetValueOrDefault(pair.Value)));
            }, token);
            foreach (var (id, language) in languages) library.CacheTrackLanguage(path, fileKey, false, id, language);
            return languages;
        });

    private Task<T> SharedLanguageWork<T>(string key, Func<Task<T>> work)
    {
        if (languageWork.TryGetValue(key, out var running)) return (Task<T>)running;
        var task = Run();
        languageWork[key] = task;
        return task;
        async Task<T> Run()
        {
            await Task.Yield(); // registered before the work starts
            try { return await work(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { languageWorkFailed.Add(key); throw; }
            finally { languageWork.Remove(key); RefreshDetailTracks(); }
        }
    }

    /// <summary>The file's tracks, read once per version of the file: Matroska and MP4 headers, or else VLC's parser.</summary>
    private Task<FileTracks?> ReadFileTracks(string path)
    {
        if (LibraryStore.TrackLanguageFile(path) is not { } fileKey) return Task.FromResult<FileTracks?>(null);
        var parse = vlc is not null;
        if (fileTracks.TryGetValue(fileKey, out var known) && !known.IsFaulted && !(parse && known.IsCompletedSuccessfully && known.Result?.Complete == false)) return known;
        if (fileTracks.Count >= 64) fileTracks.Clear();
        var token = lifetime.Token;
        return fileTracks[fileKey] = Task.Run(async () =>
        {
            var container = SubtitleText.Tracks(path, token);
            var tracks = container.Count > 0
                // VLC numbers Matroska and MP4 tracks by their position in the file.
                ? container.Select((track, id) => (Track: track, Id: id)).Where(entry => entry.Track.Type is ContainerTrackType.Audio or ContainerTrackType.Subtitle)
                    .Select(entry => new FileTrack(entry.Track.Type == ContainerTrackType.Audio ? TrackType.Audio : TrackType.Text, entry.Id,
                        TrackLanguages.Normalize(entry.Track.Language), entry.Track.Name, entry.Track.Forced, entry.Track.Untagged, entry.Track.Text, entry.Track.Number)).ToList()
                : parse ? await ParseTracks(path, token) : null;
            var sidecars = SubtitleFiles.VlcSidecars(path).Select(file =>
            {
                var (language, named) = SubtitleFileLanguage(file, token);
                return new SidecarFile(file, language, named);
            }).ToList();
            return (FileTracks?)new FileTracks(path, fileKey, tracks ?? [], sidecars, tracks is not null);
        }, token);
    }
    private static async Task<List<FileTrack>> ParseTracks(string path, CancellationToken ct)
    {
        await trackParserGate.WaitAsync(ct);
        try
        {
            if (trackParserClosed) return [];
            if (trackParser is null)
            {
                LibVLCSharp.Shared.Core.Initialize();
                trackParser = new LibVLC("--quiet", "--no-video", "--no-sub-autodetect-file", "--no-stats", "--no-osd");
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    if (!trackParserGate.Wait(5000)) return;
                    trackParserClosed = true; trackParser?.Dispose(); trackParser = null;
                };
            }
            using var media = new Media(trackParser, path, FromType.FromPath);
            var status = await media.Parse(MediaParseOptions.ParseLocal, 10000, ct);
            // Parse completes on VLC's event thread; reading and releasing the media there crashes it.
            await Task.Yield();
            if (status != MediaParsedStatus.Done) return [];
            return media.Tracks.Where(track => track.TrackType is TrackType.Audio or TrackType.Text && track.Id >= 0).Select(track =>
            {
                var language = TrackLanguages.Normalize(track.Language);
                return new FileTrack(track.TrackType, track.Id, language, string.IsNullOrWhiteSpace(track.Description) ? null : track.Description.Trim(),
                    false, language is null, false, -1);
            }).ToList();
        }
        catch (Exception ex) when (ex is VLCException or DllNotFoundException or BadImageFormatException) { return []; }
        finally { trackParserGate.Release(); }
    }

    /// <summary>Identifies a video's untagged tracks ahead of playback, as the user looks at its page.</summary>
    private void PrefetchPageLanguages(MediaItem item)
    {
        prefetchPage = item.Available && File.Exists(item.Path) ? item.Path : null;
        StartPrefetch();
    }
    /// <summary>Identifies the untagged tracks of the video that plays after this one: the next in the queue, or the next episode.</summary>
    private void PrefetchNextLanguages()
    {
        prefetchNext = null;
        if (playingItem is { } item && (room is null || room.Identity?.Host == true))
        {
            var index = PlayingFromQueue(item) ? QueuePlayback.FindNext(queue, queuePosition, 1, QueueItemAvailable) : -1;
            var next = index >= 0 ? queue[index] : EpisodePlayback.Next(items, items.FirstOrDefault(entry => entry.Id == item.Id) ?? item, false,
                entry => File.Exists(entry.Path), PreferredVersion(item));
            if (next is not null && next.Path != item.Path && File.Exists(next.Path)) prefetchNext = next.Path;
        }
        StartPrefetch();
    }

    private async void StartPrefetch()
    {
        if (prefetching || closing) return;
        prefetching = true;
        try
        {
            while (!closing)
            {
                // The playing video's own tracks come first.
                if (TrackLanguagesPending()) { await Task.Delay(500, lifetime.Token); continue; }
                var path = prefetchPage ?? prefetchNext;
                if (path is null) return;
                var done = true;
                try
                {
                    // One step at a time, so a newly opened page or a playing video takes over between tracks.
                    if (await ReadFileTracks(path) is { } tracks && NextPrefetchStep(tracks) is { } step && !closing) { done = false; await step(); } // a failed track is skipped next time
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) { PlaybackDiagnostics.Record("track-language-prefetch-failed", new { error = ex.GetType().Name }); }
                if (!done) continue;
                // Finished, or the file can't be read: playback still identifies whatever is left.
                if (prefetchPage == path) prefetchPage = null;
                if (prefetchNext == path) prefetchNext = null;
                RefreshDetailTracks();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { prefetching = false; }
    }

    /// <summary>The next piece of work for a file: its subtitle text, then each untagged audio track. Null when all are known.</summary>
    private Func<Task>? NextPrefetchStep(FileTracks file)
    {
        bool Unknown(TrackType type, int id)
        {
            var key = LibraryStore.TrackLanguageKey(file.FileKey, type == TrackType.Audio, id);
            return !languageWorkFailed.Contains(key) && library.Setting(key) is null;
        }
        var text = file.Tracks.Where(track => track.Type == TrackType.Text && track.Untagged && track.Text).ToDictionary(track => track.Id, track => track.Number);
        if (text.Keys.Any(id => Unknown(TrackType.Text, id)) && !languageWorkFailed.Contains(file.FileKey + ":text:" + string.Join(",", text.Keys.Order())))
            return () => IdentifyWritten(file.Path, file.FileKey, text);
        if (!LanguageDetection.SpeechAvailable) return null;
        var audio = file.Tracks.FirstOrDefault(track => track.Type == TrackType.Audio && track.Untagged && Unknown(TrackType.Audio, track.Id));
        return audio is null ? null : () => IdentifySpoken(file.Path, file.FileKey, audio.Id, 0);
    }

    /// <summary>Lists the selected video's audio and subtitles on its page, and starts identifying the untagged ones.</summary>
    private void ShowDetailTracks(MediaItem item)
    {
        DetailTracks.Visibility = Visibility.Collapsed;
        detailTracksVersion++;
        if (!item.Available || !File.Exists(item.Path)) return;
        PrefetchPageLanguages(item);
        RefreshDetailTracks();
    }
    private async void RefreshDetailTracks()
    {
        if (closing || selected is not { } item || DetailFile.Text != item.Path || !item.Available) return;
        var version = ++detailTracksVersion;
        FileTracks? file;
        try { file = await ReadFileTracks(item.Path); }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) PlaybackDiagnostics.Record("track-list-failed", new { error = ex.GetType().Name });
            return;
        }
        if (closing || version != detailTracksVersion || selected?.Path != item.Path) return;
        if (file is null || !file.Complete || file.Tracks.Count == 0 && file.Sidecars.Count == 0) { DetailTracks.Visibility = Visibility.Collapsed; return; }
        var identifying = prefetchPage == item.Path || prefetchNext == item.Path || LocalMediaPath() == item.Path && TrackLanguagesPending();
        string Describe(FileTrack track)
        {
            var language = track.Language; var detected = false; var pending = false;
            if (track.Untagged)
            {
                var key = LibraryStore.TrackLanguageKey(file.FileKey, track.Type == TrackType.Audio, track.Id);
                var cached = library.Setting(key);
                if (!string.IsNullOrEmpty(cached)) { language = cached; detected = true; }
                pending = cached is null && (languageWork.ContainsKey(key) || identifying && !languageWorkFailed.Contains(key) &&
                    (track.Type == TrackType.Audio ? LanguageDetection.SpeechAvailable : track.Text));
            }
            var name = language is null ? "Unknown language" : LanguageName(language);
            if (track.Name is { } title && !string.Equals(title, name, StringComparison.OrdinalIgnoreCase)) name += " – " + title;
            if (track.Forced) name += " (forced)";
            return name + (detected ? " (detected)" : pending ? " · identifying…" : "");
        }
        string DescribeFile(SidecarFile sidecar) =>
            (sidecar.Language is null ? "Unknown language" : LanguageName(sidecar.Language)) + (sidecar.Language is not null && !sidecar.Named ? " (detected, file)" : " (file)");
        var audio = file.Tracks.Where(track => track.Type == TrackType.Audio).Select(Describe).ToArray();
        var subtitles = file.Tracks.Where(track => track.Type == TrackType.Text).Select(Describe).Concat(file.Sidecars.Select(DescribeFile)).ToArray();
        DetailAudio.Text = audio.Length == 0 ? "None" : string.Join(", ", audio);
        DetailSubtitles.Text = subtitles.Length == 0 ? "None" : string.Join(", ", subtitles);
        DetailTracks.Visibility = Visibility.Visible;
    }
}
