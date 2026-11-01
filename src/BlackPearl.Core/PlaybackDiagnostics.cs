namespace BlackPearl.Core;

public static class PlaybackDiagnostics
{
    private static readonly object gate = new();
    private static DiagnosticLog? log;
    private static readonly string? overrideDirectory = AppEnvironment.Get("DIAGNOSTICS");
    static PlaybackDiagnostics() { if (!string.IsNullOrWhiteSpace(overrideDirectory)) Initialize(overrideDirectory); }
    public static bool Enabled => log is not null;
    // Per-frame and per-tick records (positions, samples, range reads, buffering, settling, clock) are only
    // kept when diagnostics are redirected for a test run or BLACKPEARL_DIAGNOSTICS_DETAIL=1 is set.
    public static bool Detailed => Enabled && detailed;
    private static readonly bool detailed = !string.IsNullOrWhiteSpace(overrideDirectory) || AppEnvironment.Get("DIAGNOSTICS_DETAIL") == "1";
    public static string? DirectoryPath => log?.DirectoryPath;
    public static void Initialize(string directory)
    {
        lock (gate)
        {
            try { log ??= new DiagnosticLog(string.IsNullOrWhiteSpace(overrideDirectory) ? directory : overrideDirectory); }
            catch { /* Diagnostics must not prevent startup. */ }
        }
    }
    public static void Record(string kind, object data) => log?.Record(kind, data);
    public static Task FlushAsync() => log?.FlushAsync() ?? Task.CompletedTask;
}
