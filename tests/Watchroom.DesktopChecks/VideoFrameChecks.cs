using System.IO;
using SkiaSharp;
using Watchroom.Core;
using Watchroom.Desktop;

static class VideoFrameChecks
{
    public static async Task Run(string directory)
    {
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "short episode with spaces.avi"); VideoFixture.Write(video, 3);
        var past = Path.Combine(directory, "past-end.jpg");
        Check(!await VideoFrames.CaptureAsync(video, 180, past, default) && !File.Exists(past), "seeking past the end of a video yields no thumbnail");
        var frame = Path.Combine(directory, "frame.jpg");
        Check(await VideoFrames.CaptureAsync(video, 1, frame, default) && ArtworkCache.IsUsable(frame), "bundled LibVLC decodes an episode frame without FFmpeg");
        using (var image = SKBitmap.Decode(frame)) Check(image is { Width: 640, Height: 480 }, "thumbnail is 640 pixels wide and keeps the video aspect ratio");
        Check(!await VideoFrames.CaptureAsync(video + ".missing", 1, Path.Combine(directory, "missing.jpg"), default), "missing video yields no thumbnail");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var threw = false;
        try { await VideoFrames.CaptureAsync(video, 1, Path.Combine(directory, "cancelled.jpg"), cancelled.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check(threw, "cancelled capture stops promptly");
        var item = new MediaItem("thumbnail", video, "Example", "Show", Series: "Example", Season: 1, Episode: 1);
        var improved = await new EpisodeArtwork(VideoFrames.CaptureAsync).ImproveAsync(item, directory, null, default);
        Check(ArtworkCache.IsUsable(improved.GeneratedEpisodePoster), "episode artwork falls back to an early frame for short videos");
    }
}
