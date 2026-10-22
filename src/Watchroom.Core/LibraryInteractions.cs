namespace Watchroom.Core;
public sealed partial class RoomClient
{
    private bool HandleLibraryInteraction(string sender, WireMessage message)
    {
        if (message.Type == "session-artwork") { SendSessionArtwork(sender, message); return true; }
        if (message.Type is not ("catalog-follow" or "catalog-artwork" or "catalog-watch")) return false;
        void Fail(Exception ex, string fallback)
        {
            PlaybackDiagnostics.Record("library-interaction-error", new { room = diagnosticId, command = message.Type, error = ex.GetType().Name });
            // Host file system errors can name local paths; only validation messages cross the peer connection.
            if (negotiated.ContainsKey(sender)) SendPeer(sender, new("catalog-error", Number: message.Number, Text: ex is InvalidDataException or System.Text.Json.JsonException ? ex.Message : fallback));
        }
        try
        {
            var source = Volatile.Read(ref hostedLibrary);
            if (message.Type == "catalog-follow")
            {
                var view = Wire.Read<LibraryBrowseView>(message.Data!);
                if (view.Browse is null) throw new InvalidDataException("Missing browse location");
                var page = SharedLibrary.Browse(source, view.Browse);
                foreach (var id in new[] { view.SelectedId, view.HoveredId }.OfType<string>())
                    if (!source.Any(item => item.Id == id && item.Available && !item.IsVirtual && !item.IsExtra)) throw new InvalidDataException("Title is no longer shared");
                coordinator!.DescribeLibrary(page.Location, view.Browse.Query, clock.Now);
                coordinator.DescribeLibraryView(view, clock.Now); PublishHostState();
            }
            else if (message.Type == "catalog-artwork")
            {
                var request = Wire.Read<LibraryArtworkRequest>(message.Data!);
                if (string.IsNullOrEmpty(request.Id) || request.Id.Length > 128 || request.Level is not ("movie" or "series" or "season" or "episode")) throw new InvalidDataException("Invalid artwork request");
                var fullHd = request.FullHd && !coordinator!.AnyoneBuffering && FullHdArtwork is not null;
                var artwork = fullHd ? FullHdArtwork : LibraryArtwork;
                var key = request.Id + ":" + request.Level + ":" + request.Season;
                if (request.FullHd && !fullHd) { SendArtwork(sender, "catalog-artwork-chunk", message.Number, key, "", true); return true; }
                // Artwork decoding reads files; keep the control loop responsive for clock and state replies.
                if (!Offload(() => artwork?.Invoke(SharedLibrary.ArtworkPath(source, request)) ?? "",
                    data => SendArtwork(sender, "catalog-artwork-chunk", message.Number, key, data, request.FullHd),
                    ex => Fail(ex, "Artwork is unavailable."))) Fail(new IOException("Library work queue is full"), "The host is busy. Try again shortly.");
            }
            else
            {
                var item = source.FirstOrDefault(x => x.Id == message.Target && x.Available && !x.IsVirtual && !x.IsExtra)
                    ?? throw new InvalidDataException("This video is no longer available.");
                var store = settings;
                // Opening the file can block on slow or network storage.
                if (!Offload(() =>
                {
                    var resume = message.Text == "resume" && store?.Setting("watched:" + item.Id) != "true" &&
                        long.TryParse(store?.Setting("position:" + item.Id), out var saved) ? Math.Max(0, saved) : 0;
                    return (Selection: PrepareHostedFile(item.Path, item.EpisodeDisplayTitle), Resume: resume);
                }, prepared =>
                {
                    if (!coordinator!.LibraryBrowsing || coordinator.LibraryActivity?.Peer != sender || !negotiated.ContainsKey(sender))
                        throw new InvalidDataException("Library browsing ended before the video opened.");
                    PublishHostedFile(prepared.Selection, true, prepared.Resume);
                    Message?.Invoke(new("catalog-started", Target: item.Id));
                    SendPeer(sender, new("catalog-started", Target: item.Id));
                }, ex => Fail(ex, "The host could not open this video."))) Fail(new IOException("Library work queue is full"), "The host is busy. Try again shortly.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
        { Fail(ex, "The host could not complete this library request."); }
        return true;
    }
    // Replies with chunks keyed by title; one empty chunk means the host has no artwork for that title.
    // Data "full" asks for the full HD copy, which the host declines while someone is buffering.
    private void SendSessionArtwork(string sender, WireMessage message)
    {
        var title = message.Text ?? "";
        var full = message.Data == "full";
        var current = Volatile.Read(ref sessionArtwork);
        var artwork = full ? FullHdArtwork : LibraryArtwork;
        if (title.Length is 0 or > 300 || current is null || current.Title != title || current.Path is null || artwork is null || full && coordinator!.AnyoneBuffering)
        { SendArtwork(sender, "session-artwork-chunk", message.Number, title, "", full); return; }
        // Artwork decoding reads files; keep the control loop responsive for clock and state replies.
        if (!Offload(() => artwork(current.Path) ?? "", data => SendArtwork(sender, "session-artwork-chunk", message.Number, title, data, full),
            _ => SendArtwork(sender, "session-artwork-chunk", message.Number, title, "", full)))
            SendArtwork(sender, "session-artwork-chunk", message.Number, title, "", full);
    }

    private sealed class ArtworkUpload(string type, long number, string key, string data, int count)
    {
        public string Type { get; } = type; public long Number { get; } = number; public string Key { get; } = key;
        public string Data { get; } = data; public int Count { get; } = count;
        public int Next { get; set; }
        public long Touched { get; set; } = Environment.TickCount64;
    }
    private readonly Dictionary<string, List<ArtworkUpload>> artworkTransfers = [];
    private void SendArtwork(string sender, string type, long number, string key, string data, bool paced)
    {
        if (!negotiated.ContainsKey(sender)) return;
        var (size, limit) = paced ? (ArtworkTransfer.PacedChunkSize, ArtworkTransfer.PacedMaxChunks) : (ArtworkTransfer.ChunkSize, ArtworkTransfer.MaxChunks);
        if (data.Length > size * limit) data = "";
        var count = Math.Max(1, (data.Length + size - 1) / size);
        if (!paced) { for (var index = 0; index < count; index++) SendArtworkChunk(sender, type, number, key, data, index, count, size, false); return; }
        var upload = new ArtworkUpload(type, number, key, data, count);
        if (count > ArtworkTransfer.Window)
        {
            // Each guest gets at most two paced transfers; abandoned ones expire.
            var uploads = artworkTransfers.TryGetValue(sender, out var list) ? list : artworkTransfers[sender] = [];
            uploads.RemoveAll(x => Environment.TickCount64 - x.Touched > 30000 || x.Number == number);
            if (uploads.Count >= 2) uploads.RemoveAt(0);
            uploads.Add(upload);
        }
        for (var index = 0; index < Math.Min(count, ArtworkTransfer.Window); index++) SendNextArtwork(sender, upload);
    }
    private void ContinueArtwork(string sender, long number)
    {
        if (!artworkTransfers.TryGetValue(sender, out var uploads) || uploads.FirstOrDefault(x => x.Number == number) is not { } upload) return;
        upload.Touched = Environment.TickCount64;
        SendNextArtwork(sender, upload);
        if (upload.Next >= upload.Count) uploads.Remove(upload);
        if (uploads.Count == 0) artworkTransfers.Remove(sender);
    }
    private void SendNextArtwork(string sender, ArtworkUpload upload)
    {
        if (upload.Next >= upload.Count) return;
        SendArtworkChunk(sender, upload.Type, upload.Number, upload.Key, upload.Data, upload.Next++, upload.Count, ArtworkTransfer.PacedChunkSize, true);
    }
    private void SendArtworkChunk(string sender, string type, long number, string key, string data, int index, int count, int size, bool paced) =>
        SendPeer(sender, new(type, Number: number, Data: Wire.Serialize(new LibraryArtworkChunk(key, index, count,
            data.Substring(Math.Min(data.Length, index * size), Math.Clamp(data.Length - index * size, 0, size)), paced))));
}
