using System.Diagnostics;
using System.IO;
using BlackPearl.Core;
using BlackPearl.Desktop;
using Microsoft.Win32;
using Windows.UI.Notifications;

// Manual end-to-end probe of auto-download's Windows side, with a built BlackPearl.exe and a scratch library:
//   dotnet run --project tests/BlackPearl.DesktopChecks -- --follow-system-probe <path to BlackPearl.exe>
// It registers the hourly task, link and notification identity for the scratch library (their names carry its folder's
// hash, so the real ones are never touched), runs the task through Task Scheduler, checks the notification it shows,
// presses Download by opening the notification's link through the shell, and removes everything again.
static class FollowSystemProbe
{
    public static void Run(string exe)
    {
        exe = Path.GetFullPath(exe);
        if (!File.Exists(exe)) throw new FileNotFoundException("BlackPearl.exe not found", exe);
        var data = Path.Combine(Path.GetTempPath(), "blackpearl-follow-probe-" + Guid.NewGuid().ToString("N")[..8]);
        BlackPearl.Desktop.Program.DataArgument(["--data-dir", data]);
        FollowSystem.ExeOverride = exe;
        var checks = 0; var failed = false;
        void Check(bool condition, string name) { if (!condition) { failed = true; throw new Exception("FAIL: " + name + " (scratch library kept in " + data + ")"); } Console.WriteLine("PASS: " + name); checks++; }
        bool Wait(Func<bool> done, int seconds) { for (var until = DateTime.UtcNow.AddSeconds(seconds); DateTime.UtcNow < until; Thread.Sleep(500)) if (done()) return true; return done(); }
        try
        {
            // A scratch library: a show with one file, followed, whose next episode aired yesterday, and a plugin that lists
            // one release per episode and logs what it's asked.
            var store = new LibraryStore(data);
            var shows = Path.Combine(data, "Shows"); var folder = Path.Combine(shows, "Probe Show"); Directory.CreateDirectory(folder);
            store.Setting("folders", Wire.Serialize(new[] { new LibraryFolder(shows, "Show") }));
            var file = new MediaItem("probe-1", Path.Combine(folder, "Probe Show S01E01.mkv"), "Probe Show", "Show", 2024, "Probe Show", 1, 1, ShowId: "show-probe", SeriesPath: folder, MetadataProvider: "tmdb", MetadataId: 1);
            store.Save(file);
            var plugin = Path.Combine(data, "plugins", "probe"); Directory.CreateDirectory(plugin);
            File.WriteAllText(Path.Combine(plugin, "plugin.json"), """{ "id": "probe", "name": "Probe", "contentTypes": ["show"], "command": "probe.ps1", "interactive": true }""");
            File.WriteAllText(Path.Combine(plugin, "probe.ps1"), """
                $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
                Add-Content -Path (Join-Path $env:BLACKPEARL_PLUGIN_DIR 'log.txt') -Value ($request.action + ' ' + (@($request.selectedReleases) -join ','))
                if ($request.action -in 'search', 'releases') {
                  $choices = @($request.episodes | ForEach-Object { @{ id = "probe-release-$($_.episode)"; title = "Probe.Show.S01E0$($_.episode).1080p-PROBE"; matches = $true; episode = "S01E0$($_.episode)" } })
                  @{ success = $true; stage = 'releases'; message = 'Choose'; choices = $choices } | ConvertTo-Json -Depth 5 -Compress
                } elseif ($request.action -eq 'acquire') { '{"success":true,"message":"Probe queued it"}' }
                else { '{"success":false,"message":"unexpected"}' }
                """);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            FollowStore.SaveSettings(store, new FollowSettings(Enabled: true));
            FollowStore.Follow(store, file, "probe", new AcquisitionOptions("Probe Show", "probe-page"), null, today.AddDays(-3));
            FollowStore.Update(store, "show-probe", x => x with { Listing = [new(1, 1, today.AddDays(-10).ToString("yyyy-MM-dd")), new(1, 2, today.AddDays(-1).ToString("yyyy-MM-dd"))], ListingProvider = "tmdb", ListedAt = Wire.Now });

            Check(FollowSystem.Register() is null, "the hourly task, link and notification identity register without administrator rights");
            Check(string.Equals(FollowSystem.TaskCommand(), exe, StringComparison.OrdinalIgnoreCase), "the task runs the BlackPearl.exe it was registered for");
            var link = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + FollowSystem.Scheme + @"\shell\open\command")?.GetValue("") as string;
            Check(link == $"\"{exe}\" --data-dir \"{data}\" \"%1\"", "the link starts BlackPearl for this library");
            Check(FollowSystem.Repair() is null && FollowSystem.NotificationProblem() is null, "a registration that's in place needs no repair, and Windows will show its notifications");

            // Task Scheduler runs the check: the episode's release waits for the user, and a notification says so.
            var run = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Run /TN \"{FollowSystem.TaskName}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            run.WaitForExit();
            FollowEpisode? Episode() => FollowStore.Show(new LibraryStore(data, deferRetention: true), "show-probe")?.Episodes.FirstOrDefault(x => x.Episode == 2);
            Check(Wait(() => Episode()?.Status == FollowStatus.Pending, 120), "the scheduled check finds the new episode's release and waits for the user");
            var token = Episode()!.Token!;
            Check(File.ReadAllLines(Path.Combine(plugin, "log.txt")).All(x => x.Trim() is "search" or "releases"), "the scheduled check only searched and listed releases");
            Check(Wait(() => ToastNotificationManager.History.GetHistory(FollowSystem.AppId).Any(x => x.Tag == token), 20), "the check shows a notification for it");

            // A made-up token from a link does nothing; the notification's own Download link downloads the release shown.
            Process.Start(new ProcessStartInfo($"{FollowSystem.Scheme}:approve?token={EpisodeFollowing.NewToken()}") { UseShellExecute = true });
            Thread.Sleep(5000);
            Check(Episode()?.Status == FollowStatus.Pending, "a link with a token that isn't waiting downloads nothing");
            Process.Start(new ProcessStartInfo($"{FollowSystem.Scheme}:approve?token={token}") { UseShellExecute = true });
            Check(Wait(() => Episode()?.Status == FollowStatus.Sent, 60), "the notification's Download link sends the episode to the plugin");
            Check(File.ReadAllLines(Path.Combine(plugin, "log.txt")).Last() == "acquire probe-release-2", "only the release the user was shown is downloaded");
            Check(Wait(() => ToastNotificationManager.History.GetHistory(FollowSystem.AppId).All(x => x.Tag != token), 10), "the answered notification goes away");
        }
        finally
        {
            foreach (var toast in ToastNotificationManager.History.GetHistory(FollowSystem.AppId)) FollowSystem.Remove(toast.Tag);
            FollowSystem.Unregister();
            Console.WriteLine(FollowSystem.TaskCommand() is null && Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + FollowSystem.Scheme) is null
                ? "PASS: the task, link and identity are removed again" : "FAIL: registration left behind");
            if (!failed) try { Directory.Delete(data, true); } catch (IOException) { }
        }
        Console.WriteLine($"{checks} auto-download system checks passed.");
    }
}
