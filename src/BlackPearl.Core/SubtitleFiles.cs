using System.Text;

namespace BlackPearl.Core;

// Subtitle files beside a video: the language their names give, and which of them VLC loads by itself.
public static class SubtitleFiles
{
    // Words in subtitle file names that describe the track rather than name its language.
    private static readonly HashSet<string> Descriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "sdh", "cc", "hi", "default", "full", "signs", "songs", "sub", "subs", "subtitle", "subtitles", "dialog", "dialogue", "commentary", "foreign"
    };

    /// <summary>
    /// The language a subtitle file name ends with, as in "Movie.ru.srt", "Movie.en.forced.srt" or "Movie_pt-BR.srt".
    /// Only the last word that isn't a descriptor counts, and never the first, which is the title.
    /// </summary>
    public static string? Language(string fileName)
    {
        var words = Path.GetFileNameWithoutExtension(fileName).Split('.', '_');
        for (var i = words.Length - 1; i >= 1; i--)
        {
            var word = words[i].Trim();
            if (word.Length == 0 || Descriptors.Contains(word) || word.All(char.IsAsciiDigit)) continue;
            return TrackLanguages.Normalize(word);
        }
        return null;
    }

    // VLC 3's subtitle file types (src/input/subtitles.c).
    private static readonly HashSet<string> VlcExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".idx", ".sub", ".srt", ".ssa", ".ass", ".smi", ".utf", ".utf8", ".utf-8", ".rt", ".aqt", ".txt", ".usf", ".jss", ".cdg", ".psb",
        ".mpsub", ".mpl2", ".pjs", ".dks", ".stl", ".vtt", ".sbv", ".ttml"
    };

    /// <summary>
    /// The subtitle files VLC loads by itself for a local video, as its default autodetection does: beside the video or
    /// in a Subtitles or Subs folder, named as the video or starting with its name, ignoring case and punctuation.
    /// A .sub is skipped when an .idx of the same name holds it.
    /// </summary>
    public static IReadOnlyList<string> VlcSidecars(string videoPath)
    {
        var found = new List<string>();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(videoPath));
            if (directory is null) return found;
            var video = Simplify(Path.GetFileNameWithoutExtension(videoPath));
            foreach (var folder in new[] { directory, Path.Combine(directory, "Subtitles"), Path.Combine(directory, "Subs") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var extension = Path.GetExtension(file);
                    if (!VlcExtensions.Contains(extension) || !Simplify(Path.GetFileNameWithoutExtension(file)).StartsWith(video, StringComparison.Ordinal)) continue;
                    if (extension.Equals(".sub", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(file, ".idx"))) continue;
                    if (!found.Contains(file, StringComparer.OrdinalIgnoreCase)) found.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { found.Clear(); }
        return found;
    }

    /// <summary>
    /// Whether VLC names no language for a subtitle file it loaded by itself. It takes whatever follows the name's last
    /// dot ("Movie.ru.srt" is "ru", "Movie.en.forced.srt" is "forced"), and players read that as a language or not at all.
    /// </summary>
    public static bool VlcUntagged(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dot = name.LastIndexOf('.');
        return dot < 0 || TrackLanguages.Normalize(name[(dot + 1)..]) is null;
    }

    // VLC compares names lowercased, with every run of non-alphanumeric characters as one space.
    private static string Simplify(string name)
    {
        var simple = new StringBuilder(name.Length);
        var gap = false;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c)) { gap = true; continue; }
            if (gap && simple.Length > 0) simple.Append(' ');
            simple.Append(char.ToLowerInvariant(c)); gap = false;
        }
        return simple.ToString();
    }
}
