using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NodeRunner.Data.Native.Archives;

// A file stored in an RPF archive (directories are not kept).
internal readonly record struct RpfEntry(
    string Name, int Disk, long Offset, uint StoredSize, uint Size, uint GraphicsFlags,
    bool IsResource, ArchiveEncryption Encryption)
{
    public uint SystemFlags => Size;
}

// Reads the table of contents of an RPF7 archive.
internal static class RpfArchive
{
    private const uint Magic = 0x52504637;          // "RPF7"
    private const uint AesMarker = 0x0FFFFFF9;
    private const uint NgMarker = 0x0FEFFFFF;
    private const uint DirectoryMarker = 0x7FFFFF00;
    private const long BlockSize = 512;

    private const int MaxDepth = 8;

    public static void Read(SafeFileHandle handle, int disk, long start, string name, uint length, string path,
        ArchiveKeys keys, Dictionary<string, RpfEntry> files, List<string> archives, int depth = 0)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"{path} : archives imbriquées en boucle.");
        Span<byte> header = stackalloc byte[16];
        ReadExactly(handle, header, start);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"{path} n'est pas une archive RPF7.");

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int namesLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var encryption = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) switch
        {
            AesMarker => ArchiveEncryption.Aes,
            NgMarker => ArchiveEncryption.Ng,
            _ => ArchiveEncryption.None,
        };
        if (entryCount <= 0 || entryCount > 1_000_000 || namesLength < 0 || namesLength > 16_000_000)
            throw new InvalidDataException($"{path} : table des fichiers invalide.");

        var entries = new byte[entryCount * 16];
        var names = new byte[namesLength];
        ReadExactly(handle, entries, start + 16);
        ReadExactly(handle, names, start + 16 + entries.Length);
        ArchiveCipher.Decrypt(encryption, keys, entries, name, length);
        ArchiveCipher.Decrypt(encryption, keys, names, name, length);

        if (BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(4)) != DirectoryMarker)
            throw new InvalidDataException($"{path} : impossible de déchiffrer la table des fichiers.");

        archives.Add(path);
        var visited = new bool[entryCount];
        AddDirectory(0, path);

        void AddDirectory(int index, string directoryPath)
        {
            // A modded archive can hold a folder listed twice or inside itself: would never end.
            if (visited[index] || directoryPath.Length > 1024)
                throw new InvalidDataException($"{path} : dossiers en boucle.");
            visited[index] = true;
            var e = entries.AsSpan(index * 16, 16);
            int first = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            if (first < 0 || count < 0 || first + count > entryCount)
                throw new InvalidDataException($"{path} : dossier invalide.");

            for (int i = first; i < first + count; i++)
            {
                var child = entries.AsSpan(i * 16, 16);
                ulong low = BinaryPrimitives.ReadUInt64LittleEndian(child);
                uint marker = BinaryPrimitives.ReadUInt32LittleEndian(child[4..]);
                string childName = Name(names, marker == DirectoryMarker ? (int)(low & 0xFFFFFFFF) : (int)(low & 0xFFFF));
                string childPath = directoryPath + "\\" + childName;

                if (marker == DirectoryMarker)
                {
                    if (i != index)
                        AddDirectory(i, childPath);
                    continue;
                }

                uint storedSize = (uint)((low >> 16) & 0xFFFFFF);
                bool isResource = (marker & 0x80000000) != 0;
                long offset = start + (long)((low >> 40) & 0x7FFFFF) * BlockSize;
                uint value1 = BinaryPrimitives.ReadUInt32LittleEndian(child[8..]);
                uint value2 = BinaryPrimitives.ReadUInt32LittleEndian(child[12..]);

                if (isResource)
                {
                    if (storedSize == 0xFFFFFF)
                        storedSize = LargeResourceSize(handle, offset);
                    // Only compiled scripts are encrypted among resources.
                    var resourceEncryption = childName.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase)
                        ? encryption : ArchiveEncryption.None;
                    files[childPath] = new RpfEntry(childName, disk, offset, storedSize, value1, value2, true, resourceEncryption);
                    continue;
                }

                // Binary file: value1 = size once decompressed, value2 = 1 when encrypted.
                var fileEncryption = value2 != 0 ? encryption : ArchiveEncryption.None;
                files[childPath] = new RpfEntry(childName, disk, offset, storedSize, value1, 0, false, fileEncryption);

                if (storedSize == 0 && childName.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                {
                    if (offset == start)
                        throw new InvalidDataException($"{childPath} : archive qui se contient elle-même.");
                    Read(handle, disk, offset, childName, value1, childPath, keys, files, archives, depth + 1);
                }
            }
        }
    }

    private static uint LargeResourceSize(SafeFileHandle handle, long offset)
    {
        Span<byte> h = stackalloc byte[16];
        ReadExactly(handle, h, offset);
        return h[7] | (uint)h[14] << 8 | (uint)h[5] << 16 | (uint)h[2] << 24;
    }

    private static string Name(byte[] names, int offset)
    {
        if (offset < 0 || offset >= names.Length)
            return "";
        int end = Array.IndexOf(names, (byte)0, offset);
        return Encoding.ASCII.GetString(names, offset, (end < 0 ? names.Length : end) - offset);
    }

    public static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0)
                throw new EndOfStreamException("Fin de l'archive atteinte trop tôt.");
            buffer = buffer[read..];
            offset += read;
        }
    }
}
