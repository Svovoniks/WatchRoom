using System.Security.Cryptography;
using System.Text;

namespace Watchroom.Core;

public record PersistentRoom(string Code, string HostKeyHash, string[]? ApprovedGuests = null, bool SharedControls = false, AdmittedGuest[]? Guests = null);
public sealed class PersistentRooms
{
    private readonly LibraryStore store;
    private readonly List<PersistentRoom> rooms;
    private readonly object gate = new();
    public PersistentRooms(string directory)
    {
        store = new(directory);
        rooms = Wire.Read<List<PersistentRoom>>(store.Setting("persistentRooms") ?? "[]");
    }
    public PersistentRoom[] All() { lock (gate) return rooms.ToArray(); }
    public void Update(string code, AdmittedGuest[] guests, bool sharedControls)
    {
        lock (gate)
        {
            var index = rooms.FindIndex(x => x.Code == code);
            if (index < 0) return;
            rooms[index] = rooms[index] with { ApprovedGuests = guests.Select(x => x.Id).ToArray(), Guests = guests, SharedControls = sharedControls };
            store.Setting("persistentRooms", Wire.Serialize(rooms));
        }
    }
    public string Create(string code)
    {
        lock (gate)
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            rooms.Add(new(code, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))));
            store.Setting("persistentRooms", Wire.Serialize(rooms));
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
