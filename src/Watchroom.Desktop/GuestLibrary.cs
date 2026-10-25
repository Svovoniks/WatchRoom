using System.IO;
using System.Windows;
using Watchroom.Core;
namespace Watchroom.Desktop;
public partial class MainWindow
{
    private readonly Dictionary<string, (long Length, long Modified, string? Data)> libraryThumbnails = new();
    private string? CachedLibraryThumbnail(string? path)
    {
        if (path is null) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            lock (libraryThumbnails)
            {
                if (libraryThumbnails.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc.Ticks) return cached.Data;
                var data = MakeThumbnail(path, 4000);
                if (libraryThumbnails.Count >= 512) libraryThumbnails.Clear();
                libraryThumbnails[path] = (file.Length, file.LastWriteTimeUtc.Ticks, data);
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private string? HighResolutionLibraryArtwork(string? path) => MakeThumbnail(path, 100000, true);
    // Fits artwork inside 1920x1080 without enlarging it, lowering JPEG quality until it fits the transfer limit.
    private static string? FullHdLibraryArtwork(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(file, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            var scale = Math.Min(1, Math.Min(1920d / frame.PixelWidth, 1080d / frame.PixelHeight));
            var width = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
            file.Position = 0;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
            bitmap.DecodePixelWidth = width; bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit(); bitmap.Freeze();
            foreach (var quality in new[] { 88, 80, 70, 60 })
            {
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output);
                if (output.Length <= ArtworkTransfer.MaxFullHdBytes) return Convert.ToBase64String(output.ToArray());
            }
            return MakeThumbnail(path, 100000, true);
        }
        catch (Exception) { return null; }
    }
    private string HostPeerId() => room?.Snapshot?.People.FirstOrDefault(p => p.IsHost)?.Id ?? room?.Identity?.Peer ?? "";
    private static string? MakeThumbnail(string? path, int maximumBytes = 12000, bool highResolution = false)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            foreach (var width in highResolution ? new[] { 600, 480, 360 } : new[] { 160, 100, 64 })
            {
                file.Position = 0;
                var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit();
                bitmap.DecodePixelWidth = width; bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit();
                var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = highResolution ? 88 : 65 };
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var output = new MemoryStream(); encoder.Save(output);
                if (output.Length <= maximumBytes) return Convert.ToBase64String(output.ToArray());
            }
            return null;
        }
        catch (Exception) { return null; }
    }
}
