namespace Watchroom.Core;

public static class QueuePlayback
{
    public static int FindNext<T>(IReadOnlyList<T> entries, int position, int direction, Func<T, bool> available)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        for (var index = position + direction; index >= 0 && index < entries.Count; index += direction)
            if (available(entries[index])) return index;
        return -1;
    }
}
