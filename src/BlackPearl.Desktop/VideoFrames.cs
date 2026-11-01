using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace BlackPearl.Desktop;

/// <summary>Episode thumbnails from the bundled LibVLC: decodes one frame into memory and saves it as a JPEG.</summary>
public static class VideoFrames
{
    private const int ThumbnailWidth = 640;
    private sealed record Frame(byte[] Pixels, int Width, int Height, int Pitch);

    public static async Task<bool> CaptureAsync(string video, double seconds, string output, CancellationToken ct)
    {
        // The background engine: no shared state with the playback engine, which closes independently.
        using var lease = BackgroundVlc.Acquire();
        if (lease is null) return false;
        using var media = new Media(lease.Engine, video, FromType.FromPath);
        media.AddOption(":start-time=" + seconds.ToString(CultureInfo.InvariantCulture));
        media.AddOption(":no-audio");
        media.AddOption(":no-spu");
        using var player = new MediaPlayer(lease.Engine) { EnableHardwareDecoding = false, EnableKeyInput = false, EnableMouseInput = false };
        var frame = new TaskCompletionSource<Frame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IntPtr allocation = IntPtr.Zero, pixels = IntPtr.Zero; int width = 0, height = 0, pitch = 0, captured = 0;

        // RV32 is BGRX in memory. Planes must be 32-byte aligned; pitches and line counts are rounded to 32.
        uint Format(ref IntPtr opaque, IntPtr chroma, ref uint w, ref uint h, ref uint pitches, ref uint lines)
        {
            Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
            width = (int)w; height = (int)h; pitch = (int)((w * 4 + 31) & ~31u); var rows = (int)((h + 31) & ~31u);
            pitches = (uint)pitch; lines = (uint)rows;
            Release();
            allocation = Marshal.AllocHGlobal(pitch * rows + 32); pixels = (IntPtr)(((long)allocation + 31) & ~31L);
            return 1;
        }
        void Release() { if (allocation != IntPtr.Zero) Marshal.FreeHGlobal(allocation); allocation = pixels = IntPtr.Zero; }
        void Cleanup(ref IntPtr opaque) => Release();
        IntPtr Lock(IntPtr opaque, IntPtr planes) { Marshal.WriteIntPtr(planes, pixels); return IntPtr.Zero; }
        void Display(IntPtr opaque, IntPtr picture)
        {
            if (Interlocked.Exchange(ref captured, 1) != 0) return;
            var copy = new byte[pitch * height]; Marshal.Copy(pixels, copy, 0, copy.Length);
            frame.TrySetResult(new Frame(copy, width, height, pitch));
        }
        LibVLCSharp.Shared.MediaPlayer.LibVLCVideoFormatCb format = Format; LibVLCSharp.Shared.MediaPlayer.LibVLCVideoCleanupCb cleanup = Cleanup;
        LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb @lock = Lock; LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb display = Display;
        player.SetVideoFormatCallbacks(format, cleanup);
        player.SetVideoCallbacks(@lock, null, display);
        // A seek past the end (short or mis-tagged videos) ends without a frame; the caller retries earlier.
        player.EndReached += (_, _) => frame.TrySetResult(null);
        player.EncounteredError += (_, _) => frame.TrySetResult(null);
        Frame? result;
        try
        {
            if (!player.Play(media)) return false;
            result = await frame.Task.WaitAsync(ct);
        }
        finally
        {
            // Stop joins the decoder threads, so no callback can touch the buffer after this.
            player.Stop();
            GC.KeepAlive(format); GC.KeepAlive(cleanup); GC.KeepAlive(@lock); GC.KeepAlive(display);
            Release();
        }
        if (result is null || result.Width <= 0 || result.Height <= 0) return false;
        // A fade-in or black leader is no preview; the caller tries another position.
        if (IsFlat(result)) return false;
        var track = media.Tracks.FirstOrDefault(x => x.TrackType == TrackType.Video).Data.Video;
        var aspect = result.Width / (double)result.Height * (track.SarNum > 0 && track.SarDen > 0 ? track.SarNum / (double)track.SarDen : 1);
        return Save(result, aspect, output);
    }

    // Nearly every sampled pixel is dark: a black frame, a fade, or a solid card rather than a picture.
    private static bool IsFlat(Frame frame)
    {
        long total = 0; var bright = 0; var samples = 0;
        var stepX = Math.Max(1, frame.Width / 64); var stepY = Math.Max(1, frame.Height / 36);
        for (var y = 0; y < frame.Height; y += stepY)
            for (var x = 0; x < frame.Width; x += stepX)
            {
                var offset = y * frame.Pitch + x * 4;
                if (offset + 2 >= frame.Pixels.Length) continue;
                var luma = (frame.Pixels[offset] * 11 + frame.Pixels[offset + 1] * 59 + frame.Pixels[offset + 2] * 30) / 100;
                total += luma; if (luma > 40) bright++; samples++;
            }
        return samples == 0 || total / samples < 12 && bright * 50 < samples;
    }
    // Thumbnails captured before dark frames were rejected can be black; such a file is replaced on the next visit.
    public static bool IsFlatThumbnail(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0) return false;
            long total = 0; var bright = 0; var samples = 0;
            var stepX = Math.Max(1, bitmap.Width / 64); var stepY = Math.Max(1, bitmap.Height / 36);
            for (var y = 0; y < bitmap.Height; y += stepY)
                for (var x = 0; x < bitmap.Width; x += stepX)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var luma = (pixel.Red * 30 + pixel.Green * 59 + pixel.Blue * 11) / 100;
                    total += luma; if (luma > 40) bright++; samples++;
                }
            return samples > 0 && total / samples < 12 && bright * 50 < samples;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Save(Frame frame, double aspect, string output)
    {
        var height = Math.Max(2, (int)Math.Round(ThumbnailWidth / aspect / 2) * 2);
        var handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = SKImage.FromPixels(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), handle.AddrOfPinnedObject(), frame.Pitch);
            using var surface = SKSurface.Create(new SKImageInfo(ThumbnailWidth, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            if (source is null || surface is null) return false;
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            surface.Canvas.DrawImage(source, new SKRect(0, 0, ThumbnailWidth, height), paint);
            using var image = surface.Snapshot();
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            if (jpeg is null) return false;
            using var file = File.Create(output); jpeg.SaveTo(file);
            return true;
        }
        finally { handle.Free(); }
    }
}
