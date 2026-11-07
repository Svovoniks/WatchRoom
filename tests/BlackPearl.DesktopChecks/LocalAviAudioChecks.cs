using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LibVLCSharp.Shared;
using BlackPearl.Core;
using BlackPearl.Desktop;

// AVIs often declare AC3 as a byte stream. VLC's own AVI demuxer then splits frames, and the bundled libVLC has no AC3
// packetizer to rejoin them, so the decoder plays silence with no error. Plays one through the app's local path.
static class LocalAviAudioChecks
{
    // One 96 kb/s stereo frame of a 440 Hz tone (32 ms). AC3 frames decode independently, so repeating it stays valid.
    private const string ToneFrame = "C3fjmAxAQ+EG8AEMDAwhgYGH1p85r0ZT/PkR9DS0oVKuUZT/Pnx8+fPnz58Zf3hgwZANBsEHSO8TszpI6KviRqWWm5sXK9HWtupW5zVmrDzSmI+g2CDpHeJ2Z0kdFXxI1LLTMGE2HSYDCIPgvXAOaMz78dAe35ht+kxbdikyNcSZkSS6CmE3fPduzOMIg+C9cA5ozPvx0B7fmG34wYTcbQgYGmxODUGW5eOeb3+utJbi96NJxkKZplE7pC0X7GXVUadgabE4NQZbl455vf660luLzBhTnOqBzjdPdDnfX/NPIB9+GOq5VXVG9QGFdLpN3Myljax+3aUpecbp7oc76/5p5APvwx1XKq3kYMJsjVQFuxk9ZOhefYIbovg6LVjPw+atzSnrUxEFNvGqPEaDJPGbq3YyesnQvPsEN0XwdFqxn4SYMKc0oYLBii+xtiZKqzXaT/M+7VNm+Y3bk2UzOEc5U9J6sSvlXd4bGwYovsbYmSqs12k/zPu1TZtwACsH";

    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, flags)!.GetValue(window)!;
        object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
        var directory = Path.Combine(Path.GetTempPath(), "wr-avi-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "AC3 byte stream.avi");
        WriteAc3Avi(path, 8);
        var item = new MediaItem("local-avi-audio", path, "AC3 byte stream", "Movie");
        long samples = 0, audible = 0;
        // Fields of a closure the native side keeps calling; LibVLCSharp pins nothing for managed callbacks it is handed.
        MediaPlayer.LibVLCAudioPlayCb play = (_, data, count, _) =>
        {
            var pcm = new short[count * 2];
            Marshal.Copy(data, pcm, 0, pcm.Length);
            Interlocked.Add(ref samples, pcm.Length);
            Interlocked.Add(ref audible, pcm.Count(sample => Math.Abs((int)sample) > 64));
        };
        MediaPlayer.LibVLCAudioPauseCb pause = (_, _) => { };
        MediaPlayer.LibVLCAudioResumeCb resume = (_, _) => { };
        MediaPlayer.LibVLCAudioFlushCb flush = (_, _) => { };
        MediaPlayer.LibVLCAudioDrainCb drain = _ => { };
        MediaPlayer? player = null;
        try
        {
            await (Task)Call("EnsurePlayer")!;
            player = Field<MediaPlayer>("player");
            player.SetAudioFormat("S16N", 48000, 2);
            player.SetAudioCallbacks(play, pause, resume, flush, drain);
            await (Task)Call("PlayLocalItem", item)!;
            player.Mute = false; player.Volume = 100;
            for (var i = 0; i < 80 && Interlocked.Read(ref samples) < 96000 * 3; i++) await Task.Delay(100);
            var heard = Interlocked.Read(ref samples);
            check(heard >= 96000, $"a local AVI with AC3 audio decodes ({heard} samples)");
            var share = heard == 0 ? 0 : (double)Interlocked.Read(ref audible) / heard;
            check(share > .5, $"a local AVI whose AC3 is declared as a byte stream is not silent ({share:P0} of samples are audible)");
        }
        finally
        {
            // Back to the real output for the checks that follow.
            player?.Stop(); player?.SetAudioOutput("any");
            GC.KeepAlive(play); GC.KeepAlive(pause); GC.KeepAlive(resume); GC.KeepAlive(flush); GC.KeepAlive(drain);
            for (var i = 0; i < 20; i++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (IOException) { await Task.Delay(100); }
            }
        }
    }

    // Uncompressed video plus AC3 with the header that triggers the problem: sample size 1 and block align 1.
    public static void WriteAc3Avi(string path, int seconds)
    {
        const int width = 64, height = 48, size = width * height * 3, framesPerSecond = 10, audioBytesPerSecond = 12000;
        var tone = Convert.FromBase64String(ToneFrame);
        var videoFrames = seconds * framesPerSecond;
        var toneFrames = seconds * audioBytesPerSecond / tone.Length;
        using var stream = File.Create(path); using var w = new BinaryWriter(stream);
        void Four(string s) => w.Write(Encoding.ASCII.GetBytes(s));
        long Start(string id) { Four(id); w.Write(0); return stream.Position; }
        void End(long start) { var end = stream.Position; stream.Position = start - 4; w.Write((int)(end - start)); stream.Position = end; if ((end & 1) != 0) w.Write((byte)0); }
        var riff = Start("RIFF"); Four("AVI "); var hdrl = Start("LIST"); Four("hdrl");
        var avih = Start("avih"); foreach (var n in new[] { 1000000 / framesPerSecond, size * framesPerSecond, 0, 16, videoFrames, 0, 2, size, width, height, 0, 0, 0, 0 }) w.Write(n); End(avih);

        var videoList = Start("LIST"); Four("strl"); var strh = Start("strh"); Four("vids"); Four("DIB "); w.Write(0); w.Write((short)0); w.Write((short)0);
        foreach (var n in new[] { 0, 1, framesPerSecond, 0, videoFrames, size, -1, 0 }) w.Write(n);
        w.Write((short)0); w.Write((short)0); w.Write((short)width); w.Write((short)height); End(strh);
        var strf = Start("strf"); w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); foreach (var n in new[] { 0, size, 0, 0, 0, 0 }) w.Write(n); End(strf); End(videoList);

        var audioList = Start("LIST"); Four("strl"); strh = Start("strh"); Four("auds"); w.Write(0); w.Write(0); w.Write((short)0); w.Write((short)0);
        foreach (var n in new[] { 0, 1, audioBytesPerSecond, 0, toneFrames * tone.Length, audioBytesPerSecond, -1, 1 }) w.Write(n);
        w.Write(0); w.Write(0); End(strh);
        strf = Start("strf"); w.Write((short)0x2000); w.Write((short)2); w.Write(48000); w.Write(audioBytesPerSecond); w.Write((short)1); w.Write((short)0); w.Write((short)0); End(strf); End(audioList);
        End(hdrl);

        var movi = Start("LIST"); Four("movi");
        var index = new List<(string Id, int Offset, int Size)>();
        var written = 0;
        for (var f = 0; f < videoFrames; f++)
        {
            index.Add(("00db", (int)(stream.Position - movi), size)); var frame = Start("00db");
            for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) { w.Write((byte)(f * 2)); w.Write((byte)(y * 5)); w.Write((byte)(x * 4)); }
            End(frame);
            for (; written < (f + 1) * toneFrames / videoFrames; written++)
            {
                index.Add(("01wb", (int)(stream.Position - movi), tone.Length)); var chunk = Start("01wb");
                w.Write(tone); End(chunk);
            }
        }
        End(movi);
        var idx1 = Start("idx1"); foreach (var (id, offset, length) in index) { Four(id); w.Write(16); w.Write(offset); w.Write(length); } End(idx1);
        End(riff);
    }
}
