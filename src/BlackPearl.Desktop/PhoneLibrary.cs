using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// What BlackPearl Mobile does with this PC's library beyond the remote (PHONE-REMOTE.md): it browses the library, plays a
// video on the phone, streamed over the phone's own link, reports how far it got so Continue watching and the watched
// marks stay the same on both, and hands the video back to this PC at the same moment.
//
// The app's window serves this while it is open (PhonePlayback.cs), and the background phone host while the app is
// closed (PhoneHost.cs). Neither needs the other: whatever only the window can do comes in through the hooks below.
// Requests run on the owner's dispatcher thread; media reads on others.
internal sealed class PhoneLibrary(LibraryStore library, Func<PhoneRemoteHost?> current, Action<Action> post, CancellationToken lifetime)
{
    public const int PageSize = 40;
    public static readonly string[] Features = ["library", "watch", "queues"];
    public static string StreamCacheDirectory => Path.Combine(Path.GetTempPath(), "BlackPearl", "stream-cache");

    // The library's videos, read on the dispatcher thread.
    public required Func<IReadOnlyList<MediaItem>> Items { get; init; }
    public required Func<string, bool> WasWatched { get; init; }
    public required Action<MediaItem, bool> SetWatched { get; init; }
    // Continue on the PC: plays the video there from where the phone was. False when it was refused (and answered).
    public required Func<JsonElement, Action<string, string>, Task<bool>> Play { get; init; }
    // The window only: stops its own playback of the video the phone takes over, and says where it was. Null when the
    // PC isn't playing it.
    public Func<MediaItem, Task<long?>>? TakeOver { get; init; }
    // The window only: true while it plays this video itself, which then keeps its own position.
    public Func<string, bool>? PlaysHere { get; init; }
    // The window only: shows progress the phone saved. The flag is true once the phone stopped or paused.
    public Action<MediaItem, bool>? ProgressSaved { get; init; }

    // One video a phone is watching: its file, or the conversion of it the phone asked for.
    private sealed class PhoneStream(MediaItem item, FileMediaSource file) : IDisposable
    {
        public MediaItem Item { get; } = item;
        public FileMediaSource File { get; } = file;
        public StreamConverter? Converter { get; init; }
        public SegmentStore? Store { get; init; }
        public string? Directory { get; init; }
        public void Dispose() => CloseConversion(Converter, Store, Directory);
    }
    // By phone id: a phone whose link drops and comes back keeps reading the same stream.
    private readonly ConcurrentDictionary<string, PhoneStream> streams = new();
    // Picture tokens handed out in library pages, and the files they stand for. Phones never see the paths.
    private readonly ConcurrentDictionary<string, string> artwork = new();
    private readonly ConcurrentDictionary<string, byte[]> artworkCache = new();

    public void Attach(PhoneRemoteHost remote)
    {
        remote.Request += (session, type, message) => post(() => { if (current() == remote) _ = Run(remote, session, type, message); });
        remote.Disconnected += session => ExpireStream(session.PhoneId);
        remote.ResolveMedia = Resolve;
    }

    // Whether a phone is watching a video now.
    public bool Streaming => !streams.IsEmpty;

    public static void CloseConversion(StreamConverter? converter, SegmentStore? store, string? directory)
    {
        if (converter is null && store is null && directory is null) return;
        converter?.Dispose();
        // The converted video and its subtitle files can be gigabytes: they go on another thread.
        _ = Task.Run(() =>
        {
            store?.Dispose();
            try { if (directory is not null && System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    // A phone that loses its link usually reconnects within moments, mid-video, so its stream waits two minutes for it.
    // Each drop starts the wait over: an earlier drop's wait ends without effect once the phone came back in between. A
    // wait ends only the stream it began with, so one the phone started since, on this link or a restarted one, stays.
    private readonly ConcurrentDictionary<string, int> drops = new();
    private void ExpireStream(string phoneId)
    {
        var drop = drops.AddOrUpdate(phoneId, 1, (_, count) => count + 1);
        if (!streams.TryGetValue(phoneId, out var waiting)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(StreamGrace);
            if (drops.GetValueOrDefault(phoneId) != drop || current() is not { } remote || remote.Connected.Any(s => s.PhoneId == phoneId)) return;
            if (streams.TryRemove(KeyValuePair.Create(phoneId, waiting))) waiting.Dispose();
        });
    }
    private static readonly TimeSpan StreamGrace = TimeSpan.FromMinutes(2);

    public void CloseStreams()
    {
        foreach (var phone in streams.Keys) if (streams.TryRemove(phone, out var stream)) stream.Dispose();
        artwork.Clear(); artworkCache.Clear();
    }

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long? Number(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : null;
    private static bool Flag(JsonElement message, string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // A phone's request never opens a dialog on the PC: whatever goes wrong is answered to the phone, which shows it.
    private async Task Run(PhoneRemoteHost remote, PhoneRemoteSession session, string type, JsonElement message)
    {
        var request = Number(message, "request") ?? 0;
        void Fail(string code, string text) => remote.SendMessage(session, new { type = "error", code, message = text, request });
        try
        {
            switch (type)
            {
                case "browse" when Text(message, "queue") is not null || Text(message, "episodesOf") is not null: await BrowseList(remote, session, request, message); break;
                case "browse": await Browse(remote, session, request, message); break;
                case "queues": await Queues(remote, session, request); break;
                case "watch": await Watch(remote, session, request, message, Fail); break;
                case "progress": SaveProgress(message); break;
                case "play":
                    if (!await Play(message, Fail)) break;
                    // The phone hands its video to the PC and stops watching. Its stream ends first: a phone still
                    // reading video gets answers on its link only once the video ahead of them has gone, which on a
                    // slow phone took longer than it waited for this one.
                    if (streams.TryRemove(session.PhoneId, out var handed)) handed.Dispose();
                    remote.SendMessage(session, new { type = "done", request });
                    break;
                case "stream-stop": if (streams.TryRemove(session.PhoneId, out var stream)) stream.Dispose(); break;
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested)
        {
            PlaybackDiagnostics.Record("phone-request-failed", new { type, error = ex.GetType().Name });
            try
            {
                // Not the exception's own message: those can name the file's path, which phones never see.
                Fail("failed", ex is IOException or UnauthorizedAccessException
                    ? "The PC couldn't open that video. Its drive may be disconnected." : "The PC couldn't do that. Try again.");
            }
            catch (Exception) { }
        }
    }

    private async Task Browse(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message)
    {
        var browse = new LibraryBrowseRequest(Text(message, "query") ?? "", (int)(Number(message, "category") ?? 0), Text(message, "parentId"),
            Number(message, "season") is { } season ? (int)season : null, (int)(Number(message, "page") ?? 0), Flag(message, "home"), (int)(Number(message, "seed") ?? 0));
        var source = Items().ToArray();
        Dictionary<string, string> playback;
        LibraryBrowsePage page;
        try
        {
            (playback, page) = await Task.Run(() =>
            {
                var settings = library.PlaybackSettings();
                return (settings, SharedLibrary.Browse(source, browse, ArtworkToken, settings, PageSize));
            });
        }
        catch (InvalidDataException ex)
        {
            remote.SendMessage(session, new { type = "error", code = "browse", message = ex.Message, request });
            return;
        }
        if (current() != remote) return;
        bool Watched(string id) => playback.GetValueOrDefault("watched:" + id) == "true";
        long Position(string id) => long.TryParse(playback.GetValueOrDefault("position:" + id), out var at) ? Math.Max(0, at) : 0;
        var byId = source.ToDictionary(x => x.Id);
        var entries = page.Entries.Select(entry =>
        {
            var file = entry.Level is "movie" or "episode" || entry.HomeSection == "continue";
            // A show's or season's card stands on one of its episodes; it describes the show, not that episode.
            var overview = entry.Level is "series" or "season" && byId.GetValueOrDefault(entry.Id)?.Overview is { Length: > 0 } show ? show[..Math.Min(show.Length, 400)] : entry.Overview;
            return new Entry(entry.Id, entry.Title, entry.Caption, entry.Level, entry.Season, entry.Episode, overview, entry.Kind,
                entry.RuntimeMinutes, entry.Year, entry.Episodes, entry.HomeSection, file ? Math.Max(entry.PositionMs, Position(entry.Id)) : 0,
                file && Watched(entry.Id), entry.Thumbnail, file);
        }).ToArray();
        // Each message is at most 64 KiB: summaries shorten first, then the page drops entries from its end.
        for (var limit = 400; ; limit /= 2)
        {
            var reply = new { type = "page", request, entries, total = page.Total, page = page.Page, pageSize = PageSize, location = page.Location };
            if (Encoding.UTF8.GetByteCount(Wire.Serialize(reply)) < 60000) { remote.SendMessage(session, reply); return; }
            entries = limit > 0 ? entries.Select(entry => entry with { Overview = entry.Overview[..Math.Min(entry.Overview.Length, limit)] }).ToArray() : entries[..^1];
        }
    }

    private record Entry(string Id, string Title, string Caption, string Level, int? Season, int? Episode, string Overview, string Kind,
        int? RuntimeMinutes, int? Year, int Episodes, string? HomeSection, long PositionMs, bool Watched, string? Artwork, bool Playable, int? Index = null);

    // The library page names each picture by a token rather than its path.
    private string? ArtworkToken(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24].ToLowerInvariant();
        if (artwork.Count > 20000) artwork.Clear();
        artwork[token] = path;
        return token;
    }

    // Reads for the phone's media channel, on threads other than the dispatcher's: "file/<id>" for the video it is
    // watching, "stream/<stream id>/<piece>" for its conversion, and "art/<token>/<width>" for pictures.
    private async Task<PhoneResource?> Resolve(PhoneRemoteSession session, string resource, CancellationToken ct)
    {
        var parts = resource.Split('/');
        switch (parts)
        {
            case ["file", var id] when streams.TryGetValue(session.PhoneId, out var stream) && stream.Item.Id == id:
                return new(stream.File.Media.Length, stream.File.ReadAsync);
            case ["stream", var id, var piece] when streams.TryGetValue(session.PhoneId, out var stream) && stream.Converter is { } converter && converter.Stream.Id == id &&
                int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < converter.Stream.Segments:
                return await converter.PhonePieceAsync(index, ct);
            case ["art", var token, var size] when artwork.TryGetValue(token, out var path) && int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var width):
                width = Math.Clamp(width, 64, 1280);
                var key = token + "/" + width;
                if (artworkCache.TryGetValue(key, out var cached)) return PhoneResource.FromBytes(cached);
                var bytes = await Task.Run(() => Picture(path, width), ct);
                if (bytes is null) return null;
                if (artworkCache.Count > 300) artworkCache.Clear();
                artworkCache[key] = bytes;
                return PhoneResource.FromBytes(bytes);
            default: return null;
        }
    }

    // The picture at most width pixels wide, as JPEG, never enlarged.
    private static byte[]? Picture(string path, int width)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            file.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
            bitmap.DecodePixelWidth = Math.Min(width, frame.PixelWidth); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze();
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception) { return null; }
    }

    // The phone asks to play a video. takeOver: the video is the one playing here, and the phone continues it, so this
    // PC saves where it was and stops. The phone hears back where to start and how to read the video: the file itself,
    // or a conversion of it at a lower bitrate for a slow link.
    private async Task Watch(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message, Action<string, string> fail)
    {
        var id = Text(message, "id");
        var item = Items().FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return; }
        var wanted = Text(message, "quality") ?? "auto";
        var home = Text(message, "network") == "home";
        // At home the file plays as it is, and the probe only lists the qualities the file allows; it starts now, so a
        // video taken over from the PC isn't left stopped while it runs.
        var ffmpeg = item.IsMusic ? null : Ffmpeg.Find();
        if (ffmpeg is not null && (wanted == StreamQuality.OriginalId || wanted == "auto" && home)) _ = Probe(ffmpeg, item, TimeSpan.Zero);
        long? takenAt = Flag(message, "takeOver") && TakeOver is { } takeOver ? await takeOver(item) : null;
        if (streams.TryRemove(session.PhoneId, out var previous)) previous.Dispose();
        var resume = takenAt ?? (!WasWatched(item.Id) && !item.IsMusic && long.TryParse(library.Setting("position:" + item.Id), out var saved) ? saved : 0);
        var file = new FileMediaSource(item.Path, item.QueueTitle, item.Id);
        var qualities = new List<string> { StreamQuality.OriginalId };
        PhoneStream stream = new(item, file);
        string? note = null;
        long durationMs = (long)((item.DurationSeconds ?? 0) * 1000);
        if (wanted != StreamQuality.OriginalId && !(wanted == "auto" && home) && !item.IsMusic)
        {
            var (converted, why) = await ConvertVideo(item, file, wanted == "auto" ? "720p" : wanted, qualities);
            if (current() != remote) { converted?.Dispose(); return; }
            if (converted is not null) { stream = converted; durationMs = converted.Converter!.Stream.DurationMs; }
            else note = why;
        }
        else if (ffmpeg is not null && await Probe(ffmpeg, item, TimeSpan.FromSeconds(2)) is { } probe)
            qualities.AddRange(StreamQuality.For(probe.ClassHeight).Select(q => q.Id));
        // Where the phone goes from here, worked out before the stream is registered, so the answer follows at once.
        var (source, version) = (Items().ToArray(), LibraryIdentity.TitleKey(item) is { } show ? library.Setting("version:" + show) : null);
        var (inQueue, place) = QueuePlace(Text(message, "queue"), Number(message, "queueIndex"), item.Id);
        var (back, forward, start) = await Task.Run(() => inQueue is null ? EpisodeSteps(source, item, version) : QueueSteps(source, inQueue, place));
        var tracks = TrackPreferences();
        if (current() != remote) { stream.Dispose(); return; }
        if (streams.TryRemove(session.PhoneId, out var raced)) raced.Dispose();
        streams[session.PhoneId] = stream;
        // The phone left while the video was being prepared: the stream waits for it as for any dropped link.
        if (remote.Connected.All(s => s.PhoneId != session.PhoneId)) ExpireStream(session.PhoneId);
        var title = item.QueueTitle;
        var detail = StreamDetail(item);
        var queue = inQueue is null ? null : new { id = inQueue.Id, name = inQueue.Name, index = place };
        remote.SendMessage(session, stream.Converter is { } converter
            ? new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "hls", streamId = converter.Stream.Id,
                segments = converter.Stream.Segments, segmentMs = converter.Stream.SegmentMs, quality = converter.Stream.Quality, qualities, note,
                previous = back, next = forward, first = start, tracks, queue }
            : (object)new { type = "stream", request, id = item.Id, title, detail, positionMs = resume, durationMs, kind = "file", resource = "file/" + item.Id,
                length = file.Media.Length, extension = file.Media.Extension, quality = StreamQuality.OriginalId, qualities, note,
                previous = back, next = forward, first = start, tracks, queue });
    }

    // The saved queue a phone plays a video from, and the video's place in it: the place it named while that still holds
    // the video, otherwise the video's first place. None when the queue is gone or no longer has the video.
    public (SavedQueue? Queue, int Place) QueuePlace(JsonElement message, string mediaId) => QueuePlace(Text(message, "queue"), Number(message, "queueIndex"), mediaId);
    public (SavedQueue? Queue, int Place) QueuePlace(string? queueId, long? named, string mediaId)
    {
        if (queueId is null || PhoneQueues.Read(library.Setting("queues")).FirstOrDefault(q => q.Id == queueId) is not { } saved) return (null, -1);
        if (named is { } at && at is >= 0 and < int.MaxValue && PhoneQueues.Holds(saved, (int)at, mediaId)) return (saved, (int)at);
        var first = Array.IndexOf(saved.MediaIds, mediaId);
        return first < 0 ? (null, -1) : (saved, first);
    }

    // Previous, Next and Loop all's start within a queue, as the PC's player steps through it.
    private static (EpisodeStep? Previous, EpisodeStep? Next, EpisodeStep? First) QueueSteps(MediaItem[] source, SavedQueue saved, int place)
    {
        var byId = ById(source);
        var (previous, next, first) = PhoneQueues.Steps(saved, byId, place, item => File.Exists(item.Path));
        EpisodeStep? Step(int? index) => index is { } at && byId.GetValueOrDefault(saved.MediaIds[at]) is { } item
            ? new(item.Id, item.QueueTitle, StreamDetail(item), at) : null;
        return (Step(previous), Step(next), Step(first));
    }

    private static Dictionary<string, MediaItem> ById(IEnumerable<MediaItem> source)
    {
        var result = new Dictionary<string, MediaItem>();
        foreach (var item in source) result.TryAdd(item.Id, item);
        return result;
    }

    // A video's picture in a list: an episode's still, or its season's or show's poster; a film's poster.
    private static string? Picture(MediaItem item) => item.Series is null ? item.Poster
        : LibraryCatalog.EpisodeArtwork(item) ?? item.SeasonPoster ?? item.SeriesPoster ?? item.Poster;

    // The PC's saved queues, for the phone's Library: each with its count, a picture, and where Play continues, as on the
    // PC's Queues page. Active is the one the PC's player uses.
    private async Task Queues(PhoneRemoteHost remote, PhoneRemoteSession session, long request)
    {
        var source = Items().ToArray();
        var saved = PhoneQueues.Read(library.Setting("queues"));
        var activeId = library.Setting("activeQueue");
        var position = int.TryParse(library.Setting("queuePosition"), out var at) ? at : -1;
        var queues = await Task.Run(() =>
        {
            var playback = library.PlaybackSettings();
            var byId = ById(source);
            bool Watched(string id) => playback.GetValueOrDefault("watched:" + id) == "true";
            return saved.Select(queue =>
            {
                var active = queue.Id == activeId ? PhoneQueues.SavedIndex(queue, byId, position) : -1;
                var upNext = PhoneQueues.UpNext(queue, byId, active, Watched);
                var entries = PhoneQueues.Entries(queue, byId);
                var cover = upNext >= 0 ? byId[queue.MediaIds[upNext]] : entries.Select(e => e.Item).FirstOrDefault();
                return new QueueCard(queue.Id, queue.Name.Length > 120 ? queue.Name[..120] : queue.Name, entries.Length, upNext >= 0 ? upNext : null,
                    queue.Id == activeId, cover is null ? null : ArtworkToken(Picture(cover)));
            }).ToArray();
        });
        if (current() != remote) return;
        // Each message is at most 64 KiB; past that, the last queues are left out.
        while (queues.Length > 0 && Encoding.UTF8.GetByteCount(Wire.Serialize(new { type = "queues", request, queues })) >= 60000) queues = queues[..^1];
        remote.SendMessage(session, new { type = "queues", request, queues });
    }

    private sealed record QueueCard(string Id, string Name, int Count, int? UpNext, bool Active, string? Artwork);

    // A queue's videos in its order (queue), or the show of a video's episodes (episodesOf), as Library entries that also
    // carry their place, for watch and play to take back.
    private async Task BrowseList(PhoneRemoteHost remote, PhoneRemoteSession session, long request, JsonElement message)
    {
        var source = Items().ToArray();
        var queueId = Text(message, "queue");
        var saved = queueId is null ? null : PhoneQueues.Read(library.Setting("queues")).FirstOrDefault(q => q.Id == queueId);
        var anchor = Text(message, "episodesOf") is { } of ? source.FirstOrDefault(x => x.Id == of) : null;
        if (saved is null && anchor is null)
        {
            remote.SendMessage(session, new { type = "error", code = "browse", request,
                message = queueId is not null ? "This queue is no longer on the PC." : "This video is no longer in the PC's library." });
            return;
        }
        var version = anchor is not null && LibraryIdentity.TitleKey(anchor) is { } show ? library.Setting("version:" + show) : null;
        var requested = (int)Math.Clamp(Number(message, "page") ?? 0, 0, 100000);
        var (entries, total, page) = await Task.Run(() =>
        {
            var playback = library.PlaybackSettings();
            var listed = saved is not null ? PhoneQueues.Entries(saved, ById(source))
                : EpisodePlayback.Episodes(source, anchor!, version).Select((item, index) => (index, item)).ToArray();
            var page = Math.Min(requested, Math.Max(0, (listed.Length - 1) / PageSize));
            var entries = listed.Skip(page * PageSize).Take(PageSize).Select(e => ListEntry(e.Item2, e.Item1, playback)).ToArray();
            return (entries, listed.Length, page);
        });
        if (current() != remote) return;
        var location = saved?.Name ?? anchor!.Series ?? anchor.DisplayTitle;
        for (var limit = 300; ; limit /= 2)
        {
            var reply = new { type = "page", request, entries, total, page, pageSize = PageSize, location };
            if (Encoding.UTF8.GetByteCount(Wire.Serialize(reply)) < 60000) { remote.SendMessage(session, reply); return; }
            entries = limit > 0 ? entries.Select(entry => entry with { Overview = entry.Overview[..Math.Min(entry.Overview.Length, limit)] }).ToArray() : entries[..^1];
        }
    }

    private Entry ListEntry(MediaItem item, int index, IReadOnlyDictionary<string, string> playback)
    {
        var watched = playback.GetValueOrDefault("watched:" + item.Id) == "true";
        var position = !watched && long.TryParse(playback.GetValueOrDefault("position:" + item.Id), out var at) ? Math.Max(0, at) : 0;
        var overview = item.DetailOverview ?? "";
        return new Entry(item.Id, item.QueueTitle, StreamDetail(item) ?? "", item.Series is null ? "movie" : "episode", item.Season, item.Episode,
            overview[..Math.Min(overview.Length, 300)], item.Kind, item.RuntimeMinutes, item.Year, 0, null, position, watched,
            ArtworkToken(Picture(item)), item.Available && !item.IsVirtual && File.Exists(item.Path), index);
    }

    private static string? StreamDetail(MediaItem item) => item.Series is not null
        ? string.Join(" · ", new[] { item.Season is { } season ? $"Season {season}" : null, item.EpisodeDisplayTitle }.OfType<string>())
        : item.Year?.ToString(CultureInfo.InvariantCulture);

    // QueueIndex: the step's place in the queue the phone plays from.
    private sealed record EpisodeStep(string Id, string Title, string? Detail, int? QueueIndex = null);

    // The episodes the phone's Previous and Next play, chosen as the PC's own buttons and up-next countdown choose them,
    // and the show's first regular episode, where Loop all starts again after the last one. Null for a film, or at either
    // end. Off the dispatcher thread: checking a file can wake a sleeping drive.
    private static (EpisodeStep? Previous, EpisodeStep? Next, EpisodeStep? First) EpisodeSteps(MediaItem[] source, MediaItem item, string? version)
    {
        if (item.Series is null || item.Season is null || item.Episode is null || item.IsMusic) return (null, null, null);
        static EpisodeStep Step(MediaItem episode) => new(episode.Id, episode.QueueTitle, StreamDetail(episode));
        bool Plays(MediaItem episode) => episode.Id == item.Id || File.Exists(episode.Path);
        var episodes = EpisodePlayback.Episodes(source, item, version);
        var at = Array.FindIndex(episodes, episode => episode.Id == item.Id);
        MediaItem? previous = null;
        for (var index = at - 1; at > 0 && index >= 0 && previous is null; index--)
            if (episodes[index].Available && File.Exists(episodes[index].Path)) previous = episodes[index];
        var next = EpisodePlayback.Next(source, item, false, episode => File.Exists(episode.Path), version);
        // As the window's Loop all: the first regular episode that plays, specials only when there is nothing else.
        var first = next is null ? episodes.FirstOrDefault(episode => episode.Season != 0 && Plays(episode)) ?? episodes.FirstOrDefault(Plays) : null;
        if (first?.Id == item.Id) first = null;
        return (previous is null ? null : Step(previous), next is null ? null : Step(next), first is null ? null : Step(first));
    }

    // Settings › Playback, as the window reads it: audio and subtitle language priorities, and subtitle rules by audio
    // language. The phone picks its starting tracks with them as the window would.
    private object TrackPreferences()
    {
        string[] audio = [], subtitles = [];
        SubtitleLanguageRule[] rules = [];
        try { audio = TrackLanguages.Parse(library.Setting("audioLanguages") ?? ""); } catch (ArgumentException) { }
        try { subtitles = TrackLanguages.Parse(library.Setting("subtitleLanguages") ?? "", true); } catch (ArgumentException) { }
        try
        {
            var saved = Wire.Read<SubtitleLanguageRule[]>(library.Setting("subtitleLanguageRules") ?? "[]") ?? [];
            if (saved.All(rule => rule?.AudioLanguage is not null && rule.Languages is not null))
                rules = TrackLanguages.ParseSubtitleRules(saved.Select(rule => new KeyValuePair<string, string>(rule.AudioLanguage, string.Join(", ", rule.Languages))));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { }
        return new { audio, subtitles, rules = rules.Select(rule => new { audio = rule.AudioLanguage, languages = rule.Languages }) };
    }

    // A conversion of the video at the quality asked for, or why there is none: no converter, a file that can't be read,
    // or a file already smaller than the conversion would be.
    private async Task<(PhoneStream? Stream, string? Why)> ConvertVideo(MediaItem item, FileMediaSource file, string qualityId, List<string> qualities)
    {
        if (Ffmpeg.Find() is not { } ffmpeg) return (null, "This copy of BlackPearl has no converter, so the phone plays the file as it is.");
        var probe = await Probe(ffmpeg, item, null);
        if (probe is null) return (null, "This file can't be converted, so the phone plays it as it is.");
        qualities.AddRange(StreamQuality.For(probe.ClassHeight).Select(q => q.Id));
        var quality = StreamQuality.Find(qualityId) is { } found && qualities.Contains(found.Id) ? found : StreamQuality.For(probe.ClassHeight).Last();
        var plan = StreamQuality.Plan(probe, file.Media.Length, quality, false);
        if (!plan.Smaller) return (null, $"This file is already smaller than {quality.Name} would be, so the phone plays it as it is.");
        var directory = Path.Combine(StreamCacheDirectory, "phone-" + Guid.NewGuid().ToString("N"));
        var segments = (int)((probe.DurationMs + StreamConverter.SegmentMs - 1) / StreamConverter.SegmentMs);
        var store = SegmentStore.TryCreate(directory, segments);
        if (store is null) return (null, "There is no room on the PC's disk to convert the video, so the phone plays the file as it is.");
        var stream = new RoomStream(Guid.NewGuid().ToString("N"), item.Id, plan.Quality.Id, plan.Height, plan.VideoKbps, probe.DurationMs, StreamConverter.SegmentMs, segments);
        var converter = new StreamConverter(ffmpeg, new StreamInput(item.Path, probe, null, null), stream, store);
        PlaybackDiagnostics.Record("phone-stream", new { quality = plan.Quality.Id, height = plan.Height, kbps = plan.VideoKbps, segments });
        return (new PhoneStream(item, file) { Converter = converter, Store = store, Directory = directory }, null);
    }

    // The video's description, or null when the converter can't read it: it failed, took longer than its own 30 seconds
    // (a sleeping drive, or security software holding ffmpeg), or longer than wait. Read once per file.
    private readonly ConcurrentDictionary<string, Task<VideoProbe?>> probes = new();
    private async Task<VideoProbe?> Probe(string ffmpeg, MediaItem item, TimeSpan? wait)
    {
        var key = item.Path + "|" + (item.FileSize ?? 0);
        if (probes.Count > 200) probes.Clear();
        var probing = probes.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try { return await VideoProbe.ReadAsync(ffmpeg, item.Path, lifetime); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
            {
                PlaybackDiagnostics.Record("phone-stream", new { state = "probe failed", error = ex.GetType().Name });
                return null;
            }
        }));
        var probe = wait is { } limit ? await Task.WhenAny(probing, Task.Delay(limit)) == probing ? await probing : null : await probing;
        // A failure isn't kept: the next try reads the file again.
        if (probing.IsCompleted && probing.Result is null) probes.TryRemove(KeyValuePair.Create(key, probing));
        return probe;
    }

    // How far the phone got. Saved as this PC saves its own playback, so Continue watching and the watched marks match.
    // A video playing here right now keeps its own position.
    private void SaveProgress(JsonElement message)
    {
        var id = Text(message, "id");
        if (Items().FirstOrDefault(x => x.Id == id) is not { IsMusic: false } item || Number(message, "positionMs") is not { } position) return;
        if (PlaysHere?.Invoke(item.Id) == true) return;
        var progress = PhoneProgress.From(position, Number(message, "durationMs") ?? 0, Flag(message, "ended"), WasWatched(item.Id));
        var saved = new List<KeyValuePair<string, string>> { KeyValuePair.Create("position:" + item.Id, progress.ResumeMs.ToString(CultureInfo.InvariantCulture)) };
        if (progress.Played) saved.Add(KeyValuePair.Create("played:" + item.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        library.SetSettings(saved);
        if (progress.Ended && !WasWatched(item.Id)) SetWatched(item, true);
        else ProgressSaved?.Invoke(item, Flag(message, "final"));
    }

    // What Continue on the PC saves before the PC plays the video: where the phone was, and a title watched before that
    // the phone has started again is no longer marked watched.
    public void SaveHandOver(MediaItem item, long? positionMs)
    {
        if (positionMs is not { } position || item.IsMusic) return;
        if (position >= 10000 && WasWatched(item.Id)) SetWatched(item, false);
        library.Setting("position:" + item.Id, Math.Max(0, position).ToString(CultureInfo.InvariantCulture));
    }

    // The video a play request names, if it can play.
    public MediaItem? Playable(JsonElement message, Action<string, string> fail)
    {
        var id = Text(message, "id");
        var item = Items().FirstOrDefault(x => x.Id == id && x.Available && !x.IsVirtual);
        if (item is null || !File.Exists(item.Path)) { fail("gone", "That video isn't available on the PC. Its drive may be disconnected."); return null; }
        return item;
    }
    public static long? Position(JsonElement message) => Number(message, "positionMs");

    // Play on the PC from a queue while BlackPearl is closed: the queue is saved as the player's, at the video's place,
    // which BlackPearl takes up as it opens, so Next goes on through the queue.
    public void SaveQueueHandOver(SavedQueue saved, int place)
    {
        var byId = ById(Items());
        var resolved = PhoneQueues.Entries(saved, byId).Select(e => e.Item.Id).ToArray();
        library.Setting("activeQueue", saved.Id);
        library.Setting("lastQueue", saved.Id);
        library.Setting("queue", Wire.Serialize(resolved));
        library.Setting("queuePosition", PhoneQueues.PlaybackIndex(saved, byId, place).ToString(CultureInfo.InvariantCulture));
    }
}
