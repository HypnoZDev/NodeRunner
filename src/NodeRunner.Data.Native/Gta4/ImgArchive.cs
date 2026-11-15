using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NodeRunner.Data.Native.Gta4;

// A file of a GTA IV archive. Resources (".wdr", ".wtd", ".wbn"...) are stored as a 12-byte "RSC\x05" header (magic,
// type, flags) followed by zlib data; other files (".wpl", ".nod"...) are stored as is.
public readonly record struct ImgEntry(string Name, int Disk, long Offset, int Size, bool IsResource, uint ResourceType,
    uint ResourceFlags);

// GTA IV archive (".img", version 3): a 20-byte header (magic, version, entry count, table size, entry size), then the
// table: 16-byte entries followed by the file names. The game encrypts the first 16 bytes of the header and the table.
public static class ImgArchive
{
    public const uint Magic = 0xA94E2A52;
    public const uint ResourceMagic = 0x05435352;
    public const int BlockSize = 2048;
    public const int HeaderSize = 20;
    public const int ResourceHeaderSize = 12;
    private const int EntrySize = 16;
    private const ushort ResourceFlag = 0x2000;
    private const ushort PaddingMask = 0x7FF;

    // True if the header is encrypted (the archive cannot be read without the key).
    public static bool IsEncrypted(SafeFileHandle handle)
    {
        Span<byte> magic = stackalloc byte[4];
        return RandomAccess.Read(handle, magic, 0) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magic) != Magic;
    }

    public static List<ImgEntry> Read(SafeFileHandle handle, int disk, Gta4Cipher? cipher)
    {
        long length = RandomAccess.GetLength(handle);
        var header = new byte[HeaderSize];
        ReadExactly(handle, header, 0);
        bool encrypted = BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic;
        if (encrypted)
        {
            if (cipher is null)
                throw new InvalidDataException("archive chiffrée, clé du jeu introuvable");
            cipher.Decrypt(header.AsSpan(0, 16));
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 3)
            throw new InvalidDataException("pas une archive .img de GTA IV (version 3)");

        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        int tableSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (count < 0 || tableSize < (long)count * EntrySize || HeaderSize + (long)tableSize > length)
            throw new InvalidDataException("table des fichiers invalide");

        var table = new byte[tableSize];
        ReadExactly(handle, table, HeaderSize);
        if (encrypted)
            cipher!.Decrypt(table);

        var entries = new List<ImgEntry>(count);
        int names = count * EntrySize;
        for (int i = 0; i < count; i++)
        {
            var e = table.AsSpan(i * EntrySize, EntrySize);
            int end = names < tableSize ? Array.IndexOf(table, (byte)0, names) : -1;
            if (end < 0)
                end = tableSize;
            string name = names < tableSize ? Encoding.ASCII.GetString(table, names, end - names) : $"file{i}";
            names = end + 1;

            uint sizeOrFlags = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            long offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * BlockSize;
            int blocks = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(e[14..]);
            int size = Math.Max(0, blocks * BlockSize - (flags & PaddingMask));
            if (offset + size > length || name.Length == 0)
                continue;
            bool resource = (flags & ResourceFlag) != 0;
            entries.Add(new ImgEntry(name, disk, offset, size, resource, resource ? type : 0, resource ? sizeOrFlags : 0));
        }
        return entries;
    }

    // Size of the memory pages described by resource flags: system part, then graphics part.
    public static (int System, int Graphics) ResourceSizes(uint flags) => (
        (int)Math.Min((long)(flags & 0x7FF) << (int)(((flags >> 11) & 0xF) + 8), int.MaxValue),
        (int)Math.Min((long)((flags >> 15) & 0x7FF) << (int)(((flags >> 26) & 0xF) + 8), int.MaxValue));

    // Content of a resource as stored in the archive (header included), decompressed.
    public static byte[] InflateResource(ReadOnlySpan<byte> stored)
    {
        if (stored.Length < ResourceHeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(stored) != ResourceMagic)
            throw new InvalidDataException("ressource invalide");
        var (system, graphics) = ResourceSizes(BinaryPrimitives.ReadUInt32LittleEndian(stored[8..]));
        using var input = new MemoryStream(stored[ResourceHeaderSize..].ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        // Capped like RPF resources: wrong flags in a modded file must not reserve a huge buffer.
        using var output = new MemoryStream(Math.Clamp(system + graphics, 0, 32 * 1024 * 1024));
        zlib.CopyTo(output);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
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
