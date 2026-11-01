using LibVLCSharp.Shared;

namespace BlackPearl.Desktop;

/// <summary>
/// One long-lived libVLC engine for background work: episode frames and decoding audio for language identification.
/// libVLC 3 aborts the whole process after about 80 engines have been created and released, so background work
/// shares this engine instead of creating its own. It stays apart from the playback engine.
/// </summary>
internal static class BackgroundVlc
{
    private static readonly object gate = new();
    private static LibVLC? engine;
    private static int users;
    private static bool closed;

    /// <summary>The shared engine, held until the lease is disposed; null once the app is exiting.</summary>
    public static Lease? Acquire()
    {
        lock (gate)
        {
            if (closed) return null;
            if (engine is null)
            {
                LibVLCSharp.Shared.Core.Initialize();
                // Audio, video and subtitle output are chosen per media by each kind of work.
                engine = new LibVLC("--quiet", "--no-sub-autodetect-file", "--no-video-title-show", "--no-osd", "--no-stats");
                // An engine still alive at process exit crashes it; wait briefly for work in progress, then release it.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
            }
            users++;
            return new Lease(engine);
        }
    }

    private static void Release()
    {
        lock (gate) { users--; Monitor.PulseAll(gate); }
    }

    private static void Close()
    {
        lock (gate)
        {
            closed = true;
            var deadline = Environment.TickCount64 + 5000;
            while (users > 0 && Environment.TickCount64 < deadline) Monitor.Wait(gate, 100);
            if (users > 0) return;
            engine?.Dispose();
            engine = null;
        }
    }

    public sealed class Lease(LibVLC engine) : IDisposable
    {
        private int released;
        public LibVLC Engine => engine;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) Release(); }
    }
}
