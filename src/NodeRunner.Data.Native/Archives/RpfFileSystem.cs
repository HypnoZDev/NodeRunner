using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using NodeRunner.Data.FileSystem;
using NodeRunner.Data.Installation;

namespace NodeRunner.Data.Native.Archives;

// Gives the keys of the user's game installation (read from their own game files).
public interface IArchiveKeySource
{
    ArchiveKeys Load(GameInstallation installation, IProgress<string>? progress);
}

// IGameFileSystem over the RPF archives of the user's installation.
public sealed class RpfFileSystem : IGameFileSystem
{
    private readonly IArchiveKeySource _keySource;
    private ArchiveKeys? _keys;
    private readonly List<SafeFileHandle> _disks = [];
    private readonly Dictionary<string, RpfEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _archives = [];

    public RpfFileSystem(IArchiveKeySource keySource)
    {
        _keySource = keySource;
    }

    public GameInstallation? Installation { get; private set; }

    public IReadOnlyList<string> Errors => _errors;
    private readonly List<string> _errors = [];

    public void Open(GameInstallation installation, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        Dispose();

        progress?.Report("Lecture des clés du jeu...");
        var keys = _keySource.Load(installation, progress);

        string root = Path.GetFullPath(installation.FolderPath);
        var paths = Directory.EnumerateFiles(root, "*.rpf", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Where(p => !p.StartsWith("mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Opened in order (disk numbers), read on a few threads, merged in order: a later archive wins, as before.
        var opened = new (string Relative, int Disk, string? Error)[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            try
            {
                _disks.Add(File.OpenHandle(Path.Combine(root, paths[i]), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    FileOptions.RandomAccess));
                opened[i] = (paths[i], _disks.Count - 1, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                opened[i] = (paths[i], -1, $"{paths[i]} : {ex.Message}");
            }
        }

        var read = new (Dictionary<string, RpfEntry> Files, List<string> Archives, string? Error)[paths.Count];
        int done = 0;
        // Few threads: enough for an SSD, and a hard disk is not made to jump between too many places at once.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        Parallel.For(0, paths.Count, options, i =>
        {
            var (relative, disk, error) = opened[i];
            var files = new Dictionary<string, RpfEntry>(StringComparer.OrdinalIgnoreCase);
            var archives = new List<string>();
            if (error is null)
            {
                try
                {
                    var handle = _disks[disk];
                    long length = RandomAccess.GetLength(handle);
                    RpfArchive.Read(handle, disk, 0, Path.GetFileName(relative), (uint)length, relative, keys, files, archives);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A damaged (or badly modded) archive is skipped whole, as if it were absent.
                    error = $"{relative} : {ex.Message}";
                }
            }
            read[i] = (files, archives, error);
            int count = Interlocked.Increment(ref done);
            progress?.Report($"Lecture des archives ({count} / {paths.Count}) : {relative}");
        });

        foreach (var (files, archives, error) in read)
        {
            if (error is not null)
            {
                _errors.Add(error);
                continue;
            }
            foreach (var (path, entry) in files)
                _files[path] = entry;
            _archives.AddRange(archives);
        }

        _keys = keys;
        Installation = installation;
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public byte[]? ReadFile(string path)
    {
        if (!_files.TryGetValue(path, out var entry))
            return null;
        var keys = _keys ?? throw new InvalidOperationException("Call Open() before reading game files.");
        var handle = _disks[entry.Disk];

        if (entry.IsResource)
        {
            // Stored as its 16-byte "RSC7" header followed by the compressed pages.
            if (entry.StoredSize < 16)
                return [];
            var data = new byte[entry.StoredSize - 16];
            RpfArchive.ReadExactly(handle, data, entry.Offset + 16);
            ArchiveCipher.Decrypt(entry.Encryption, keys, data, entry.Name, entry.StoredSize);
            try
            {
                return Inflate(data, ResourcePages.Size(entry.SystemFlags) + ResourcePages.Size(entry.GraphicsFlags));
            }
            catch (InvalidDataException)
            {
                // A few resources of the game are not compressed data at all (damaged): returned as stored,
                // their reader will reject them.
                return data;
            }
        }

        bool compressed = entry.StoredSize != 0;
        var stored = new byte[compressed ? entry.StoredSize : entry.Size];
        RpfArchive.ReadExactly(handle, stored, entry.Offset);
        ArchiveCipher.Decrypt(entry.Encryption, keys, stored, entry.Name, entry.Size);
        return compressed ? Inflate(stored, (int)entry.Size) : stored;
    }

    public ResourceHeader? GetResourceHeader(string path) =>
        _files.TryGetValue(path, out var entry) && entry.IsResource
            ? new ResourceHeader(ResourcePages.Version(entry.SystemFlags, entry.GraphicsFlags), entry.SystemFlags, entry.GraphicsFlags)
            : null;

    public IEnumerable<string> EnumerateFiles(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        return _files.Keys.Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IEnumerable<string> EnumerateArchives() => _archives.ToList();

    public IReadOnlyCollection<string> AllFiles => _files.Keys;

    public ArchiveFileInfo? GetInfo(string path)
    {
        if (!_files.TryGetValue(path, out var e))
            return null;
        if (e.IsResource)
        {
            long size = (long)ResourcePages.Size(e.SystemFlags) + ResourcePages.Size(e.GraphicsFlags);
            return new ArchiveFileInfo(e.StoredSize, size, true, ResourcePages.Version(e.SystemFlags, e.GraphicsFlags),
                e.Encryption != ArchiveEncryption.None);
        }
        return new ArchiveFileInfo(e.StoredSize == 0 ? e.Size : e.StoredSize, e.Size, false, 0, e.Encryption != ArchiveEncryption.None);
    }

    public void Dispose()
    {
        foreach (var handle in _disks)
            handle.Dispose();
        _disks.Clear();
        _files.Clear();
        _archives.Clear();
        _errors.Clear();
        _keys = null;
        Installation = null;
    }

    private static byte[] Inflate(byte[] data, int expectedLength)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        // The expected length only sizes the buffer: capped, in case the flags are wrong (modded files), so that a few
        // reads at once cannot exhaust the memory of a small PC; the buffer grows if really needed.
        using var output = new MemoryStream(Math.Clamp(expectedLength, 0, 32 * 1024 * 1024));
        deflate.CopyTo(output);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }
}

// A file of an archive: bytes stored in the archive, size once decompressed, resource version, encryption.
public readonly record struct ArchiveFileInfo(long StoredSize, long Size, bool IsResource, int ResourceVersion, bool IsEncrypted);

// Page flags of a resource: the content is split into pages of a few sizes; each flag word counts them. The high 4
// bits of each word hold half of the format version.
internal static class ResourcePages
{
    public static int Version(uint systemFlags, uint graphicsFlags) =>
        (int)(((systemFlags >> 28) & 0xF) << 4 | ((graphicsFlags >> 28) & 0xF));

    // Total size of the pages described by one flag word: base size 0x200 shifted by the low 4 bits, then page counts
    // of 256, 128, 64, 32, 16, 8, 4, 2 and 1 times that size (the layout MetaDocument.PageFlags writes).
    public static int Size(uint flags)
    {
        long baseSize = 0x200L << (int)(flags & 0xF);
        long units =
            ((flags >> 4) & 0x1) * 256 +
            ((flags >> 5) & 0x3) * 128 +
            ((flags >> 7) & 0xF) * 64 +
            ((flags >> 11) & 0x3F) * 32 +
            ((flags >> 17) & 0x7F) * 16 +
            ((flags >> 24) & 0x1) * 8 +
            ((flags >> 25) & 0x1) * 4 +
            ((flags >> 26) & 0x1) * 2 +
            ((flags >> 27) & 0x1);
        return (int)Math.Min(baseSize * units, int.MaxValue);
    }
}
