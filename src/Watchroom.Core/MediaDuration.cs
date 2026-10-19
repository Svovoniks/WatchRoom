using System.Buffers.Binary;

namespace Watchroom.Core;

// Read container headers only, on demand. No decoder, executable or full-file scan.
public static class MediaDuration
{
    public static double? Minutes(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var seconds = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" or ".mov" => Mp4(file, file.Length, ct),
                ".mkv" or ".webm" => Matroska(file, ct),
                ".avi" => Avi(file, ct),
                _ => null
            };
            return seconds is > 0 and < 604800 && double.IsFinite(seconds.Value) ? seconds / 60 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException) { return null; }
    }
    private static double? Mp4(Stream file, long end, CancellationToken ct, int depth = 0)
    {
        Span<byte> header = stackalloc byte[8];
        for (var count = 0; count < 256 && file.Position + 8 <= end; count++)
        {
            ct.ThrowIfCancellationRequested(); var start = file.Position; file.ReadExactly(header);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header); var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (size == 1) { file.ReadExactly(header); size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header)); }
            if (size == 0) size = end - start;
            if (size < file.Position - start || size > end - start) return null;
            var next = start + size;
            if (type == 0x6d6f6f76 && depth == 0) return Mp4(file, next, ct, 1); // moov
            if (type == 0x6d766864) // mvhd: creation/modification times, timescale, duration
            {
                if (next - file.Position < 20) return null;
                file.ReadExactly(header[..4]); var version = header[0];
                if (version > 1) return null;
                var skip = version == 1 ? 16 : 8;
                if (next - file.Position < skip + (version == 1 ? 12 : 8)) return null;
                file.Position += skip; file.ReadExactly(header[..4]); var scale = BinaryPrimitives.ReadUInt32BigEndian(header);
                file.ReadExactly(header[..(version == 1 ? 8 : 4)]);
                var duration = version == 1 ? BinaryPrimitives.ReadUInt64BigEndian(header) : BinaryPrimitives.ReadUInt32BigEndian(header);
                return scale > 0 && duration != (version == 1 ? ulong.MaxValue : uint.MaxValue) ? (double)duration / scale : null;
            }
            file.Position = next;
        }
        return null;
    }
    private static ulong Vint(Stream file, bool id)
    {
        var first = file.ReadByte(); if (first <= 0) throw new IOException("Invalid EBML header.");
        var mask = 0x80; var length = 1; while ((first & mask) == 0) { mask >>= 1; length++; }
        if (length > (id ? 4 : 8)) throw new IOException("Invalid EBML length.");
        ulong value = (ulong)(id ? first : first & (mask - 1));
        for (var i = 1; i < length; i++) { var next = file.ReadByte(); if (next < 0) throw new EndOfStreamException(); value = value << 8 | (byte)next; }
        return !id && value == (1UL << (7 * length)) - 1 ? ulong.MaxValue : value;
    }
    private static double? Matroska(Stream file, CancellationToken ct)
    {
        long end = file.Length; long scale = 1_000_000; double? duration = null;
        Span<byte> bytes = stackalloc byte[8];
        for (var count = 0; count < 256 && file.Position < end; count++)
        {
            ct.ThrowIfCancellationRequested(); var id = Vint(file, true); var size = Vint(file, false);
            var next = size == ulong.MaxValue ? end : checked(file.Position + (long)size);
            if (next > end) return null;
            if (id == 0x18538067) { end = next; continue; } // Segment
            if (id == 0x1549a966) // Info
            {
                for (var fields = 0; fields < 128 && file.Position < next; fields++)
                {
                    ct.ThrowIfCancellationRequested(); var field = Vint(file, true); var length = Vint(file, false);
                    if (length > (ulong)(next - file.Position)) return null;
                    if (field == 0x2ad7b1 && length is > 0 and <= 8)
                    {
                        bytes.Clear(); file.ReadExactly(bytes[(8 - (int)length)..]); scale = checked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes));
                    }
                    else if (field == 0x4489 && length is 4 or 8)
                    {
                        file.ReadExactly(bytes[..(int)length]); duration = length == 4
                            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))
                            : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes));
                    }
                    else file.Position += (long)length;
                }
                return duration * scale / 1_000_000_000;
            }
            // Don't walk video clusters looking for absent duration metadata.
            if (id == 0x1f43b675 || next <= file.Position) return null;
            file.Position = next;
        }
        return null;
    }
    private static double? Avi(Stream file, CancellationToken ct)
    {
        Span<byte> bytes = stackalloc byte[36];
        file.ReadExactly(bytes[..12]);
        if (!bytes[..4].SequenceEqual("RIFF"u8) || !bytes[8..12].SequenceEqual("AVI "u8)) return null;
        var end = Math.Min(file.Length, 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]));
        uint perFrame = 0, frames = 0, totalFrames = 0, scale = 0, rate = 0, length = 0; var video = false;
        for (var count = 0; count < 512 && file.Position + 8 <= end; count++)
        {
            ct.ThrowIfCancellationRequested(); file.ReadExactly(bytes[..8]);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]); var next = file.Position + size + (size & 1);
            if (bytes[..4].SequenceEqual("LIST"u8))
            {
                if (size < 4) return null;
                file.ReadExactly(bytes[..4]);
                // Header lists hold the chunks we need and their children follow in order, so walk straight in.
                if (bytes[..4].SequenceEqual("hdrl"u8) || bytes[..4].SequenceEqual("strl"u8) || bytes[..4].SequenceEqual("odml"u8)) continue;
                if (bytes[..4].SequenceEqual("movi"u8)) break; // stream data; every header precedes it
            }
            else if (bytes[..4].SequenceEqual("avih"u8) && size >= 20)
            {
                file.ReadExactly(bytes[..20]); perFrame = BinaryPrimitives.ReadUInt32LittleEndian(bytes); frames = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
            }
            else if (bytes[..4].SequenceEqual("strh"u8) && size >= 36 && !video)
            {
                file.ReadExactly(bytes[..36]);
                if (video = bytes[..4].SequenceEqual("vids"u8))
                {
                    scale = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]); rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
                    length = (uint)Math.Min(uint.MaxValue, (ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) + BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]));
                }
            }
            // OpenDML files over 1 GB count only the first RIFF segment in avih; dmlh has the whole file.
            else if (bytes[..4].SequenceEqual("dmlh"u8) && size >= 4) { file.ReadExactly(bytes[..4]); totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(bytes); }
            if (next > end) break;
            file.Position = next;
        }
        if (scale > 0 && rate > 0 && length > 0) return (double)Math.Max(length, totalFrames) * scale / rate;
        return perFrame > 0 ? (double)Math.Max(frames, totalFrames) * perFrame / 1_000_000 : null;
    }
}
