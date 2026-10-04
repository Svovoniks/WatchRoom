namespace Watchroom.Desktop;

public partial class MainWindow
{
    private async Task StopNativePlayback()
    {
        var native = player;
        if (native is null) return;
        nativeStopCount++;
        try { await Task.Run(native.Stop); }
        finally { nativeStopCount--; }
    }
}
