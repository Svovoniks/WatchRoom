using System.IO;
using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, int> roomSubtitleIds = [];
    private CancellationTokenSource? roomSubtitleCancellation;
    private bool roomSubtitlesLoading;
    private long roomSubtitleRetryAt;
    private bool CanChangeRoomTracks => nativeStopCount == 0 && (room is null || ready && loadedMedia == room.Snapshot?.Media?.Id &&
        room.IsConnected && (room.Identity?.Host == true || room.Snapshot?.SharedControls == true));
    private string RoomSubtitleName(int id, string fallback) => room?.Snapshot?.Media?.Subtitles?
        .FirstOrDefault(asset => roomSubtitleIds.TryGetValue(asset.Id, out var trackId) && trackId == id)?.Title ?? fallback;

    private void ResetRoomTracks()
    {
        roomSubtitleCancellation?.Cancel();
        roomSubtitleIds.Clear();
        roomSubtitleRetryAt = 0;
    }

    private SharedTrack? DescribeRoomTrack(int id, TrackChoice[] choices, bool subtitle)
    {
        if (id == -1) return new(-1);
        if (subtitle)
            foreach (var pair in roomSubtitleIds) if (pair.Value == id) return new(0, pair.Key);
        var embedded = choices.Where(choice => choice.Id >= 0 && (!subtitle || !roomSubtitleIds.ContainsValue(choice.Id))).ToArray();
        var index = Array.FindIndex(embedded, choice => choice.Id == id);
        return index < 0 ? null : new(index);
    }

    private int? ResolveRoomTrack(SharedTrack? track, TrackChoice[] choices, bool subtitle)
    {
        if (track is null) return null;
        if (track.Index == -1) return -1;
        if (track.AssetId is { } asset) return roomSubtitleIds.TryGetValue(asset, out var id) ? id : null;
        var embedded = choices.Where(choice => choice.Id >= 0 && (!subtitle || !roomSubtitleIds.ContainsValue(choice.Id))).ToArray();
        return track.Index >= 0 && track.Index < embedded.Length ? embedded[track.Index].Id : null;
    }

    private void SynchronizeRoomTracks(TrackChoice[] audio, TrackChoice[] subtitles)
    {
        if (room?.Snapshot?.Media is not { } media || player is null || !ready || loadedMedia != media.Id) return;
        var assetsReady = !(media.Subtitles ?? []).Any(asset => !roomSubtitleIds.ContainsKey(asset.Id));
        if (!assetsReady)
        {
            if (!roomSubtitlesLoading && Environment.TickCount64 >= roomSubtitleRetryAt) _ = AttachRoomSubtitles(room, media, loadGeneration);
        }
        if (room.Snapshot.Tracks is { } tracks && tracks.MediaId == media.Id)
        {
            if (ResolveRoomTrack(tracks.Audio, audio, false) is { } audioId && player.AudioTrack != audioId) player.SetAudioTrack(audioId);
            if (assetsReady && ResolveRoomTrack(tracks.Subtitles, subtitles, true) is { } subtitleId && player.Spu != subtitleId) player.SetSpu(subtitleId);
        }
        if (room.Identity?.Host == true)
        {
            var audioChoice = room.Snapshot.Tracks?.Audio is null ? DescribeRoomTrack(player.AudioTrack, audio, false) : null;
            var subtitleChoice = assetsReady && room.Snapshot.Tracks?.Subtitles is null ? DescribeRoomTrack(player.Spu, subtitles, true) : null;
            if (audioChoice is not null || subtitleChoice is not null)
                room.Send(new("tracks", Data: Wire.Serialize(new RoomTracks(media.Id, audioChoice, subtitleChoice))));
        }
    }

    private void PublishRoomTrack(int id, bool subtitle)
    {
        if (room?.Snapshot?.Media is not { } media || player is null || !CanChangeRoomTracks) return;
        var choices = (subtitle ? player.SpuDescription : player.AudioTrackDescription).Select(t => new TrackChoice(t.Id, t.Name)).ToArray();
        if (DescribeRoomTrack(id, choices, subtitle) is not { } track) return;
        room.Send(new("tracks", Data: Wire.Serialize(new RoomTracks(media.Id, subtitle ? null : track, subtitle ? track : null))));
    }

    private async Task AttachRoomSubtitles(RoomClient client, SharedMedia media, int generation)
    {
        roomSubtitlesLoading = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        roomSubtitleCancellation = cancellation;
        var token = cancellation.Token;
        bool Current() => !closing && room == client && generation == loadGeneration && loadedMedia == media.Id;
        try
        {
            foreach (var asset in media.Subtitles ?? [])
            {
                if (!Current()) return;
                if (roomSubtitleIds.ContainsKey(asset.Id)) continue;
                if (asset.Length is <= 0 or > 8388608 || !new[] { ".srt", ".ass", ".ssa", ".sub" }.Contains(asset.Extension.ToLowerInvariant())) continue;
                var source = await client.GetSourceAsync(asset, token);
                var directory = Path.Combine(App.DataDirectory, "subtitles"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + asset.Extension.ToLowerInvariant());
                try
                {
                    await using (var output = File.Create(path))
                        for (long offset = 0; offset < asset.Length;)
                        {
                            var bytes = await source.ReadAsync(offset, (int)Math.Min(32768, asset.Length - offset), token);
                            if (bytes.Length == 0) throw new IOException("Subtitle transfer ended early.");
                            await output.WriteAsync(bytes, token); offset += bytes.Length;
                        }
                    if (!Current()) return;
                    var previous = player!.SpuDescription.Where(t => t.Id >= 0).Select(t => t.Id).ToHashSet();
                    if (!player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, false)) throw new IOException("Shared subtitles could not be loaded.");
                    var deadline = Environment.TickCount64 + 5000;
                    while (Current() && Environment.TickCount64 < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        var added = player.SpuDescription.FirstOrDefault(t => t.Id >= 0 && !previous.Contains(t.Id));
                        if (added.Name is not null) { roomSubtitleIds[asset.Id] = added.Id; break; }
                        await Task.Delay(50, token);
                    }
                    if (Current() && !roomSubtitleIds.ContainsKey(asset.Id)) throw new IOException("Shared subtitle track did not become available.");
                }
                finally { if (!Current() || !roomSubtitleIds.ContainsKey(asset.Id)) { try { File.Delete(path); } catch (IOException) { } } }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { if (Current()) { roomSubtitleRetryAt = Environment.TickCount64 + 10000; SetStatus(ex.Message); } }
        finally
        {
            if (roomSubtitleCancellation == cancellation) { roomSubtitleCancellation = null; roomSubtitlesLoading = false; }
            if (Current()) RefreshTracks();
        }
    }
}
