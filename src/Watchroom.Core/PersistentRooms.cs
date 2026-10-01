using System.Security.Cryptography;
using System.Text;

namespace Watchroom.Core;

public record PersistentRoom(string Code, string HostKeyHash);
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
