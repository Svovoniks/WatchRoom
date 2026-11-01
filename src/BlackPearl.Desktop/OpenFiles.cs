using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// Opening a video from Explorer while BlackPearl runs hands the path to the running app, which shares the library
// database, instead of starting a second copy. Each data directory has its own instance.
public static class OpenFileHandoff
{
    private static Mutex? instance;
    // Keeps its name from when the app was called Watchroom, so a running Watchroom and a newly started BlackPearl
    // still recognize each other on the same library.
    private static string Name => "Watchroom-" + LibraryIdentity.Hash(Path.GetFullPath(Program.DataDirectory).ToUpperInvariant());

    public static string? FileArgument(IEnumerable<string> args) =>
        args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).Select(arg =>
        {
            try { return Path.GetFullPath(arg); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }).FirstOrDefault(path => path is not null && File.Exists(path));

    // True when this process is the data directory's running app.
    public static bool Claimed => instance is not null;
    public static bool Claim()
    {
        instance = new Mutex(true, @"Local\" + Name, out var created);
        if (created) return true;
        try { if (instance.WaitOne(0)) return true; } catch (AbandonedMutexException) { return true; }
        instance.Dispose(); instance = null;
        return false;
    }
    // A closing app stops taking videos at once, and gives up the data directory once it has saved where playback
    // stopped, so a video opened while it shuts down starts its own window instead of waiting for the old process
    // to exit. Release runs on the thread that claimed it.
    public static void StopListening() => listening?.Cancel();
    public static void Release()
    {
        StopListening();
        if (instance is null) return;
        try { instance.ReleaseMutex(); } catch (ApplicationException) { }
        instance.Dispose(); instance = null;
    }

    // Hands the path to the running app. Gives up when that app closes in the meantime, which then makes this
    // process the running app (see Claimed), or after five seconds.
    public static bool Forward(string path)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(100);
                // Explorer gave this process the foreground; pass it on so the running app can come to the front.
                if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var owner)) AllowSetForegroundWindow(owner);
                var bytes = Encoding.UTF8.GetBytes(path + "\n");
                pipe.Write(bytes); pipe.Flush();
                return true;
            }
            catch (TimeoutException) { }
            // The running app closed its end; it may be shutting down.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            if (Claim() || Environment.TickCount64 >= deadline) return false;
        }
    }

    private static CancellationTokenSource? listening;
    public static void Listen(Action<string> open, CancellationToken stop)
    {
        listening = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var cancellation = listening.Token;
        _ = Task.Run(async () =>
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
    }

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
        libraryReady.TrySetResult();
        if (pendingOpenFile is { } path) { pendingOpenFile = null; _ = Guard(() => OpenFileAsync(path)); }
    }

    private readonly TaskCompletionSource libraryReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // A video opened at launch shows the player before the library loads, titled from its file name.
    private void ShowStartupPlayer(string path)
    {
        openedFile = true;
        PlayerEmpty.Visibility = Visibility.Collapsed;
        RoomHeading.Text = PlaybackTitle(FileNames.Parse(path)); RoomSubtitle.Text = "Opening…";
        ConnectionStatus.Text = "Local playback";
        ShowPage("Room");
    }
    // Records a video opened at launch could match, read without waiting for the whole catalog. Files outside the library's
    // folders are left out, as the catalog load prunes them.
    private List<MediaItem> StartupFileCandidates(string path, IReadOnlyList<LibraryFolder> roots, IReadOnlyList<string> excluded) =>
        library.OpenedFileCandidates(path).Where(item => item.IsVirtual ||
            roots.Any(root => LibraryStore.IsWithin(item.Path, root.Path)) && !excluded.Any(folder => LibraryStore.IsWithin(item.Path, folder))).ToList();
    // Plays a video opened at launch while the player starts and the catalog loads. The file is matched against the
    // records it could belong to rather than the full catalog; the library's pages fill in behind the player.
    private async Task OpenStartupFileAsync(string path, Task<List<MediaItem>> candidates)
    {
        List<MediaItem> known;
        // A failed lookup only means the video plays on its own; the library load reports its own failures.
        try { known = await candidates; }
        catch (Exception ex) { PlaybackDiagnostics.Record("open-file-lookup-failed", new { error = ex.GetType().Name }); known = []; }
        // Only a few records remain, so matching them is quick enough for this thread.
        var opened = OpenedFiles.Resolve(known, path);
        if (closing || playingItem is not null) return;
        PlaybackDiagnostics.Record("open-file", new { match = opened.Match, known = opened.Library?.Id, startup = true });
        queuePosition = -1;
        try { await PlayLocalItem(opened.Item); }
        finally
        {
            // The player could not start: show the empty player instead of the opening title.
            if (playingItem is null && !closing)
            {
                PlayerEmpty.Visibility = Visibility.Visible;
                RoomHeading.Text = "BlackPearl player"; RoomSubtitle.Text = "Choose a video from your library to start watching.";
            }
        }
        if (playingItem is null) return;
        PublishQueue();
        // Leaving the player goes back to the details page and then Home, as when the video is started from the library.
        await libraryReady.Task;
        if (closing) return;
        var history = new List<BrowseLocation> { new("Home", 0, null, null, null, null, "", 0, 0, 0, openQueueId) };
        if (opened.Library is { } record && items.FirstOrDefault(item => item.Id == record.Id) is { } listed)
        {
            var series = listed.Series is null ? null : LibraryIdentity.ShowKey(listed);
            var kind = listed.Series is null ? null : listed.Kind;
            int? season = listed.Series is null ? null : listed.Season ?? -1;
            history.Add(new("Details", CategoryFor(listed), series, kind, season, listed.Id, "", 0, 0, 0, openQueueId));
            // Back to library from the player opens the video's show, unless the library is already showing elsewhere.
            if (currentPage == "Room") { libraryCategory = CategoryFor(listed); browseSeries = series; browseKind = kind; browseSeason = season; }
        }
        backLocations.InsertRange(0, history);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
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
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=BlackPearl") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }
}
