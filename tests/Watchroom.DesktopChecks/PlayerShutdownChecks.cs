using System.Reflection;
using Watchroom.Desktop;

static class PlayerShutdownChecks
{
    public static async Task Run(MainWindow window, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var loading = (SemaphoreSlim)typeof(MainWindow).GetField("loading", flags)!.GetValue(window)!;
        var cancellationField = typeof(MainWindow).GetField("mediaLoadCancellation", flags)!;
        using var cancellation = new CancellationTokenSource();
        await loading.WaitAsync();
        cancellationField.SetValue(window, cancellation);
        Task disconnect;
        try
        {
            disconnect = (Task)typeof(MainWindow).GetMethod("Disconnect", flags)!.Invoke(window, new object[] { false, false })!;
            await Task.Yield();
            check(cancellation.IsCancellationRequested, "closing player cancels an unfinished media load before waiting for cleanup");
            check(!disconnect.IsCompleted, "closing player keeps native cleanup serialized with the current media load");
        }
        finally { cancellationField.SetValue(window, null); loading.Release(); }
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        check((int)typeof(MainWindow).GetField("nativeStopCount", flags)!.GetValue(window)! == 0,
            "closing player completes native cleanup without leaving the stop guard active");
    }
}
