using System.Text.Json;
using System.Threading.Channels;

namespace Watchroom.Core;

// Opt-in diagnostics. Native callbacks and UI ticks never wait for disk writes.
public static class PlaybackDiagnostics
{
    private sealed record Entry(string Kind, long MonoMs, long UtcMs, object Data);
    private static readonly string? directory = Environment.GetEnvironmentVariable("WATCHROOM_DIAGNOSTICS");
    private static readonly Channel<Entry>? entries = string.IsNullOrWhiteSpace(directory) ? null :
        Channel.CreateBounded<Entry>(new BoundedChannelOptions(8192) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static long dropped;
    public static bool Enabled => entries is not null;
    static PlaybackDiagnostics() { if (entries is not null) _ = Task.Run(WriteAsync); }
    public static void Record(string kind, object data)
    {
        if (entries is not null && !entries.Writer.TryWrite(new(kind, Environment.TickCount64, Wire.Now, data)))
            Interlocked.Increment(ref dropped);
    }
    private static async Task WriteAsync()
    {
        try
        {
            Directory.CreateDirectory(directory!);
            await using var output = new StreamWriter(Path.Combine(directory!, "diagnostics.jsonl"), append: true);
            await foreach (var entry in entries!.Reader.ReadAllAsync())
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { kind = entry.Kind, monoMs = entry.MonoMs, utcMs = entry.UtcMs, dropped = Interlocked.Read(ref dropped), data = entry.Data }, Wire.Json));
                await output.FlushAsync();
            }
        }
        catch (Exception) { /* Diagnostics must not terminate playback. */ }
    }
}
