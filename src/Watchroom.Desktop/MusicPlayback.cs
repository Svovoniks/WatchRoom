using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Audio has no picture, so while music plays the player shows the track's artwork, title, artist and album, and keeps
// its controls on screen. Library music is known before it starts; anything else, such as a song streamed from a
// room's host, counts as music once it has played for a moment without a video track.
public partial class MainWindow
{
    private const long AudioCheckDelayMs = 1500;
    private bool audioPlayback, audioChecked;
    private int audioCheckGeneration = -1, audioArtworkVersion;
    private long audioCheckSince, audioDetailsAt;
    private string? audioArtworkPath;

    private bool PlayingKnownMusic => playingItem?.IsMusic == true && (room is null || room.Identity?.Host == true);

    // Called as library music starts loading, so its panel shows before playback begins; anything else clears it.
    private void ShowKnownMusic()
    {
        audioCheckGeneration = loadGeneration; audioCheckSince = Environment.TickCount64; audioChecked = PlayingKnownMusic;
        SetAudioPlayback(PlayingKnownMusic);
    }

    // Runs with the playback tick.
    private void UpdateAudioPlayback()
    {
        if (player is null || PlayerEmpty.Visibility == Visibility.Visible) { SetAudioPlayback(false); return; }
        if (audioCheckGeneration != loadGeneration)
        {
            audioCheckGeneration = loadGeneration; audioCheckSince = Environment.TickCount64; audioChecked = false;
            if (!PlayingKnownMusic) SetAudioPlayback(false);
        }
        if (!audioChecked)
        {
            if (PlayingKnownMusic) { audioChecked = true; SetAudioPlayback(true); return; }
            if (!ready || Environment.TickCount64 - audioCheckSince < AudioCheckDelayMs) return;
            audioChecked = true;
            SetAudioPlayback(player.VideoTrackCount <= 0 && player.AudioTrackCount > 0);
            return;
        }
        // A streamed song's tags arrive as it plays; library music changes only when its record does.
        if (audioPlayback && Environment.TickCount64 - audioDetailsAt > 2000) RefreshAudioDetails();
    }

    private void SetAudioPlayback(bool on)
    {
        if (audioPlayback == on) { if (on) RefreshAudioDetails(); return; }
        audioPlayback = on;
        AudioPlayback.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { RefreshAudioDetails(); ShowPlayerControls(); }
        else
        {
            audioArtworkPath = null; audioArtworkVersion++;
            AudioArtwork.Source = AudioBackdrop.Source = null; AudioArtworkGlyph.Visibility = Visibility.Visible;
        }
        UpdateSystemMediaControls();
    }

    private void RefreshAudioDetails()
    {
        audioDetailsAt = Environment.TickCount64;
        var item = PlayingKnownMusic ? playingItem : null;
        string? title = item?.Title, artist = item?.Artist, album = item?.Series;
        var artwork = item is null ? null : LibraryCatalog.EpisodeArtwork(item);
        if (item is null && player is not null)
        {
            using var media = player.Media;
            if (media is not null)
            {
                string? Meta(MetadataType type) => media.Meta(type) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
                artist = Meta(MetadataType.Artist) ?? Meta(MetadataType.AlbumArtist); album = Meta(MetadataType.Album);
                // Without an artist, VLC's title is often just the stream's address; the room names the song instead.
                if (artist is not null) title = Meta(MetadataType.Title);
                if (Meta(MetadataType.ArtworkURL) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile) artwork = uri.LocalPath;
            }
        }
        title ??= room?.Snapshot?.Media?.Title ?? playingItem?.Title ?? "Now playing";
        if (AudioTitle.Text != title) AudioTitle.Text = title;
        AudioArtist.Text = artist ?? "";
        AudioArtist.Visibility = artist is null ? Visibility.Collapsed : Visibility.Visible;
        AudioAlbum.Text = album ?? "";
        AudioAlbum.Visibility = album is null ? Visibility.Collapsed : Visibility.Visible;
        if (artwork != audioArtworkPath)
        {
            audioArtworkPath = artwork; var version = ++audioArtworkVersion;
            AudioArtwork.Source = AudioBackdrop.Source = null; AudioArtworkGlyph.Visibility = Visibility.Visible;
            if (artwork is not null) _ = LoadAudioArtwork(artwork, version);
        }
        UpdateSystemMediaControls();
    }

    private async Task LoadAudioArtwork(string path, int version)
    {
        var image = await Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 640; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
        });
        if (closing || version != audioArtworkVersion || image is null) return;
        AudioArtwork.Source = AudioBackdrop.Source = image;
        AudioArtworkGlyph.Visibility = Visibility.Collapsed;
    }

    // The details page of a track: what the tags say about it.
    private static string MusicSummary(MediaItem item) => string.Join("\n", new[]
    {
        item.Artist is null ? null : "Artist: " + item.Artist,
        item.Series is null ? null : "Album: " + item.Series,
        item.Episode is { } track ? $"Track {track}" + (item.Season is > 1 ? $" · Disc {item.Season}" : "") : null,
        item.Year is { } year ? "Year: " + year : null,
        item.Genres?.Length > 0 ? "Genre: " + string.Join(", ", item.Genres) : null,
        item.DurationSeconds is > 0 ? "Length: " + MusicFiles.Duration(item.DurationSeconds.Value) : null
    }.Where(x => x is not null));
}
