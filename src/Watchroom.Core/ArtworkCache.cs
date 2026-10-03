using System.Collections.Concurrent;

namespace Watchroom.Core;

public static class ArtworkCache
{
    private static readonly ConcurrentDictionary<string, (long Length, long Modified, bool Valid)> Checks = new(StringComparer.OrdinalIgnoreCase);
    public static bool IsUsable(string? path)
    {
        if (path is null) return false;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 8) return false;
            var modified = file.LastWriteTimeUtc.Ticks;
            if (Checks.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == modified) return cached.Valid;
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[8]; var read = stream.Read(header);
            var valid = read == 8 && (header[0] == 255 && header[1] == 216 || header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            if (Checks.Count > 8192) Checks.Clear();
            Checks[path] = (file.Length, modified, valid); return valid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
