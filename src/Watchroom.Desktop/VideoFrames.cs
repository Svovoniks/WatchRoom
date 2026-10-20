using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace Watchroom.Desktop;

/// <summary>Episode thumbnails from the bundled LibVLC: decodes one frame into memory and saves it as a JPEG.</summary>
public static class VideoFrames
{
    private const int ThumbnailWidth = 640;
    private sealed record Frame(byte[] Pixels, int Width, int Height, int Pitch);

    public static async Task<bool> CaptureAsync(string video, double seconds, string output, CancellationToken ct)
    {
        LibVLCSharp.Shared.Core.Initialize();
        // A private engine per capture: no shared state with the playback engine, which closes independently.
        using var engine = new LibVLC("--quiet", "--no-audio", "--no-spu", "--no-sub-autodetect-file", "--no-video-title-show", "--no-osd", "--no-stats");
        using var media = new Media(engine, video, FromType.FromPath);
        media.AddOption(":start-time=" + seconds.ToString(CultureInfo.InvariantCulture));
        using var player = new MediaPlayer(engine) { EnableHardwareDecoding = false, EnableKeyInput = false, EnableMouseInput = false };
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
        var track = media.Tracks.FirstOrDefault(x => x.TrackType == TrackType.Video).Data.Video;
        var aspect = result.Width / (double)result.Height * (track.SarNum > 0 && track.SarDen > 0 ? track.SarNum / (double)track.SarDen : 1);
        return Save(result, aspect, output);
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
