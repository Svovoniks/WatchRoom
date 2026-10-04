using System.Diagnostics;
using Watchroom.Core;

static class BridgeShutdownFixture
{
    public static async Task Run(Action<bool, string> check)
    {
        var source = new StalledSource();
        await using var bridge = new MediaBridge();
        var url = await bridge.StartAsync(source);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var request = http.GetAsync(url);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Stopwatch.StartNew();
        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        check(elapsed.Elapsed < TimeSpan.FromSeconds(3), "bridge shutdown does not wait for a stalled remote media read");
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(source.Canceled.Task.IsCompletedSuccessfully, "bridge shutdown cancels outstanding media transfers");
        try { using var response = await request.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (HttpRequestException) { }
        check(request.IsCompleted, "bridge shutdown terminates the player's blocked HTTP request");
        bridge.CancelPendingReads(); await bridge.DisposeAsync();
        check(true, "bridge shutdown is safe to repeat");
    }
    private sealed class StalledSource : IMediaSource
    {
        public SharedMedia Media => new("shutdown", "Stalled video", 1048576, ".mkv");
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken ct)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return []; }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
        }
    }
}
