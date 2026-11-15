using System.Buffers.Binary;
using System.Text;

namespace NodeRunner.Data.Native.Archives;

// Builds an RPF7 archive with the "OPEN" encryption marker (tables in clear), the form mods folders load. Files are
// stored as given: binary files uncompressed, resources ("RSC7" files) as they are, nested archives as built by another
// RpfWriter.
public sealed class RpfWriter
{
    private const uint Magic = 0x52504637;          // "RPF7"
    private const uint OpenMarker = 0x4E45504F;     // "OPEN"
    private const uint DirectoryMarker = 0x7FFFFF00;
    private const uint ResourceMagic = 0x37435352;  // "RSC7"
    private const int BlockSize = 512;
    private const uint MaxStoredSize = 0xFFFFFF;
    private const long MaxArchiveSize = 0x7FFFFFL * BlockSize;

    private sealed class Directory(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, Directory> Directories { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Func<Stream>> Files { get; } = new(StringComparer.Ordinal);
    }

    private readonly Directory _root = new("");

    public int FileCount { get; private set; }

    // path: "folder/sub/name.ext" (either slash); names are stored in lower case, as in the game's archives.
    public void Add(string path, byte[] data) => Add(path, () => new MemoryStream(data, writable: false));

    public void AddFile(string path, string sourceFile) => Add(path, () => File.OpenRead(sourceFile));

    public void Add(string path, Func<Stream> open)
    {
        var parts = path.Replace('/', '\\').Trim('\\').ToLowerInvariant()
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ArgumentException("Chemin vide.", nameof(path));
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Any(c => c < 0x20 || c > 0x7E))
                throw new ArgumentException($"Nom invalide dans une archive : {path}", nameof(path));
        }

        var directory = _root;
        foreach (var part in parts[..^1])
        {
            if (directory.Files.ContainsKey(part))
                throw new ArgumentException($"{path} : {part} est déjà un fichier.", nameof(path));
            if (!directory.Directories.TryGetValue(part, out var next))
                directory.Directories[part] = next = new Directory(part);
            directory = next;
        }
        string name = parts[^1];
        if (directory.Directories.ContainsKey(name))
            throw new ArgumentException($"{path} : c'est déjà un dossier.", nameof(path));
        if (!directory.Files.ContainsKey(name))
            FileCount++;
        directory.Files[name] = open;
    }

    public byte[] ToArray()
    {
        using var output = new MemoryStream();
        Write(output);
        return output.ToArray();
    }

    public void Write(string file)
    {
        using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Write(output);
    }

    // Writes the whole archive; output must start empty (offsets are relative to its start).
    public void Write(Stream output)
    {
        // Breadth-first: the root is entry 0, each directory's children are consecutive and sorted by name (the game
        // looks names up by binary search).
        var entries = new List<Entry>();
        var names = new MemoryStream();
        int NameOffset(string name)
        {
            int offset = (int)names.Length;
            names.Write(Encoding.ASCII.GetBytes(name));
            names.WriteByte(0);
            return offset;
        }

        entries.Add(new Entry(_root, null, NameOffset("")));
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Folder is not { } folder)
                continue;
            var children = folder.Directories.Keys.Concat(folder.Files.Keys).Order(StringComparer.Ordinal).ToList();
            entries[i] = entries[i] with { First = entries.Count, Count = children.Count };
            foreach (var child in children)
            {
                entries.Add(folder.Directories.TryGetValue(child, out var sub)
                    ? new Entry(sub, null, NameOffset(child))
                    : new Entry(null, folder.Files[child], NameOffset(child)) { Name = child });
            }
        }
        while (names.Length % 16 != 0)
            names.WriteByte(0);
        // File entries keep only 16 bits for their name offset.
        if (names.Length > 0x10000)
            throw new InvalidDataException("Trop de noms de fichiers pour une seule archive.");

        int tableLength = 16 + entries.Count * 16 + (int)names.Length;
        var table = new byte[tableLength];
        BinaryPrimitives.WriteUInt32LittleEndian(table, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(4), entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8), (int)names.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(12), OpenMarker);
        names.ToArray().CopyTo(table, 16 + entries.Count * 16);

        // The table is written last, once each file's place and size are known.
        output.Write(new byte[Align(tableLength)]);
        long position = Align(tableLength);
        var buffer = new byte[1 << 20];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var e = table.AsSpan(16 + i * 16, 16);
            if (entry.Folder is not null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)entry.NameOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], DirectoryMarker);
                BinaryPrimitives.WriteInt32LittleEndian(e[8..], entry.First);
                BinaryPrimitives.WriteInt32LittleEndian(e[12..], entry.Count);
                continue;
            }

            long start = position;
            using (var source = entry.Open!())
                position += WriteFile(source, output, e, entry, start, buffer);
            long padding = Align(position) - position;
            output.Write(new byte[padding]);
            position += padding;
            if (position > MaxArchiveSize)
                throw new InvalidDataException("Archive trop grande (4 Go au plus).");
        }

        output.Position = 0;
        output.Write(table);
        output.Position = position;
    }

    private static long WriteFile(Stream source, Stream output, Span<byte> e, Entry entry, long start, byte[] buffer)
    {
        long block = start / BlockSize;
        Span<byte> head = stackalloc byte[16];
        int headLength = ReadUpTo(source, head);
        bool isResource = headLength == 16 && BinaryPrimitives.ReadUInt32LittleEndian(head) == ResourceMagic
            && !entry.Name!.EndsWith(".rpf", StringComparison.Ordinal);

        if (isResource)
        {
            uint systemFlags = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            uint graphicsFlags = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            long total = CopyRest(source, output, buffer, head[..headLength]);
            if (total >= MaxStoredSize)
            {
                // Large resources: the stored size goes into four bytes of the header copy.
                if (total > uint.MaxValue)
                    throw new InvalidDataException($"{entry.Name} : fichier trop grand.");
                long end = output.Position;
                output.Position = start;
                head[2] = (byte)(total >> 24);
                head[5] = (byte)(total >> 16);
                head[7] = (byte)total;
                head[14] = (byte)(total >> 8);
                output.Write(head);
                output.Position = end;
            }
            ulong stored = Math.Min((ulong)total, MaxStoredSize);
            BinaryPrimitives.WriteUInt64LittleEndian(e, (uint)entry.NameOffset | stored << 16 | ((ulong)block | 0x800000) << 40);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], systemFlags);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], graphicsFlags);
            return total;
        }

        long length = CopyRest(source, output, buffer, head[..headLength]);
        if (length > uint.MaxValue)
            throw new InvalidDataException($"{entry.Name} : fichier trop grand.");
        // Stored size 0 = not compressed; then the real size, and 0 = not encrypted.
        BinaryPrimitives.WriteUInt64LittleEndian(e, (uint)entry.NameOffset | (ulong)block << 40);
        BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[12..], 0);
        return length;
    }

    private static long CopyRest(Stream source, Stream output, byte[] buffer, ReadOnlySpan<byte> head)
    {
        output.Write(head);
        long copied = head.Length;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            copied += read;
        }
        return copied;
    }

    private static int ReadUpTo(Stream source, Span<byte> buffer)
    {
        int total = 0;
        int read;
        while (total < buffer.Length && (read = source.Read(buffer[total..])) > 0)
            total += read;
        return total;
    }

    private static long Align(long value) => (value + BlockSize - 1) / BlockSize * BlockSize;

    private sealed record Entry(Directory? Folder, Func<Stream>? Open, int NameOffset)
    {
        public string? Name { get; init; }
        public int First { get; init; }
        public int Count { get; init; }
    }
}
