namespace Watchroom.Core;

public static class PlaybackDiagnostics
{
    private static readonly object gate = new();
    private static DiagnosticLog? log;
    private static readonly string? overrideDirectory = Environment.GetEnvironmentVariable("WATCHROOM_DIAGNOSTICS");
    static PlaybackDiagnostics() { if (!string.IsNullOrWhiteSpace(overrideDirectory)) Initialize(overrideDirectory); }
    public static bool Enabled => log is not null;
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
