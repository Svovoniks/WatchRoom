using System.Text.Json;
using System.Threading.Channels;

namespace Watchroom.Core;

// File work stays on one worker. Failed writes and full queues never stop playback.
public sealed class DiagnosticLog : IAsyncDisposable
{
    private sealed record Entry(string? Json, TaskCompletionSource? Flushed = null);
    private readonly Channel<Entry> entries = Channel.CreateBounded<Entry>(new BoundedChannelOptions(2048)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task writer;
    private readonly long maximumBytes;
    private readonly int retainedFiles;
    private long dropped;
    public string DirectoryPath { get; }
    public DiagnosticLog(string directory, long maximumBytes = 5 * 1024 * 1024, int retainedFiles = 4)
    {
        DirectoryPath = Path.GetFullPath(directory);
        this.maximumBytes = Math.Max(1024, maximumBytes); this.retainedFiles = Math.Clamp(retainedFiles, 1, 10);
        writer = Task.Run(WriteAsync);
    }
    public void Record(string kind, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { kind, monoMs = Environment.TickCount64, utcMs = Wire.Now,
                process = Environment.ProcessId, dropped = Interlocked.Read(ref dropped), data }, Wire.Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) + 1 > maximumBytes) { Interlocked.Increment(ref dropped); return; }
            if (!entries.Writer.TryWrite(new(json))) Interlocked.Increment(ref dropped);
        }
        catch { Interlocked.Increment(ref dropped); }
    }
    public async Task FlushAsync()
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await entries.Writer.WriteAsync(new(null, marker)); await marker.Task; }
        catch (ChannelClosedException) { }
    }
    private async Task WriteAsync()
    {
        FileStream? stream = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "diagnostics.jsonl");
            FileStream Open() => new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            stream = Open();
            await foreach (var entry in entries.Reader.ReadAllAsync())
            {
                if (entry.Json is null)
                {
                    try { await stream.FlushAsync(); }
                    finally { entry.Flushed?.TrySetResult(); }
                    continue;
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(entry.Json + "\n");
                if (stream.Length > 0 && stream.Length + bytes.Length > maximumBytes)
                {
                    await stream.DisposeAsync();
                    for (var index = retainedFiles - 1; index >= 1; index--)
                    {
                        var from = index == 1 ? path : path + "." + (index - 1);
                        if (File.Exists(from)) File.Move(from, path + "." + index, overwrite: true);
                    }
                    if (retainedFiles == 1) File.Delete(path);
                    stream = Open();
                }
                await stream.WriteAsync(bytes); await stream.FlushAsync();
            }
        }
        catch { entries.Writer.TryComplete(); }
        finally
        {
            if (stream is not null) { try { await stream.DisposeAsync(); } catch { } }
            while (entries.Reader.TryRead(out var entry)) entry.Flushed?.TrySetResult();
        }
    }
    public async ValueTask DisposeAsync() { entries.Writer.TryComplete(); await writer; }
}
