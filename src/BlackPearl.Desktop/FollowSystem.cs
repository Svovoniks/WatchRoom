using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using BlackPearl.Core;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BlackPearl.Desktop;

// What auto-download registers with Windows so it works while BlackPearl is closed, on Windows 10 (1809 and later) and 11:
// - an hourly Task Scheduler task for the current user that runs "BlackPearl.exe --check-episodes";
// - a notification identity (AppUserModelId in the registry), so a process with no window can show notifications;
// - a link scheme that notification buttons open, which starts BlackPearl with the button's action and token.
// Nothing here needs administrator rights, and nothing touches WPF: the background check runs without loading it.
// A data folder other than the usual one gets its own task, identity and scheme, so a test library never takes over the
// real one's. BLACKPEARL_FOLLOW_SYSTEM=off turns all of it off for checks.
internal static class FollowSystem
{
    private static string Folder(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
    private static bool Usual => Folder(Program.DataDirectory).Equals(Folder(Program.DefaultDataDirectory), StringComparison.OrdinalIgnoreCase);
    private static string Suffix => LibraryIdentity.Hash(Folder(Program.DataDirectory).ToUpperInvariant())[..8].ToLowerInvariant();
    public static string Scheme => Usual ? "blackpearl-downloads" : "blackpearl-downloads-" + Suffix;
    public static string AppId => Usual ? "Svovoniks.BlackPearl.Downloads" : "Svovoniks.BlackPearl.Downloads." + Suffix;
    public static string TaskName => Usual ? "BlackPearl episode check" : "BlackPearl episode check " + Suffix;
    public static bool Off => AppEnvironment.Get("FOLLOW_SYSTEM") is { } value && value.Equals("off", StringComparison.OrdinalIgnoreCase);
    // The program the task and links start: this one, or the app a probe registers for.
    internal static string? ExeOverride { get; set; }
    internal static string Exe => ExeOverride ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BlackPearl.exe");
    internal static string DataArgument => $"--data-dir \"{Folder(Program.DataDirectory)}\"";
    private static string Icon => Path.Combine(AppContext.BaseDirectory, "Assets", "BlackPearl.png");
    private static string SchemeKey => @"Software\Classes\" + Scheme;
    private static string IdentityKey => @"Software\Classes\AppUserModelId\" + AppId;
    internal static string CheckArguments => $"--check-episodes --data-dir \"{Folder(Program.DataDirectory)}\"";

    // Registers everything for the running copy of BlackPearl. Returns what went wrong, or null.
    public static string? Register()
    {
        if (Off) return null;
        try
        {
            RegisterIdentity();
            using (var scheme = Registry.CurrentUser.CreateSubKey(SchemeKey))
            {
                scheme.SetValue("", "URL:BlackPearl downloads"); scheme.SetValue("URL Protocol", "");
                using var icon = scheme.CreateSubKey("DefaultIcon"); icon.SetValue("", $"\"{Exe}\",0");
                using var command = scheme.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{Exe}\" --data-dir \"{Folder(Program.DataDirectory)}\" \"%1\"");
            }
            var file = Path.Combine(Path.GetTempPath(), $"blackpearl-task-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(file, FollowNotifications.TaskXml(WindowsIdentity.GetCurrent().Name, Exe, CheckArguments, DateTime.Now.AddMinutes(1)), Encoding.Unicode);
                var (code, output) = Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
                if (code != 0) return "Windows didn't accept the hourly check: " + output;
            }
            finally { try { File.Delete(file); } catch (IOException) { } }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return "Couldn't set up the hourly check: " + ex.Message; }
    }
    private static void RegisterIdentity()
    {
        using var identity = Registry.CurrentUser.CreateSubKey(IdentityKey);
        identity.SetValue("DisplayName", "BlackPearl");
        if (File.Exists(Icon)) identity.SetValue("IconUri", Icon);
    }
    public static void Unregister()
    {
        if (Off) return;
        try { Schtasks("/Delete", "/TN", TaskName, "/F"); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        foreach (var key in new[] { SchemeKey, IdentityKey })
            try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
    // The program the hourly task runs, or null when there's no task.
    public static string? TaskCommand()
    {
        if (Off) return null;
        try
        {
            var (code, output) = Schtasks("/Query", "/TN", TaskName, "/XML");
            if (code != 0) return null;
            var ns = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");
            return XDocument.Parse(output).Descendants(ns + "Command").FirstOrDefault()?.Value;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }
    // At startup, while auto-download is on: a task that is gone, or that runs a BlackPearl.exe that no longer exists (an
    // installation that moved), is registered again for this copy. A task that runs another copy that still exists, such
    // as the installed app while a development build runs, is left alone.
    public static string? Repair()
    {
        if (Off) return null;
        var command = TaskCommand();
        var link = (Registry.CurrentUser.OpenSubKey(SchemeKey + @"\shell\open\command")?.GetValue("") as string) ?? "";
        var linkExe = link.StartsWith('"') && link.IndexOf('"', 1) is > 1 and var end ? link[1..end] : null;
        return command is null || !File.Exists(command) || linkExe is null || !File.Exists(linkExe) ? Register() : null;
    }
    private static (int Code, string Output) Schtasks(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks didn't start.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { try { process.Kill(); } catch (InvalidOperationException) { } return (-1, "schtasks didn't finish."); }
        return (process.ExitCode, (output.Result + errors.Result).Trim());
    }

    // Why notifications wouldn't show, or null when they will. Focus assist can still hold them back; they then wait in
    // the notification center, and Settings > Downloads lists them either way.
    public static string? NotificationProblem()
    {
        if (Off) return null;
        try
        {
            RegisterIdentity();
            return ToastNotificationManager.CreateToastNotifier(AppId).Setting switch
            {
                NotificationSetting.Enabled => null,
                NotificationSetting.DisabledForApplication => "Windows notifications are turned off for BlackPearl.",
                NotificationSetting.DisabledForUser => "Windows notifications are turned off on this computer.",
                NotificationSetting.DisabledByGroupPolicy => "Your organization's policy turns notifications off.",
                _ => "Windows can't show BlackPearl's notifications."
            };
        }
        catch (Exception ex) { PlaybackDiagnostics.Record("follow-notification-setting-failed", new { error = ex.GetType().Name }); return null; }
    }
    public static void Show(FollowNotice? notice)
    {
        if (notice is null || Off) return;
        try
        {
            RegisterIdentity();
            var xml = new XmlDocument(); xml.LoadXml(FollowNotifications.ToastXml(notice, Scheme));
            var toast = new ToastNotification(xml) { Tag = notice.Token, Group = "follow" };
            if (notice.Kind != "ready") toast.ExpirationTime = DateTimeOffset.Now.AddDays(1);
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex) { PlaybackDiagnostics.Record("follow-notification-failed", new { kind = notice.Kind, error = ex.GetType().Name, hresult = ex.HResult }); }
    }
    // Takes a notification away once its episodes were approved or skipped somewhere else.
    public static void Remove(string? token)
    {
        if (token is null || Off) return;
        try { ToastNotificationManager.History.Remove(token, "follow", AppId); }
        catch (Exception ex) { PlaybackDiagnostics.Record("follow-notification-remove-failed", new { error = ex.GetType().Name }); }
    }
}

// The background side of auto-download: the hourly check, and the buttons of its notifications. Both run in a process
// with no window and exit when done; Review opens the app at Settings > Downloads instead.
internal static class BackgroundFollow
{
    private static LibraryStore Library() => new(Program.DataDirectory, deferRetention: true);
    internal static EpisodeFollowing Engine(LibraryStore library, IReadOnlyList<AcquisitionPlugin>? plugins = null, string? token = null)
    {
        plugins ??= ContentAcquisition.Discover(Program.PluginsDirectory).Plugins;
        if (token is null)
        {
            try { token = MetadataCredential.Load(Program.DataDirectory); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException) { token = ""; }
        }
        return new EpisodeFollowing(library, plugins, (member, ct) => EpisodeFollowing.ProviderSeasonsAsync(library, member, token, ct));
    }
    private static void Start()
    {
        Directory.CreateDirectory(Program.DataDirectory);
        PlaybackDiagnostics.Initialize(Path.Combine(Program.DataDirectory, "logs"));
    }
    private static void Fail(string stage, Exception ex)
    {
        PlaybackDiagnostics.Record("follow-background-failed", new { stage, error = ex.GetType().Name });
        try { File.AppendAllText(Path.Combine(Program.DataDirectory, "errors.log"), DateTime.UtcNow + " auto-download " + stage + ": " + ex + Environment.NewLine); } catch (IOException) { }
        try { PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }

    // --check-episodes: one check, its notifications, and its countdown when the user chose one.
    public static void Check()
    {
        Start();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(50));
        try { CheckAsync(lifetime.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("check", ex); return; }
        try { PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
    internal static async Task CheckAsync(CancellationToken ct)
    {
        var library = Library();
        if (!FollowStore.Settings(library).Enabled) return;
        var engine = Engine(library);
        // A countdown that ran out while no check was running.
        await Approve(engine, engine.DueCountdowns(), ct);
        var result = await engine.CheckAsync(ct);
        PlaybackDiagnostics.Record("follow-check", new { result.Summary });
        FollowSystem.Show(result.Ready); FollowSystem.Show(result.Attention);
        DesktopIcon.Sync(engine);
        if (result.Ready is { StartAt: > 0 } countdown) await Countdown(engine, countdown, ct);
    }
    // Waits out a countdown, then downloads whatever the user didn't skip or download already.
    internal static async Task Countdown(EpisodeFollowing engine, FollowNotice notice, CancellationToken ct)
    {
        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < notice.StartAt)
        {
            if (engine.WithToken(notice.Token).Length == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        await Approve(engine, engine.WithToken(notice.Token), ct, notice.Token);
    }
    internal static async Task Approve(EpisodeFollowing engine, FollowEpisodeRef[] episodes, CancellationToken ct, string? token = null)
    {
        if (episodes.Length == 0) return;
        var outcomes = await engine.ApproveAsync(episodes, ct);
        FollowSystem.Remove(token);
        FollowSystem.Show(EpisodeFollowing.OutcomeNotice(outcomes));
        DesktopIcon.Sync(engine);
    }

    // A notification button: Download and Skip run here; Review returns false so the app opens at Settings > Downloads.
    // A link that isn't ours, or a token with nothing waiting (already handled, or made up), does nothing.
    public static bool Activate(string link)
    {
        var parsed = FollowNotifications.ParseLink(FollowSystem.Scheme, link);
        if (parsed is null) return true;
        var (action, token) = parsed.Value;
        if (action == "open") return false;
        Start();
        try
        {
            var library = Library();
            var engine = Engine(library);
            if (action == "desktop")
            {
                // The desktop icon: exactly the episodes it listed that still wait for the user.
                var waiting = engine.Pending().Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToHashSet();
                var listed = DesktopIcon.Episodes(library, token!).Where(waiting.Contains).ToArray();
                if (listed.Length == 0)
                {
                    FollowSystem.Show(new FollowNotice("outcome", EpisodeFollowing.NewToken(), "Nothing to download", ["These episodes were already downloaded or skipped."], null, 0, []));
                    DesktopIcon.Sync(engine);
                    return true;
                }
                var notices = engine.Pending().Where(x => listed.Contains(new(x.Show.Key, x.Episode.Season, x.Episode.Episode))).Select(x => x.Episode.Token).OfType<string>().Distinct().ToArray();
                using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                Approve(engine, listed, lifetime.Token).GetAwaiter().GetResult();
                // Their notifications go away once nothing they list still waits.
                foreach (var notice in notices.Where(x => engine.WithToken(x).Length == 0)) FollowSystem.Remove(notice);
                PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2));
                return true;
            }
            var episodes = engine.WithToken(token!);
            if (episodes.Length == 0) { FollowSystem.Remove(token); return true; }
            if (action == "skip") { engine.Skip(episodes); FollowSystem.Remove(token); DesktopIcon.Sync(engine); }
            else
            {
                using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                Approve(engine, episodes, lifetime.Token, token).GetAwaiter().GetResult();
            }
            PlaybackDiagnostics.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { Fail(action, ex); }
        return true;
    }
}
