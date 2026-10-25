using System.Security.Cryptography;
using System.Text;

namespace Watchroom.Core;

// The host's saved queues as guests see and edit them on their Queues page. The host sends its whole queue state;
// guests send small edit requests back, and the host applies them to its own queues.
public record SharedQueueMedia(string Id, string Title, string Kind, int? Year = null, string? Series = null, int? Season = null,
    int? Episode = null, int? EpisodeEnd = null, string? EpisodeTitle = null, int? RuntimeMinutes = null,
    bool Available = true, bool IsVirtual = false, bool IsBonus = false);
public record SharedQueue(string Id, string Name, string[] MediaIds);
// ActiveIndex is the entry of the active queue that is playing, or -1.
public record SharedQueues(SharedQueue[] Queues, SharedQueueMedia[] Media, string? ActiveId = null, int ActiveIndex = -1);
// Order keeps the listed entries in that order, which also removes the rest; Add appends videos; Create starts a queue.
// Fingerprint is the queue an Order edit was made against, so an edit never lands on a queue that changed meanwhile.
public record QueueEdit(string Action, string? QueueId = null, string? Fingerprint = null, int[]? Order = null, string[]? MediaIds = null, string? Name = null);

public static class QueueSharing
{
    public const string StateMessage = "library-queues", EditMessage = "library-queue-edit", ErrorMessage = "library-queue-error";
    // Room control messages are limited to 64 KiB, so the state travels in chunks well below that.
    public const int ChunkChars = 40000, MaxChunks = 80, MaxName = 80, MaxAdd = 500, MaxEntries = 20000;

    public static string Fingerprint(IEnumerable<string> mediaIds) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', mediaIds))), 0, 8);

    // Base64 keeps every chunk's size exact; JSON escaping would otherwise grow non-Latin titles several times over.
    public static WireMessage[] Messages(long revision, SharedQueues state)
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wire.Serialize(state)));
        var count = Math.Max(1, (data.Length + ChunkChars - 1) / ChunkChars);
        if (count > MaxChunks) throw new InvalidDataException("These queues are too large to share with the room.");
        return Enumerable.Range(0, count).Select(index => new WireMessage(StateMessage,
            Text: data.Substring(index * ChunkChars, Math.Min(ChunkChars, data.Length - index * ChunkChars)), Data: $"{index}/{count}", Number: revision)).ToArray();
    }

    // Returns why an edit is malformed, or null; whether it still fits the host's queues is the host's check.
    public static string? Problem(QueueEdit edit) => edit.Action switch
    {
        "order" => ValidId(edit.QueueId) && edit.Fingerprint is { Length: 16 } && edit.Order is { Length: <= MaxEntries } ? null : "That queue change was not understood.",
        "add" => ValidId(edit.QueueId) && edit.MediaIds is { Length: > 0 and <= MaxAdd } && edit.MediaIds.All(ValidId) ? null : "Those videos could not be added.",
        "create" => edit.Name?.Trim() is { Length: > 0 and <= MaxName } name && !name.Any(char.IsControl) && (edit.MediaIds ?? []).Length <= MaxAdd && (edit.MediaIds ?? []).All(ValidId)
            ? null : $"Queue names need 1 to {MaxName} characters.",
        _ => "That queue change was not understood.",
    };
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 128 };
}

// Joins the chunks of one queue state. A newer revision replaces a partly received older one; older chunks are ignored.
public sealed class QueueStateAssembler
{
    private long revision = long.MinValue;
    private string?[] parts = [];

    public SharedQueues? Add(WireMessage message)
    {
        var slash = message.Data?.IndexOf('/') ?? -1;
        if (slash <= 0 || !int.TryParse(message.Data![..slash], out var index) || !int.TryParse(message.Data[(slash + 1)..], out var count) ||
            count is < 1 or > QueueSharing.MaxChunks || index < 0 || index >= count || message.Text is not { Length: > 0 and <= QueueSharing.ChunkChars } text) return null;
        if (message.Number < revision) return null;
        if (message.Number != revision || parts.Length != count) { revision = message.Number; parts = new string?[count]; }
        parts[index] = text;
        if (parts.Any(part => part is null)) return null;
        var data = string.Concat(parts);
        parts = [];
        return Wire.Read<SharedQueues>(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
    }
}
