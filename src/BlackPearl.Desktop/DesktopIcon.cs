using System.IO;
using System.Runtime.InteropServices;
using BlackPearl.Core;

namespace BlackPearl.Desktop;

// An icon on the desktop while episodes wait for the user's OK, when Settings > Downloads asks for one. Its name says how
// many, its tooltip lists them, and opening it downloads exactly those, like a notification's Download. It changes or
// goes away as soon as anything downloads or skips them, or auto-download or the option is turned off. Like the rest of
// auto-download's background side it never touches WPF. BLACKPEARL_DESKTOP puts it in another folder, for checks.
internal static class DesktopIcon
{
    private const string StateKey = "follow-desktop";
    private sealed record State(string Token, string Path, FollowEpisodeRef[] Episodes);
    private static string Folder => AppEnvironment.Get("DESKTOP") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static State? Read(LibraryStore library)
    {
        try { return library.Setting(StateKey) is { Length: > 0 } json ? Wire.Read<State>(json) : null; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { return null; }
    }
    // The episodes the icon stands for, when the token is the icon's own.
    public static FollowEpisodeRef[] Episodes(LibraryStore library, string token) => Read(library) is { } state && state.Token == token ? state.Episodes : [];
    public static string? CurrentPath(LibraryStore library) => Read(library)?.Path;
    // "3 new episodes - BlackPearl"; a test library's icon carries its library's suffix so it never replaces the real one.
    internal static string Name(int count) => $"{count} new {(count == 1 ? "episode" : "episodes")} - BlackPearl" +
        (FollowSystem.Scheme == "blackpearl-downloads" ? "" : " " + FollowSystem.Scheme[^8..]) + ".lnk";

    // Puts the icon up for the episodes waiting now, or takes it down when there are none or the option is off.
    public static void Sync(EpisodeFollowing engine)
    {
        var library = engine.Library;
        try
        {
            using var gate = FollowLock.State(library);
            var settings = FollowStore.Settings(library);
            var pending = settings.Enabled && settings.DesktopIcon ? engine.Pending().ToArray() : [];
            var episodes = pending.Select(x => new FollowEpisodeRef(x.Show.Key, x.Episode.Season, x.Episode.Episode)).ToArray();
            var state = Read(library);
            if (state is not null && episodes.Length > 0 && state.Episodes.ToHashSet().SetEquals(episodes) && File.Exists(state.Path)) return;
            if (state is not null)
            {
                try { File.Delete(state.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                library.Setting(StateKey, "");
            }
            if (episodes.Length == 0) return;
            var token = EpisodeFollowing.NewToken();
            var path = Path.Combine(Folder, Name(episodes.Length));
            var names = pending.Take(4).Select(x => $"{x.Show.Title} {x.Episode.Code}").ToList();
            if (pending.Length > 4) names.Add($"and {pending.Length - 4} more");
            var description = "Download " + string.Join(", ", names);
            Write(path, FollowSystem.Exe, FollowSystem.DataArgument + " " + FollowNotifications.Link(FollowSystem.Scheme, "desktop", token),
                description.Length > 250 ? description[..250] : description);
            library.Setting(StateKey, Wire.Serialize(new State(token, path, episodes)));
        }
        catch (Exception ex) { PlaybackDiagnostics.Record("follow-desktop-icon-failed", new { error = ex.GetType().Name, hresult = ex.HResult }); }
    }
    private static void Write(string path, string exe, string arguments, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try
            {
                link.TargetPath = exe; link.Arguments = arguments; link.WorkingDirectory = Path.GetDirectoryName(exe);
                // Its own picture, the pearl with a download badge, beside the app; the app's icon if that file is missing.
                var icon = Path.Combine(Path.GetDirectoryName(exe)!, "Assets", "NewEpisodes.ico");
                link.IconLocation = (File.Exists(icon) ? icon : exe) + ",0"; link.Description = description; link.Save();
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }
}
