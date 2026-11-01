using System.Security.Cryptography;
using System.Text;

namespace BlackPearl.Core;

public record PersistentRoom(string Code, string HostKeyHash, string[]? ApprovedGuests = null, bool SharedControls = false, AdmittedGuest[]? Guests = null, string? Name = null);
public sealed class PersistentRooms
{
    private readonly LibraryStore store;
    private readonly List<PersistentRoom> rooms;
    private readonly object gate = new();
    // Each room is its own settings row, so a change rewrites one room instead of every saved room.
    private const string Prefix = "persistentRoom:";
    public PersistentRooms(string directory)
    {
        store = new(directory);
        rooms = store.SettingsWithPrefix(Prefix).Values.Select(Wire.Read<PersistentRoom>).ToList();
        // Move rooms saved by older servers as one list into their own rows.
        var legacy = Wire.Read<List<PersistentRoom>>(store.Setting("persistentRooms") ?? "[]");
        if (legacy.Count > 0)
        {
            var known = rooms.Select(x => x.Code).ToHashSet();
            var moved = legacy.Where(x => known.Add(x.Code)).ToArray();
            store.SetSettings(moved.Select(room => KeyValuePair.Create(Prefix + room.Code, Wire.Serialize(room))));
            rooms.AddRange(moved);
            store.Setting("persistentRooms", "[]");
        }
    }
    private void Save(PersistentRoom room) => store.Setting(Prefix + room.Code, Wire.Serialize(room));
    public PersistentRoom[] All() { lock (gate) return rooms.ToArray(); }
    public int Count { get { lock (gate) return rooms.Count; } }
    public void Update(string code, AdmittedGuest[] guests, bool sharedControls, string? name = null)
    {
        lock (gate)
        {
            var index = rooms.FindIndex(x => x.Code == code);
            if (index < 0) return;
            rooms[index] = rooms[index] with { ApprovedGuests = guests.Select(x => x.Id).ToArray(), Guests = guests, SharedControls = sharedControls, Name = name ?? rooms[index].Name };
            Save(rooms[index]);
        }
    }
    public string Create(string code)
    {
        lock (gate)
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            rooms.Add(new(code, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))));
            Save(rooms[^1]);
            return key;
        }
    }
    public bool Verify(string code, string? key)
    {
        if (key is null || key.Length != 64) return false;
        lock (gate)
        {
            var saved = rooms.FirstOrDefault(x => x.Code == code);
            return saved is not null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(saved.HostKeyHash), SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
    }
}
