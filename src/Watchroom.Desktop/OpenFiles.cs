using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Watchroom.Core;

namespace Watchroom.Desktop;

// Opening a video from Explorer while Watchroom runs hands the path to the running app, which shares the library
// database, instead of starting a second copy. Each data directory has its own instance.
public static class OpenFileHandoff
{
    private static Mutex? instance;
    private static string Name => "Watchroom-" + LibraryIdentity.Hash(Path.GetFullPath(App.DataDirectory).ToUpperInvariant());

    public static string? FileArgument(IEnumerable<string> args) =>
        args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).Select(arg =>
        {
            try { return Path.GetFullPath(arg); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }).FirstOrDefault(path => path is not null && File.Exists(path));

    // True when this process is the data directory's running app.
    public static bool Claim()
    {
        instance = new Mutex(true, @"Local\" + Name, out var created);
        if (created) return true;
        try { if (instance.WaitOne(0)) return true; } catch (AbandonedMutexException) { return true; }
        instance.Dispose(); instance = null;
        return false;
    }

    public static bool Forward(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            // Explorer gave this process the foreground; pass it on so the running app can come to the front.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var owner)) AllowSetForegroundWindow(owner);
            var bytes = Encoding.UTF8.GetBytes(path + "\n");
            pipe.Write(bytes); pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    public static void Listen(Action<string> open, CancellationToken cancellation) => _ = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var buffer = new char[32768];
                var read = await reader.ReadBlockAsync(buffer, cancellation);
                var path = new string(buffer, 0, read).Split('\n')[0].Trim();
                if (path.Length > 0) open(path);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PlaybackDiagnostics.Record("open-file-handoff-error", new { error = ex.GetType().Name });
                await Task.Delay(500, CancellationToken.None);
            }
        }
    });

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out int processId);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

public partial class MainWindow
{
    private string? pendingOpenFile;
    private bool openFilesReady, openedFile;

    // Plays a video opened from outside the app. A video the library knows plays exactly as if started from its
    // details page; any other video plays on its own.
    public void OpenFile(string path)
    {
        if (closing) return;
        BringToFront();
        openedFile = true;
        if (!openFilesReady) { pendingOpenFile = path; return; }
        _ = Guard(() => OpenFileAsync(path));
    }
    private void OpenFilesReady()
    {
        openFilesReady = true;
        if (pendingOpenFile is { } path) { pendingOpenFile = null; _ = Guard(() => OpenFileAsync(path)); }
    }
    private async Task OpenFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException(Path.GetFileName(path) + " could not be found. It may have been moved or deleted.");
        if (room is not null)
        {
            var host = room.Identity?.Host == true;
            if (!Dialogs.Confirm(this, host ? "Close the watch room?" : "Leave the watch room?",
                $"Playing {Path.GetFileName(path)} on this computer {(host ? "closes the room for your guests" : "leaves the room")}.",
                "Play here", "Stay in room")) return;
            await Disconnect(); RefreshSavedRooms();
        }
        var catalog = items.ToArray();
        var opened = await Task.Run(() => OpenedFiles.Resolve(catalog, path));
        if (closing) return;
        PlaybackDiagnostics.Record("open-file", new { match = opened.Match, known = opened.Library?.Id });
        // The details page sits behind the player, so leaving it returns there as it would from the library.
        if (opened.Library is { } known && items.FirstOrDefault(item => item.Id == known.Id) is { } listed) Select(listed);
        queuePosition = -1;
        await PlayLocalItem(opened.Item);
        PublishQueue();
    }
    private void BringToFront()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && WindowState == WindowState.Minimized) ShowWindow(handle, 9);
        Activate();
    }
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private static string? DroppedVideo(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && OpenedFiles.IsVideo(paths[0]) && File.Exists(paths[0]) ? paths[0] : null;
    private void InitializeOpenFiles()
    {
        // The player overlay sits in its own window above the native video, so it takes drops separately.
        foreach (UIElement target in new UIElement[] { this, PlayerOverlay })
        {
            target.AllowDrop = true;
            target.DragOver += (_, e) => { if (e.Handled) return; e.Effects = DroppedVideo(e) is null ? DragDropEffects.None : DragDropEffects.Copy; e.Handled = true; };
            target.Drop += (_, e) => { if (e.Handled || DroppedVideo(e) is not { } path) return; e.Handled = true; OpenFile(path); };
        }
    }
    private void OpenDefaultAppsSettings(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Watchroom") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }
}
