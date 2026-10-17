namespace Watchroom.Core;
public sealed partial class RoomClient
{
    private bool HandleLibraryInteraction(string sender, WireMessage message)
    {
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
                var artwork = LibraryArtwork;
                // Artwork decoding reads files; keep the control loop responsive for clock and state replies.
                if (!Offload(() => artwork?.Invoke(SharedLibrary.ArtworkPath(source, request)) ?? "", data =>
                {
                    if (data.Length > 200000) throw new InvalidDataException("Artwork too large");
                    if (!negotiated.ContainsKey(sender)) return;
                    const int chunkSize = 8000;
                    var count = Math.Max(1, (data.Length + chunkSize - 1) / chunkSize);
                    for (var index = 0; index < count; index++)
                        SendPeer(sender, new("catalog-artwork-chunk", Number: message.Number,
                            Data: Wire.Serialize(new LibraryArtworkChunk(request.Id + ":" + request.Level + ":" + request.Season,
                                index, count, data.Substring(index * chunkSize, Math.Min(chunkSize, data.Length - index * chunkSize))))));
                }, ex => Fail(ex, "Artwork is unavailable."))) Fail(new IOException("Library work queue is full"), "The host is busy. Try again shortly.");
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
}
