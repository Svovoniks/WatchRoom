using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using BlackPearl.Core;
using Microsoft.Win32;

namespace BlackPearl.Desktop;

// The phone remote's settings, read the same way by the app and by the phone host.
internal static class PhoneRemoteSettings
{
    public static bool Enabled(LibraryStore library) => library.Setting("phoneRemote") == "on";
    public static bool PhonesCanStart(LibraryStore library) => library.Setting("phoneRemoteCanStart") != "off";
    // Phones keep the library while BlackPearl is closed (PhoneHost). On unless turned off.
    public static bool WhileClosed(LibraryStore library) => library.Setting("phoneRemoteWhileClosed") != "off";
    // The phone host also starts when the user signs in to Windows. Off unless chosen.
    public static bool AtSignIn(LibraryStore library) => library.Setting("phoneRemoteAtSignIn") == "on";
    public static string? HostKey(LibraryStore library) => library.Setting("phoneRemoteHostKey") is { Length: 64 } key ? key : null;
    public static bool HasPhones(LibraryStore library) => new LibraryPhoneStore(library).Load().Count > 0;
    public static string Server(LibraryStore library) => ServerSettings.Saved(library);
    public static string AppVersion => typeof(PhoneRemoteSettings).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string PcName => Environment.MachineName;
}

internal sealed class LibraryPhoneStore(LibraryStore library) : IPairedPhoneStore
{
    public IReadOnlyList<PairedPhone> Load()
    {
        try { return Wire.Read<List<PairedPhone>>(library.Setting("phoneRemotePhones") ?? "[]"); }
        catch (JsonException) { return []; }
    }
    public void Save(IReadOnlyList<PairedPhone> phones) => library.Setting("phoneRemotePhones", Wire.Serialize(phones));
}

// While BlackPearl is closed, paired phones can still browse this PC's library, watch it on the phone and save how far
// they got (PHONE-REMOTE.md). A process of the same app with no window does it: BlackPearl starts it as it closes, and
// Windows at sign-in when chosen. It shows an icon in the notification area, and ends as soon as BlackPearl opens the
// library again, which takes the phones over: the room server hands the PC's id to the newer connection, and the phones
// reconnect to it.
internal static class PhoneHost
{
    public const string Argument = "--phone-host";
    public const string SignInArgument = "--signed-in";
    private static string Name => OpenFileHandoff.InstanceName + "-phone-host";

    // BLACKPEARL_PHONE_HOST=off keeps a build from starting the host or changing the sign-in entry, as dev launches do.
    public static bool Off => AppEnvironment.Get("PHONE_HOST") is { } value && value.Equals("off", StringComparison.OrdinalIgnoreCase);

    // Whether the app has this library open: it holds its instance mutex from launch until it has closed.
    internal static bool AppOpen
    {
        get
        {
            if (!Mutex.TryOpenExisting(@"Local\" + OpenFileHandoff.InstanceName, out var open)) return false;
            open.Dispose();
            return true;
        }
    }

    // Called by the app once it has closed and let go of the library.
    public static void Start()
    {
        if (Off || Environment.ProcessPath is not { } exe) return;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.ArgumentList.Add(Argument); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Program.DataDirectory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        { PlaybackDiagnostics.Record("phone-host-start-failed", new { error = ex.GetType().Name }); }
    }

    // Whether BlackPearl is open or this host started it moments ago and it is still starting. A second start then would
    // open a second window on the same library: a double click on the icon clicks twice, and the icon stays until the
    // app has taken the phones over.
    private static long openedAt = -1;
    internal static bool AppOpenOrOpening => AppOpen || openedAt >= 0 && Environment.TickCount64 - openedAt < 15000;

    // Opens BlackPearl, optionally playing a video once its library has loaded, unless it is open or opening already.
    internal static void OpenApp(string? playId = null)
    {
        if (AppOpenOrOpening || Environment.ProcessPath is not { } exe) return;
        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (playId is not null) { start.ArgumentList.Add(Program.PlayArgument); start.ArgumentList.Add(playId); }
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Program.DataDirectory);
            Process.Start(start)?.Dispose();
            openedAt = Environment.TickCount64;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        { PlaybackDiagnostics.Record("phone-host-open-failed", new { error = ex.GetType().Name }); }
    }

    // The host's own process. One per library: a second one waits for the first to end, since a host closing as the app
    // opens and a new one started as the app closes again can overlap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(bool signedIn)
    {
        using var claim = new Mutex(false, @"Local\" + Name);
        try { if (!claim.WaitOne(TimeSpan.FromSeconds(10))) return; }
        catch (AbandonedMutexException) { }
        try
        {
            if (AppOpen) return;
            // It draws nothing; without this its hidden window loads the graphics driver, a few hundred MB of it.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            PlaybackDiagnostics.Initialize(Path.Combine(Program.DataDirectory, "logs", "phone-host"));
            LibraryStore library;
            try { library = new LibraryStore(Program.DataDirectory, deferRetention: true); }
            catch (Exception ex) { PlaybackDiagnostics.Record("phone-host-library-failed", new { error = ex.GetType().Name }); return; }
            if (!PhoneRemoteSettings.Enabled(library) || !PhoneRemoteSettings.WhileClosed(library) || (signedIn && !PhoneRemoteSettings.AtSignIn(library))
                || PhoneRemoteSettings.HostKey(library) is null || !PhoneRemoteSettings.HasPhones(library))
            {
                PlaybackDiagnostics.Record("phone-host-not-needed", new { signedIn });
                return;
            }
            new PhoneHostProcess(library).Run();
        }
        finally
        {
            try { PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            claim.ReleaseMutex();
        }
    }
}

// The phone host's link, library and icon, on one dispatcher thread.
internal sealed class PhoneHostProcess(LibraryStore library)
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<MediaItem> items = [];
    private PhoneRemoteHost? remote;
    private PhoneLibrary? phoneLibrary;
    private TrayIcon? tray;
    private bool ending;

    public void Run()
    {
        PlaybackDiagnostics.Record("phone-host-start", new { version = PhoneRemoteSettings.AppVersion });
        items = library.All();
        var link = remote = new PhoneRemoteHost(new PhoneRemoteOptions
        {
            Server = PhoneRemoteSettings.Server(library), HostKey = PhoneRemoteSettings.HostKey(library)!, PcName = PhoneRemoteSettings.PcName,
            Store = new LibraryPhoneStore(library), CanStart = PhoneRemoteSettings.PhonesCanStart(library), Features = PhoneLibrary.Features,
            AppVersion = PhoneRemoteSettings.AppVersion,
        });
        phoneLibrary = new PhoneLibrary(library, () => remote, action => dispatcher.BeginInvoke(action), lifetime.Token)
        {
            Items = () => items,
            WasWatched = id => library.Setting("watched:" + id) == "true",
            SetWatched = (item, watched) => library.Setting("watched:" + item.Id, watched ? "true" : "false"),
            Play = PlayOnPc,
        };
        phoneLibrary.Attach(link);
        // Nothing plays here: the remote's buttons have nothing to control until BlackPearl opens.
        var volume = int.TryParse(library.Setting("volume"), out var saved) ? Math.Clamp(saved, 0, 100) : 100;
        link.CurrentState = () => new RemoteState(false, Volume: volume);
        link.Command += (session, _) => link.SendError(session, "nothing-playing", "BlackPearl isn't open on the PC. Choose a video in Library to play it on the PC or here.");
        link.Changed += () => dispatcher.BeginInvoke(ShowStatus);
        // Without its window and icon nobody could see it, turn it off or close it before an update, so it doesn't run.
        try { tray = new TrayIcon(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            PlaybackDiagnostics.Record("phone-host-icon-failed", new { error = ex.GetType().Name });
            remote = null; link.Dispose(); phoneLibrary.CloseStreams();
            return;
        }
        tray.Open += () => { PhoneHost.OpenApp(); };
        tray.TurnOff += TurnOff;
        tray.Ended += () => End("session");
        ShowStatus();
        if (library.Setting("phoneHostIntroduced") is null)
        {
            library.Setting("phoneHostIntroduced", "1");
            tray.Notify("Your phone can still reach BlackPearl",
                "Your phone can browse and watch this PC's library while BlackPearl is closed. Right-click this icon to turn that off.");
        }
        link.Start();
        // BlackPearl opening the library takes the phones over, so the host ends: once the room server has handed this
        // PC's id to the app, or after ten seconds. Until then the phones keep their link, so an answer just sent, such
        // as Continue on PC's, still reaches them, and they never wait with neither one there. The library is read
        // again now and then for files that changed while BlackPearl was closed, such as a drive coming back.
        long appSeenAt = 0;
        var watch = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            if (!PhoneHost.AppOpen) { appSeenAt = 0; return; }
            if (appSeenAt == 0) appSeenAt = Environment.TickCount64;
            if (remote?.ServerReady != true || Environment.TickCount64 - appSeenAt > 10000) End("app-opened");
        }, dispatcher);
        watch.Start();
        var reload = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, async (_, _) =>
        {
            try { var fresh = await Task.Run(library.All); if (!ending) items = fresh; }
            catch (Exception ex) { PlaybackDiagnostics.Record("phone-host-reload-failed", new { error = ex.GetType().Name }); }
        }, dispatcher);
        reload.Start();
        Dispatcher.Run();
        watch.Stop(); reload.Stop();
    }

    private void ShowStatus()
    {
        if (tray is null || remote is not { } link) return;
        var connected = link.Connected.Count;
        tray.Tip = connected > 0 ? $"BlackPearl: {connected} phone{(connected == 1 ? "" : "s")} connected" : "BlackPearl: your phone can reach this PC's library";
    }

    // Continue on the PC: BlackPearl opens and plays the video from where the phone was. The phone hears "done" first;
    // then the app starts, and the host ends once the app has the library.
    private Task<bool> PlayOnPc(JsonElement message, Action<string, string> fail)
    {
        if (!PhoneRemoteSettings.PhonesCanStart(library)) { fail("not-allowed", "This PC doesn't let phones start videos. Turn it on in Settings › Profile on the PC."); return Task.FromResult(false); }
        // A BlackPearl that is already opening wouldn't take the video; the phone reaches it in a moment and asks again.
        if (PhoneHost.AppOpenOrOpening) { fail("opening", "BlackPearl is opening on the PC. Try again in a moment."); return Task.FromResult(false); }
        if (phoneLibrary!.Playable(message, fail) is not { } item) return Task.FromResult(false);
        phoneLibrary.SaveHandOver(item, PhoneLibrary.Position(message));
        if (phoneLibrary.QueuePlace(message, item.Id) is ({ } fromQueue, var place)) phoneLibrary.SaveQueueHandOver(fromQueue, place);
        dispatcher.BeginInvoke(() => PhoneHost.OpenApp(item.Id), DispatcherPriority.Background);
        return Task.FromResult(true);
    }

    private void TurnOff()
    {
        library.Setting("phoneRemoteWhileClosed", "off");
        PhoneHostStartup.Sync(library);
        End("turned-off");
    }

    private void End(string reason)
    {
        if (ending) return;
        ending = true;
        PlaybackDiagnostics.Record("phone-host-end", new { reason });
        lifetime.Cancel();
        remote?.Dispose(); remote = null;
        phoneLibrary?.CloseStreams();
        tray?.Dispose();
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }
}

// The sign-in entry that starts the phone host with Windows. Only the installed app, on its own library, has one: any
// other copy (a development or portable build), a library opened with BLACKPEARL_DATA or --data-dir elsewhere, and
// builds with BLACKPEARL_PHONE_HOST=off leave it alone.
internal static class PhoneHostStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "BlackPearl phone access";
    // The installer's record of where it installed the app (packaging/BlackPearl.iss, AppId).
    private const string InstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{90B3BA58-BBC8-4993-A0EA-25C68D8B1DA6}_is1";

    private static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool Installed(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
        return key?.GetValue("InstallLocation") is string { Length: > 0 } folder && Same(folder, Path.GetDirectoryName(exe)!);
    }
    private static bool Applies => !PhoneHost.Off && Environment.ProcessPath is { } exe && Same(Program.DataDirectory, Program.DefaultDataDirectory) && Installed(exe);

    public static void Sync(LibraryStore library)
    {
        if (!Applies) return;
        var wanted = PhoneRemoteSettings.Enabled(library) && PhoneRemoteSettings.WhileClosed(library) && PhoneRemoteSettings.AtSignIn(library);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (wanted) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {PhoneHost.Argument} {PhoneHost.SignInArgument}");
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { PlaybackDiagnostics.Record("phone-host-startup-failed", new { error = ex.GetType().Name }); }
    }
}

// The phone host's icon in the notification area, with a menu to open BlackPearl or turn the host off. Its window is
// hidden and exists for the icon's messages, and so that Windows can close the host at sign-out and installers can
// close it before an update.
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 17; // WM_APP + 17
    private const int OpenCommand = 1, TurnOffCommand = 2;
    private readonly HwndSource window;
    private readonly IntPtr icon;
    private readonly int taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private string tip = "BlackPearl";
    private bool added, disposed;

    public event Action? Open, TurnOff, Ended;

    public TrayIcon()
    {
        // The menu follows Windows' dark mode, as the notification area's own menus do. Windows has no documented call
        // for that; Notepad and Terminal use this one, and older Windows simply keep the light menu.
        try { SetPreferredAppMode(1 /* AllowDark */); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
        window = new HwndSource(new HwndSourceParameters("BlackPearl phone access") { WindowStyle = 0, ExtendedWindowStyle = 0x80 /* WS_EX_TOOLWINDOW */, Width = 0, Height = 0 });
        window.AddHook(Hook);
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        var icons = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && PrivateExtractIcons(exe, 0, size, size, icons, null, 1, 0) > 0) icon = icons[0];
        Add();
    }

    public string Tip
    {
        get => tip;
        set { tip = value.Length > 127 ? value[..127] : value; if (added) Modify(NifTip | NifShowTip); }
    }

    // A one-time notice beside the icon.
    public void Notify(string title, string text)
    {
        var data = Data(NifInfo);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = 4; // NIIF_USER: the icon itself
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    private const int NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10, NifShowTip = 0x80;

    private NotifyIconData Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = window.Handle, uID = 1, uFlags = flags, uCallbackMessage = CallbackMessage, hIcon = icon,
        szTip = tip, szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        added = Shell_NotifyIcon(0 /* NIM_ADD */, ref data);
        data.uVersion = 4; // NOTIFYICON_VERSION_4
        if (added) Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref data);
    }

    private void Modify(int flags)
    {
        var data = Data(flags);
        Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            var kind = (int)(lParam.ToInt64() & 0xFFFF);
            // WM_CONTEXTMENU, then NIN_SELECT and NIN_KEYSELECT (a click, Enter or Space). A double click sends a select
            // as well, so it isn't handled on its own.
            if (kind == 0x7B) ShowMenu((short)(wParam.ToInt64() & 0xFFFF), (short)((wParam.ToInt64() >> 16) & 0xFFFF));
            else if (kind is 0x400 or 0x401) Open?.Invoke();
            handled = true;
        }
        else if (message == taskbarCreated) Add(); // Explorer restarted and lost the icon.
        else if (message == 0x11 /* WM_QUERYENDSESSION */) { handled = true; return 1; }
        else if (message == 0x16 /* WM_ENDSESSION */ && wParam != IntPtr.Zero || message == 0x10 /* WM_CLOSE */) { handled = true; Ended?.Invoke(); }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, 1 /* MF_GRAYED */, 0, "Your phone can browse and watch this PC's library");
            AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
            AppendMenu(menu, 0, OpenCommand, "Open BlackPearl");
            AppendMenu(menu, 0, TurnOffCommand, "Turn off while BlackPearl is closed");
            SetMenuDefaultItem(menu, OpenCommand, 0);
            // The menu closes when clicked elsewhere only if its window is in the foreground.
            SetForegroundWindow(window.Handle);
            var chosen = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, x, y, window.Handle, IntPtr.Zero);
            PostMessage(window.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen == OpenCommand) Open?.Invoke();
            else if (chosen == TurnOffCommand) TurnOff?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (added) { var data = Data(0); Shell_NotifyIcon(2 /* NIM_DELETE */, ref data); added = false; }
        window.Dispose();
        if (icon != IntPtr.Zero) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[]? ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
