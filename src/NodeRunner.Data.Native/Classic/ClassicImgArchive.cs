using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NodeRunner.Data.Native.Classic;

// A file of a GTA III / Vice City / San Andreas archive, stored as is (no compression).
public readonly record struct ClassicImgEntry(string Name, int Disk, long Offset, int Size);

// ".img" archives of the first trilogy, in 2048-byte blocks with 32-byte entries (name on 24 bytes):
// - version 1 (GTA III, Vice City): the table is a separate ".dir" file of entries (offset, size);
// - version 2 (San Andreas): "VER2", entry count, then entries (offset, size as two 16-bit numbers) at the start.
public static class ClassicImgArchive
{
    public const int BlockSize = 2048;
    public const uint Version2Magic = 0x32524556; // "VER2"
    private const int EntrySize = 32;
    private const int NameSize = 24;
    private const int MaxEntries = 1 << 20;

    public static bool IsVersion2(SafeFileHandle handle)
    {
        Span<byte> magic = stackalloc byte[4];
        return RandomAccess.Read(handle, magic, 0) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) == Version2Magic;
    }

    // Version 2: the table is at the start of the archive.
    public static List<ClassicImgEntry> Read(SafeFileHandle handle, int disk)
    {
        long length = RandomAccess.GetLength(handle);
        var header = new byte[8];
        ReadExactly(handle, header, 0);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Version2Magic)
            throw new InvalidDataException("pas une archive .img de San Andreas (VER2)");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (count < 0 || count > MaxEntries || 8 + (long)count * EntrySize > length)
            throw new InvalidDataException("table des fichiers invalide");
        var table = new byte[count * EntrySize];
        ReadExactly(handle, table, 8);
        var entries = new List<ClassicImgEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * EntrySize, EntrySize);
            long offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(e) * BlockSize;
            int streaming = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
            int stored = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            Add(entries, Name(e[8..]), disk, offset, stored != 0 ? stored : streaming, length);
        }
        return entries;
    }

    // Version 1: the table comes from the ".dir" file next to the archive.
    public static List<ClassicImgEntry> Read(SafeFileHandle handle, ReadOnlySpan<byte> directory, int disk)
    {
        if (directory.Length % EntrySize != 0 || directory.Length / EntrySize > MaxEntries)
            throw new InvalidDataException("fichier .dir invalide");
        long length = RandomAccess.GetLength(handle);
        int count = directory.Length / EntrySize;
        var entries = new List<ClassicImgEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = directory.Slice(i * EntrySize, EntrySize);
            long offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(e) * BlockSize;
            long blocks = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            Add(entries, Name(e[8..]), disk, offset, blocks, length);
        }
        return entries;
    }

    private static void Add(List<ClassicImgEntry> entries, string name, int disk, long offset, long blocks, long length)
    {
        long size = blocks * BlockSize;
        // The last file may end before its last block is complete.
        if (offset + size > length)
            size = length - offset;
        if (name.Length == 0 || size <= 0 || size > int.MaxValue)
            return;
        entries.Add(new ClassicImgEntry(name, disk, offset, (int)size));
    }

    private static string Name(ReadOnlySpan<byte> field)
    {
        field = field[..NameSize];
        int end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).Trim();
    }

    internal static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0)
                throw new EndOfStreamException("archive tronquée");
            buffer = buffer[read..];
            offset += read;
        }
    }
}
