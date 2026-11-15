using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NodeRunner.Data.Native.Archives;

// Changes files of an RPF7 archive on disk without rewriting it: the new content is added at the end and only its
// entry of the table changes, so the archive stays valid if the work stops halfway. Works on archives with tables in
// clear ("OPEN", as mods folders use); CopyAsOpen makes one from a game archive.
public sealed class RpfEditor : IDisposable
{
    private const uint Magic = 0x52504637;          // "RPF7"
    private const uint OpenMarker = 0x4E45504F;     // "OPEN"
    private const uint AesMarker = 0x0FFFFFF9;
    private const uint NgMarker = 0x0FEFFFFF;
    private const uint DirectoryMarker = 0x7FFFFF00;
    private const int BlockSize = 512;
    private const long MaxBlock = 0x7FFFFF;

    private readonly FileStream _file;
    private readonly byte[] _entries;
    private readonly byte[] _names;

    public RpfEditor(string path, bool writable = true)
    {
        _file = new FileStream(path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.Read : FileShare.ReadWrite);
        try
        {
            var (count, namesLength, marker) = ReadHeader(_file, path);
            if (marker is AesMarker or NgMarker)
                throw new InvalidDataException($"{Path.GetFileName(path)} : archive chiffrée, impossible de la modifier.");
            _entries = new byte[count * 16];
            _names = new byte[namesLength];
            ReadAt(_file, 16, _entries);
            ReadAt(_file, 16 + _entries.Length, _names);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public static bool IsEncrypted(string path)
    {
        using var file = File.OpenRead(path);
        return ReadHeader(file, path).Marker is AesMarker or NgMarker;
    }

    // null if absent. path: "folder\name.ext" from the archive's root, any case.
    public byte[]? Read(string path)
    {
        if (Find(path) is not { } index)
            return null;
        var e = _entries.AsSpan(index * 16, 16);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(e);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) & 0x80000000) != 0)
            throw new InvalidDataException($"{path} : ressource, pas un fichier simple.");
        uint stored = (uint)((low >> 16) & 0xFFFFFF);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
        long offset = (long)((low >> 40) & 0x7FFFFF) * BlockSize;
        var data = new byte[stored != 0 ? stored : size];
        ReadAt(_file, offset, data);
        if (stored == 0)
            return data;
        using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream((int)Math.Min(size, 64 * 1024 * 1024));
        deflate.CopyTo(output);
        return output.ToArray();
    }

    // Replaces an existing binary file, stored uncompressed.
    public void Replace(string path, ReadOnlySpan<byte> data)
    {
        if (Find(path) is not { } index)
            throw new FileNotFoundException($"{path} absent de l'archive.");
        var e = _entries.AsSpan(index * 16, 16);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(e[4..]) & 0x80000000) != 0)
            throw new InvalidDataException($"{path} : ressource, pas un fichier simple.");

        long start = Align(_file.Length);
        if (start / BlockSize > MaxBlock)
            throw new InvalidDataException("Archive trop grande pour y ajouter un fichier.");
        _file.Position = start;
        _file.Write(data);
        _file.Write(new byte[Align(start + data.Length) - start - data.Length]);
        _file.Flush(flushToDisk: true);

        ulong nameOffset = BinaryPrimitives.ReadUInt64LittleEndian(e) & 0xFFFF;
        BinaryPrimitives.WriteUInt64LittleEndian(e, nameOffset | (ulong)(start / BlockSize) << 40);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0);
        _file.Position = 16 + index * 16;
        _file.Write(e);
        _file.Flush(flushToDisk: true);
    }

    private int? Find(string path)
    {
        var parts = path.Replace('/', '\\').Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int directory = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            var d = _entries.AsSpan(directory * 16, 16);
            int first = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
            int? found = null;
            for (int i = first; i < first + count && i < _entries.Length / 16; i++)
            {
                var child = _entries.AsSpan(i * 16, 16);
                bool isDirectory = BinaryPrimitives.ReadUInt32LittleEndian(child[4..]) == DirectoryMarker;
                int nameOffset = isDirectory
                    ? BinaryPrimitives.ReadInt32LittleEndian(child)
                    : BinaryPrimitives.ReadUInt16LittleEndian(child);
                if (isDirectory == (p < parts.Length - 1) && Name(nameOffset).Equals(parts[p], StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }
            if (found is not { } index)
                return null;
            if (p == parts.Length - 1)
                return index;
            directory = index;
        }
        return null;
    }

    private string Name(int offset)
    {
        if (offset < 0 || offset >= _names.Length)
            return "";
        int end = Array.IndexOf(_names, (byte)0, offset);
        return Encoding.ASCII.GetString(_names, offset, (end < 0 ? _names.Length : end) - offset);
    }

    public void Dispose() => _file.Dispose();

    // Copies a game archive with its table and its encrypted files decrypted, marked "OPEN". Nested archives are copied
    // as they are (each one is encrypted with its own name and size, which do not change).
    public static void CopyAsOpen(string source, string target, ArchiveKeys keys, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        {
            var buffer = new byte[4 << 20];
            long total = input.Length;
            long done = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                progress?.Report((double)done / total);
            }
        }

        using var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (count, namesLength, marker) = ReadHeader(file, source);
        var encryption = marker switch
        {
            AesMarker => ArchiveEncryption.Aes,
            NgMarker => ArchiveEncryption.Ng,
            _ => ArchiveEncryption.None,
        };
        if (encryption == ArchiveEncryption.None)
            return;

        string name = Path.GetFileName(source);
        uint length = (uint)file.Length;
        var entries = new byte[count * 16];
        var names = new byte[namesLength];
        ReadAt(file, 16, entries);
        ReadAt(file, 16 + entries.Length, names);
        ArchiveCipher.Decrypt(encryption, keys, entries, name, length);
        ArchiveCipher.Decrypt(encryption, keys, names, name, length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(4)) != DirectoryMarker)
            throw new InvalidDataException($"{name} : impossible de déchiffrer la table des fichiers.");

        for (int i = 0; i < count; i++)
        {
            var e = entries.AsSpan(i * 16, 16);
            uint second = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            if (second == DirectoryMarker)
                continue;
            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(e);
            string entryName = EntryName(names, (int)(low & 0xFFFF));
            uint stored = (uint)((low >> 16) & 0xFFFFFF);
            long offset = (long)((low >> 40) & 0x7FFFFF) * BlockSize;
            if ((second & 0x80000000) != 0)
            {
                // Among resources, only compiled scripts are encrypted (after their 16-byte header).
                if (!entryName.EndsWith(".ysc", StringComparison.OrdinalIgnoreCase) || stored <= 16 || stored == 0xFFFFFF)
                    continue;
                var data = new byte[stored - 16];
                ReadAt(file, offset + 16, data);
                ArchiveCipher.Decrypt(encryption, keys, data, entryName, stored);
                file.Position = offset + 16;
                file.Write(data);
                continue;
            }
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(e[12..]) == 0)
                continue;
            var content = new byte[stored != 0 ? stored : size];
            ReadAt(file, offset, content);
            ArchiveCipher.Decrypt(encryption, keys, content, entryName, size);
            file.Position = offset;
            file.Write(content);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0);
        }

        file.Position = 12;
        Span<byte> open = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(open, OpenMarker);
        file.Write(open);
        file.Position = 16;
        file.Write(entries);
        file.Write(names);
        file.Flush(flushToDisk: true);
    }

    private static string EntryName(byte[] names, int offset)
    {
        if (offset < 0 || offset >= names.Length)
            return "";
        int end = Array.IndexOf(names, (byte)0, offset);
        return Encoding.ASCII.GetString(names, offset, (end < 0 ? names.Length : end) - offset);
    }

    private static (int Count, int NamesLength, uint Marker) ReadHeader(FileStream file, string path)
    {
        Span<byte> header = stackalloc byte[16];
        if (file.Length < 16)
            throw new InvalidDataException($"{Path.GetFileName(path)} n'est pas une archive RPF7.");
        ReadAt(file, 0, header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"{Path.GetFileName(path)} n'est pas une archive RPF7.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int namesLength = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (count <= 0 || count > 1_000_000 || namesLength < 0 || namesLength > 16_000_000)
            throw new InvalidDataException($"{Path.GetFileName(path)} : table des fichiers invalide.");
        return (count, namesLength, BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
    }

    private static void ReadAt(FileStream file, long offset, Span<byte> buffer)
    {
        file.Position = offset;
        file.ReadExactly(buffer);
    }

    private static long Align(long value) => (value + BlockSize - 1) / BlockSize * BlockSize;
}
