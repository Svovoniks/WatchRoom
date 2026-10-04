namespace Watchroom.Core;
public sealed partial class RoomClient
{
    private bool HandleLibraryInteraction(string sender, WireMessage message)
    {
        if (message.Type is not ("catalog-follow" or "catalog-artwork" or "catalog-watch")) return false;
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
                var data = LibraryArtwork?.Invoke(SharedLibrary.ArtworkPath(source, request)) ?? "";
                if (data.Length > 200000) throw new InvalidDataException("Artwork too large");
                const int chunkSize = 8000;
                var count = Math.Max(1, (data.Length + chunkSize - 1) / chunkSize);
                for (var index = 0; index < count; index++)
                    SendPeer(sender, new("catalog-artwork-chunk", Number: message.Number,
                        Data: Wire.Serialize(new LibraryArtworkChunk(request.Id + ":" + request.Level + ":" + request.Season,
                            index, count, data.Substring(index * chunkSize, Math.Min(chunkSize, data.Length - index * chunkSize))))));
            }
            else
            {
                var item = source.FirstOrDefault(x => x.Id == message.Target && x.Available && !x.IsVirtual && !x.IsExtra)
                    ?? throw new InvalidDataException("This video is no longer available.");
                PublishHostedFile(PrepareHostedFile(item.Path, item.EpisodeDisplayTitle), true);
                Message?.Invoke(new("catalog-started", Target: item.Id));
                SendPeer(sender, new("catalog-started", Target: item.Id));
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException)
        { SendPeer(sender, new("catalog-error", Number: message.Number, Text: ex.Message)); }
        return true;
    }
}
