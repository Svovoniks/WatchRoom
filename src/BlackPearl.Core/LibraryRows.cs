using Microsoft.Data.Sqlite;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackPearl.Core;

// The whole catalog is read many times an hour, and several times in a row at launch and during a scan, while few of
// its rows change in between. Turning every row's JSON into new records each time cost about 200 MB of short-lived
// memory per read of a 6,000-file library, most of which outlived the read and stayed allocated while the app sat idle.
// A row whose stored bytes are unchanged now gives back the record read from it before, so a read allocates only for
// the rows that changed, and every reader shares one copy of each record. Records and their collections are never
// changed in place, which is what makes sharing them safe.
public sealed partial class LibraryStore
{
    private readonly record struct ReadItem(MediaItem Item, LibraryShow? Show, LibrarySeason? Season);
    private readonly object rowGate = new();
    private Dictionary<UInt128, ReadItem> readItems = [];
    private Dictionary<UInt128, LibraryShow> readShows = [];
    private Dictionary<UInt128, LibrarySeason> readSeasons = [];

    private List<MediaItem> ReadCatalog()
    {
        lock (rowGate)
        {
            // One snapshot for the three tables, so an episode is never joined to a show saved after it was read.
            using var db = Open(); using var snapshot = db.BeginTransaction(deferred: true);
            using var pool = RowText.Share();
            var shows = ReadRows(db, snapshot, "shows", ref readShows).ToDictionary(x => x.Id);
            var seasons = ReadRows(db, snapshot, "seasons", ref readSeasons).ToDictionary(x => x.Id);
            using var command = db.CreateCommand(); command.Transaction = snapshot; command.CommandText = "SELECT json FROM media";
            using var rows = command.ExecuteReader();
            var current = new Dictionary<UInt128, ReadItem>(readItems.Count); var items = new List<MediaItem>(readItems.Count);
            while (rows.Read())
            {
                var json = RowText.Bytes(rows, 0); var key = RowText.Hash(json);
                // Two rows never hold the same bytes, as each holds its own ID, but a row read twice must not fail.
                if (current.TryGetValue(key, out var read)) { items.Add(read.Item); continue; }
                if (readItems.TryGetValue(key, out read) && ReferenceEquals(read.Show, Show(read.Item)) && ReferenceEquals(read.Season, Season(read.Item)))
                { current[key] = read; items.Add(read.Item); continue; }
                var stored = RowText.Read<MediaItem>(json);
                current[key] = read = new(ApplyHierarchy(stored, Show(stored), Season(stored)), Show(stored), Season(stored));
                items.Add(read.Item);
            }
            readItems = current;
            return SortItems(items);
            LibraryShow? Show(MediaItem item) => item.ShowId is { } id ? shows.GetValueOrDefault(id) : null;
            LibrarySeason? Season(MediaItem item) => item.SeasonId is { } id ? seasons.GetValueOrDefault(id) : null;
        }
    }
    private static List<T> ReadRows<T>(SqliteConnection db, SqliteTransaction snapshot, string table, ref Dictionary<UInt128, T> read) where T : class
    {
        using var command = db.CreateCommand(); command.Transaction = snapshot; command.CommandText = "SELECT json FROM " + table;
        using var rows = command.ExecuteReader();
        var current = new Dictionary<UInt128, T>(read.Count); var records = new List<T>(read.Count);
        while (rows.Read())
        {
            var json = RowText.Bytes(rows, 0); var key = RowText.Hash(json);
            if (!current.TryGetValue(key, out var record)) current[key] = record = read.TryGetValue(key, out var known) ? known : RowText.Read<T>(json);
            records.Add(record);
        }
        read = current;
        return records;
    }
}

// A row's JSON as SQLite stores it: UTF-8 bytes that are valid until the reader moves on, read without first copying
// them into a string twice their size.
internal static class RowText
{
    private static readonly JsonSerializerOptions Json = new(Wire.Json) { Converters = { new SharedText() } };
    [ThreadStatic] private static HashSet<string>? shared;

    public static ReadOnlySpan<byte> Bytes(SqliteDataReader rows, int column) => SQLitePCL.raw.sqlite3_column_blob(rows.Handle!, column);
    // 128 bits of SHA-256: a row that changed must never be taken for the one read before.
    public static UInt128 Hash(ReadOnlySpan<byte> json)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes]; SHA256.HashData(json, hash);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }
    public static T Read<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Empty message");
    // Until disposed, equal texts read on this thread are one string. A show's episodes each repeat its overview, cast,
    // genres and artwork paths, and a field downloaded from TMDB stores its value twice more, so most of a catalog's
    // text is copies.
    public static Sharing Share() { var outer = shared; shared = new(StringComparer.Ordinal); return new(outer); }
    public readonly struct Sharing(HashSet<string>? outer) : IDisposable { public void Dispose() => shared = outer; }

    private sealed class SharedText : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Text(ref reader);
        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Text(ref reader)!;
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
        private static string? Text(ref Utf8JsonReader reader)
        {
            if (shared is not { } texts) return reader.GetString();
            // Unescaped text is never longer in UTF-16 units than its UTF-8 bytes.
            var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
            char[]? rented = null;
            Span<char> buffer = length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(length));
            try
            {
                ReadOnlySpan<char> text = buffer[..reader.CopyString(buffer)];
                if (texts.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out var known)) return known;
                var added = new string(text); texts.Add(added);
                return added;
            }
            finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }
        }
    }
}
